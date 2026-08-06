namespace Netsoft.Jobs.Resilience.Tests;

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
    private JobsApi Api => _api ?? throw new InvalidOperationException("初期化されていません。");

    private JobsHost Host => _host ?? throw new InvalidOperationException("初期化されていません。");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "netsoft-jobs-resilience", Path.GetRandomFileName());

    private JobsHost? _host;
    private JobsApi? _api;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _host = await JobsHost.StartAsync(Path.Combine(_directory, "jobs.db"));
        _api = new JobsApi(_host);
    }

    public async Task DisposeAsync()
    {
        _api?.Dispose();

        if (_host is not null)
        {
            SubTaskRows.ClearPool(_host.DatabasePath);
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
        // 1 サブタスクにつき 60 秒。殺すまでの間に走り切らせない。
        JobsApi.JobDto registered = await Api.RegisterAsync("3 60");
        await Api.WaitForAsync(registered.Id, "Running");

        // 停止要求ではなく電源断。走っている Job は結末を書けない。
        await Host.KillAsync();

        await Host.RestartAsync();

        JobsApi.JobDto recovered = await Api.WaitForAsync(registered.Id, "Failed");

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
        JobsApi.JobDto registered = await Api.RegisterAsync("3 60");
        await WaitForSubTaskStatusAsync(registered.Id, index: 0, "Running");

        await Host.KillAsync();
        await Host.RestartAsync();

        await Api.WaitForAsync(registered.Id, "Failed");

        Assert.Equal(
            ["Running", "Pending", "Pending"],
            await SubTaskRows.ReadAsync(Host.DatabasePath, registered.Id));
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
        // 1 つ目が長く占有している間に登録されたものは待機中のまま。
        JobsApi.JobDto running = await Api.RegisterAsync("1 60");
        await Api.WaitForAsync(running.Id, "Running");

        JobsApi.JobDto queued = await Api.RegisterAsync("1 1");
        await Api.WaitForAsync(queued.Id, "Queued");

        await Host.KillAsync();
        await Host.RestartAsync();

        // 残骸は閉じられ、待機中だったものはそのまま走って完了する。
        await Api.WaitForAsync(running.Id, "Failed");
        await Api.WaitForAsync(queued.Id, "Completed");
    }

    private Task<string> WaitForSubTaskStatusAsync(string id, int index, string status) =>
        Host.PollAsync(
            async _ => (await SubTaskRows.ReadAsync(Host.DatabasePath, id)) is { } rows
                && rows.Count > index && rows[index] == status
                ? status
                : null,
            $"Job {id} の {index} 番目が {status} になること");
}
