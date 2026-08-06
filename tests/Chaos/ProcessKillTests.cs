using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Netsoft.Jobs.Chaos.Tests;

/// <summary>
/// 走っている最中にプロセスが消えたとき、次の起動で何が起きるか。
/// </summary>
/// <remarks>
/// <para>
/// 起動時復旧はこの状況のためだけにある。ところがこれまで、その状況を<b>実際に作った</b>
/// テストは無かった（既存のテストは残骸に見える行を DB へ書いて復旧を呼んでいる）。
/// 電源が落ちた瞬間に本当にその形の行が残るのか、残った行を本当に閉じられるのかは、
/// プロセスを殺してみないと分からない。
/// </para>
/// <para>
/// DB とポートはインスタンスごとに分け、殺すのは自分が起こしたプロセスだけなので、
/// 他のテストと並行に走ってよい。
/// </para>
/// </remarks>
public sealed class ProcessKillTests : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "netsoft-jobs-chaos", Path.GetRandomFileName());

    private JobsHost? _host;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗でテストの結果を変えない（docs/build.md「テストの後始末」）。
        }
    }

    [Fact]
    public async Task 実行中に強制終了されたJobは次の起動でFailedとして閉じられる()
    {
        JobsHost host = await StartAsync();

        // 1 サブタスクにつき 60 秒。殺すまでの間に走り切らせない。
        JobDto registered = await RegisterAsync(host, "3 60");
        await WaitForStatusAsync(host, registered.Id, "Running");

        // 停止要求ではなく電源断。走っている Job は結末を書けない。
        await host.KillAsync();

        await host.RestartAsync();

        JobDto recovered = await WaitForStatusAsync(host, registered.Id, "Failed");

        Assert.NotNull(recovered.FinishedAt);
        Assert.Contains("異常終了", recovered.FailureMessage);
    }

    /// <summary>
    /// 強制終了で残ったサブタスクの行は、中断点の記録としてそのまま残る。
    /// </summary>
    /// <remarks>
    /// Job は終端（Failed）なのに行が非終端、という食い違いが「ここで止まった」の記録になる。
    /// 復旧が行まで畳んでしまうと、どこまで進んでいたかが失われる。
    /// </remarks>
    [Fact]
    public async Task 強制終了で残ったサブタスクの行は中断点として残る()
    {
        JobsHost host = await StartAsync();

        JobDto registered = await RegisterAsync(host, "3 60");
        await WaitForSubTaskStatusAsync(host, registered.Id, index: 0, "Running");

        await host.KillAsync();
        await host.RestartAsync();

        await WaitForStatusAsync(host, registered.Id, "Failed");

        IReadOnlyList<SubTaskDto> subTasks = await host.PollAsync(
            client => client.GetFromJsonAsync<IReadOnlyList<SubTaskDto>>($"/api/jobs/{registered.Id}/subtasks"),
            "サブタスクの取得");

        Assert.Equal(["Running", "Pending", "Pending"], subTasks.Select(subTask => subTask.Status));
    }

    /// <summary>
    /// 待機中のまま強制終了された Job は、次の起動でそのまま実行される。
    /// </summary>
    /// <remarks>
    /// 復旧が閉じるのはハンドラが動いていたはずの状態だけ。Queued まで巻き込むと、
    /// まだ 1 度も走っていない仕事が「異常終了しました」で捨てられる。
    /// </remarks>
    [Fact]
    public async Task 待機中のまま強制終了されたJobは次の起動で実行される()
    {
        JobsHost host = await StartAsync();

        // 1 つ目が長く占有している間に登録されたものは待機中のまま。
        JobDto running = await RegisterAsync(host, "1 60");
        await WaitForStatusAsync(host, running.Id, "Running");

        JobDto queued = await RegisterAsync(host, "1 1");
        await WaitForStatusAsync(host, queued.Id, "Queued");

        await host.KillAsync();
        await host.RestartAsync();

        // 残骸は閉じられ、待機中だったものはそのまま走って完了する。
        await WaitForStatusAsync(host, running.Id, "Failed");
        await WaitForStatusAsync(host, queued.Id, "Completed");
    }

    private async Task<JobsHost> StartAsync()
    {
        _host = await JobsHost.StartAsync(Path.Combine(_directory, "jobs.db"));
        return _host;
    }

    private static async Task<JobDto> RegisterAsync(JobsHost host, string parameters)
    {
        using HttpClient client = new() { BaseAddress = new Uri(host.BaseUrl) };

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/jobs",
            new { name = "障害テスト", jobType = "subtasks", parameters });

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JobDto>()
            ?? throw new InvalidOperationException($"登録の応答を読めません。\n{host.Output}");
    }

    private static Task<JobDto> WaitForStatusAsync(JobsHost host, string id, string status) =>
        host.PollAsync(
            async client => await client.GetFromJsonAsync<JobDto>($"/api/jobs/{id}") is { } job && job.Status == status
                ? job
                : null,
            $"Job {id} が {status} になること");

    private static Task<SubTaskDto> WaitForSubTaskStatusAsync(JobsHost host, string id, int index, string status) =>
        host.PollAsync(
            async client =>
            {
                IReadOnlyList<SubTaskDto>? subTasks =
                    await client.GetFromJsonAsync<IReadOnlyList<SubTaskDto>>($"/api/jobs/{id}/subtasks");

                return subTasks?.FirstOrDefault(subTask => subTask.Index == index && subTask.Status == status);
            },
            $"Job {id} の {index} 番目が {status} になること");

    /// <remarks>
    /// Contracts を参照せず、応答の JSON から必要な項目だけを読む。プロセスをまたぐ
    /// 相手なので、型を共有せず<b>外から見える形</b>で確かめる。
    /// </remarks>
    private sealed record JobDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAt,
        [property: JsonPropertyName("failureMessage")] string? FailureMessage);

    private sealed record SubTaskDto(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("status")] string Status);
}
