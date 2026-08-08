using System.Net.Http.Json;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// 監査ログの HTTP API の結合テスト。
/// </summary>
/// <remarks>
/// <para>
/// <b>ここでしか確かめられないのは「利用者の操作 1 つに対して監査ログが 1 つ」。</b>
/// ハンドラ単体では材料が 1 つ作られることまでしか見えず、それが実際に 1 回だけ
/// 書かれるかはエンドポイントの結線に乗っている。件数を数えるのはこの層の仕事。
/// </para>
/// <para>
/// エンジンは止まっているので、ここに出てくる監査ログはすべて利用者の操作の分。
/// システムのアクションが混ざらないぶん、件数が読みやすい。
/// </para>
/// </remarks>
public sealed class AuditLogApiTests : IDisposable
{
    private readonly JobsWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public AuditLogApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    /// <summary>
    /// 通った操作も、拒否された操作も、1 回につき 1 件。
    /// </summary>
    /// <remarks>
    /// 拒否を数に入れるのが肝。<b>押したのに記録が無いと、後から読む人には
    /// 「押していない」と区別が付かない</b>。エラーの有無で分けるのは
    /// <c>Error</c> 列であって、件数ではない。
    /// </remarks>
    [Fact]
    public async Task 操作1回につき監査ログが1件増える()
    {
        JobDto job = await RegisterAsync();

        // 登録そのものが 1 件目。
        Assert.Single(await LogsForAsync(job.Id));

        // 待ち行列の Job は一時停止できない（拒否）。それでも 1 件増える。
        await _client.PostAsync($"/api/jobs/{job.Id}/pause", content: null);
        Assert.Equal(2, (await LogsForAsync(job.Id)).Count);

        await _client.PutAsJsonAsync($"/api/jobs/{job.Id}/parameters", new { parameters = "5 2" });
        Assert.Equal(3, (await LogsForAsync(job.Id)).Count);

        await _client.PostAsync($"/api/jobs/{job.Id}/cancel", content: null);
        Assert.Equal(4, (await LogsForAsync(job.Id)).Count);
    }

    /// <summary>
    /// 実施内容は過去形で、パラメータを含む。エラーは通らなかったときだけ載る。
    /// </summary>
    [Fact]
    public async Task 実施内容は過去形でパラメータを含みエラーは失敗時だけ載る()
    {
        JobDto job = await RegisterAsync();

        await _client.PutAsJsonAsync($"/api/jobs/{job.Id}/parameters", new { parameters = "5 2" });
        await _client.PostAsync($"/api/jobs/{job.Id}/pause", content: null);

        IReadOnlyList<AuditLogDto> logs = await LogsForAsync(job.Id);

        Assert.Equal("Job を登録した（名前=監査される Job, 種類=subtasks, パラメータ=3 1）", logs[0].Content);
        Assert.Null(logs[0].Error);

        Assert.Equal("パラメータを変更した（変更前=3 1, 変更後=5 2）", logs[1].Content);
        Assert.Null(logs[1].Error);

        Assert.Equal("一時停止を要求した", logs[2].Content);
        Assert.Equal("現在の状態（Registered）では一時停止できません。", logs[2].Error);

        // 実施者はすべて利用者。エンジンは止めてあるのでシステムの分は出てこない。
        Assert.All(logs, log => Assert.Equal("User", log.Actor));
        Assert.All(logs, log => Assert.Equal(job.Id, log.JobId));
    }

    /// <summary>
    /// Job 単位は古い順。1 つの Job の記録は物語として読まれる。
    /// </summary>
    [Fact]
    public async Task Job単位の監査ログは古い順に並ぶ()
    {
        JobDto job = await RegisterAsync();
        await _client.PostAsync($"/api/jobs/{job.Id}/cancel", content: null);

        IReadOnlyList<AuditLogDto> logs = await LogsForAsync(job.Id);

        Assert.Equal(["Job を登録した（名前=監査される Job, 種類=subtasks, パラメータ=3 1）", "キャンセルを要求した"],
            logs.Select(log => log.Content));
    }

    /// <summary>
    /// 全件は新しい順。こちらは「直近に何が起きたか」を見るためのもの。
    /// </summary>
    [Fact]
    public async Task 全件の監査ログは新しい順に並ぶ()
    {
        JobDto job = await RegisterAsync();
        await _client.PostAsync($"/api/jobs/{job.Id}/cancel", content: null);

        IReadOnlyList<AuditLogDto> logs = await AllLogsAsync();

        Assert.Equal("キャンセルを要求した", logs[0].Content);
    }

    /// <summary>
    /// Job に紐づかない実施も記録される。画面には出さないが、全件からは読める。
    /// </summary>
    [Fact]
    public async Task Jobに紐づかない実施も全件には出る()
    {
        // 存在しない Id への操作。Job が無いので紐づけ先が無い。
        await _client.PostAsync("/api/jobs/居ない/cancel", content: null);

        AuditLogDto log = (await AllLogsAsync())[0];

        Assert.Null(log.JobId);
        Assert.Equal("キャンセルを要求した", log.Content);
        Assert.Equal("対象の Job が見つかりませんでした。", log.Error);
    }

    /// <summary>
    /// 入力で弾かれた登録も 1 件。Job は出来ていないので紐づかない。
    /// </summary>
    [Fact]
    public async Task 弾かれた登録も記録される()
    {
        await _client.PostAsJsonAsync("/api/jobs", new { name = "", jobType = "", parameters = "1 1" });

        AuditLogDto log = (await AllLogsAsync())[0];

        Assert.Null(log.JobId);
        Assert.Equal("Job を登録した（名前=, 種類=, パラメータ=1 1）", log.Content);
        Assert.NotNull(log.Error);
    }

    /// <summary>
    /// <b>コマンドの口はひとつ残らず監査される。</b>
    /// </summary>
    /// <remarks>
    /// 個別のケースを並べず、登録されたエンドポイントを総当たりで数える。個別に書くと、
    /// 新しいコマンドを足した人がテストを書き足さない限り監査が欠けたまま通ってしまい、
    /// <b>監査が欠けたことは監査ログからは分からない</b>（無い記録は探せない）。
    /// <para>
    /// 状態を変える口の目印は「GET でないこと」。読み出しは操作ではないので監査しない。
    /// </para>
    /// </remarks>
    [Fact]
    public void コマンドのエンドポイントはすべて監査の口を通る()
    {
        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        IReadOnlyList<RouteEndpoint> commands =
        [
            .. endpoints.Endpoints
                .OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.Metadata
                    .GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()
                    ?.HttpMethods.Any(method => method is not "GET") == true)
        ];

        Assert.NotEmpty(commands);

        // 監査を通すコマンドの名前。足したのに書き忘れたら、下の突き合わせで落ちる。
        string[] audited =
        [
            "RegisterJob",
            "CancelJob",
            "PauseJob",
            "ResumeJob",
            "EditJobParameters",
        ];

        Assert.Equal(
            [.. audited.Order(StringComparer.Ordinal)],
            [.. commands.Select(endpoint => endpoint.DisplayName is { } name
                ? endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.EndpointNameMetadata>()?.EndpointName ?? name
                : string.Empty).Order(StringComparer.Ordinal)]);
    }

    private async Task<JobDto> RegisterAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/jobs",
            new { name = "監査される Job", jobType = "subtasks", parameters = "3 1" });

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JobDto>()
            ?? throw new InvalidOperationException("登録の応答が空でした。");
    }

    private async Task<IReadOnlyList<AuditLogDto>> LogsForAsync(string id) =>
        await _client.GetFromJsonAsync<IReadOnlyList<AuditLogDto>>($"/api/jobs/{id}/audit-logs")
        ?? throw new InvalidOperationException("監査ログの応答が空でした。");

    private async Task<IReadOnlyList<AuditLogDto>> AllLogsAsync() =>
        await _client.GetFromJsonAsync<IReadOnlyList<AuditLogDto>>("/api/audit-logs")
        ?? throw new InvalidOperationException("監査ログの応答が空でした。");
}
