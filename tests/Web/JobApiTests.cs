using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// HTTP API の結合テスト。実際のホストとエンドポイントの結線を通して確かめる。
/// </summary>
/// <remarks>
/// テストごとにホストと DB を作り直す（xUnit はテストごとにクラスを生成する）。
/// 共有すると、登録した Job が他のテストの一覧に混ざって前提が崩れる。
/// 実行エンジンはファクトリが止めているので、登録した Job は Registered のまま動かない。
/// </remarks>
public sealed class JobApiTests : IDisposable
{
    private readonly JobsWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public JobApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task 登録すると201が返りLocationの先で取得できる()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/jobs",
            new { name = "夜間バッチ", jobType = "subtasks", parameters = "3 1" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Uri? location = response.Headers.Location;
        Assert.NotNull(location);

        JobDto? job = await _client.GetFromJsonAsync<JobDto>(location);
        Assert.NotNull(job);
        Assert.Equal("夜間バッチ", job.Name);
        Assert.Equal("subtasks", job.JobType);
        Assert.Equal("3 1", job.Parameters);
        Assert.Equal("Registered", job.Status);
    }

    [Fact]
    public async Task 不正な登録は400で項目ごとのエラーが返る()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/jobs",
            new { name = "", jobType = "", parameters = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // ValidationProblem（RFC 9457）の errors に、入力欄と対応する項目名で入っていること。
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement errors = body.RootElement.GetProperty("errors");

        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("jobType", out _));
        Assert.True(errors.TryGetProperty("parameters", out _));
    }

    [Fact]
    public async Task 一覧は登録したJobを返す()
    {
        await RegisterAsync("先の Job");
        await RegisterAsync("後の Job");

        IReadOnlyList<JobDto>? jobs = await _client.GetFromJsonAsync<IReadOnlyList<JobDto>>("/api/jobs");

        Assert.NotNull(jobs);
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, job => job.Name == "先の Job");
        Assert.Contains(jobs, job => job.Name == "後の Job");
    }

    /// <summary>
    /// 画面の選択肢はこの応答だけで作られる。種類が実際に登録されているハンドラ
    /// （subtasks が唯一）と一致することを保証する。
    /// </summary>
    [Fact]
    public async Task 種類の一覧は登録済みのハンドラを名前順のオブジェクトで返す()
    {
        IReadOnlyList<JobTypeDto>? jobTypes =
            await _client.GetFromJsonAsync<IReadOnlyList<JobTypeDto>>("/api/jobs/types");

        Assert.NotNull(jobTypes);
        Assert.Equal([new JobTypeDto("subtasks")], jobTypes);
    }

    /// <summary>
    /// 応答の要素が文字列ではなくオブジェクト（jobType 1 項目）であること。
    /// 型付きの読み出しでは "demo" と {"jobType":"demo"} の違いが見えないので、
    /// 生の JSON で形そのものを見る。将来項目を足すときに応答の形を変えずに済む契約。
    /// </summary>
    [Fact]
    public async Task 種類の一覧の要素はjobTypeを持つオブジェクト()
    {
        using JsonDocument body = JsonDocument.Parse(await _client.GetStringAsync("/api/jobs/types"));

        JsonElement first = body.RootElement.EnumerateArray().First();
        Assert.Equal(JsonValueKind.Object, first.ValueKind);
        Assert.Equal("subtasks", first.GetProperty("jobType").GetString());
    }

    [Fact]
    public async Task 存在しないJobの取得は404()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/jobs/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 待機中のJobは200でキャンセルできCancelledになる()
    {
        JobDto registered = await RegisterAsync("止めたい Job");

        HttpResponseMessage response = await _client.PostAsync($"/api/jobs/{registered.Id}/cancel", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 応答だけでなく、取得し直しても Cancelled が保存されていること。
        JobDto? job = await _client.GetFromJsonAsync<JobDto>($"/api/jobs/{registered.Id}");
        Assert.NotNull(job);
        Assert.Equal("Cancelled", job.Status);
    }

    [Fact]
    public async Task 存在しないJobのキャンセルは404()
    {
        HttpResponseMessage response = await _client.PostAsync("/api/jobs/does-not-exist/cancel", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 終端のJobへのキャンセルは409()
    {
        JobDto registered = await RegisterAsync("既に終わる Job");

        // エンジンは止まっているので、待機中へのキャンセルで即座に終端（Cancelled）へ落とせる。
        HttpResponseMessage first = await _client.PostAsync($"/api/jobs/{registered.Id}/cancel", content: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        HttpResponseMessage second = await _client.PostAsync($"/api/jobs/{registered.Id}/cancel", content: null);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // 409 でも現在の Job が本文に載る契約（呼び出し側が読み直さずに済む）。
        JobDto? job = await second.Content.ReadFromJsonAsync<JobDto>();
        Assert.NotNull(job);
        Assert.Equal("Cancelled", job.Status);
    }

    [Fact]
    public void DIコンテナはValidateOnBuild付きで解決できる()
    {
        // ファクトリが ValidateOnBuild / ValidateScopes を有効にしてホストを組んでいる。
        // Services に触れた時点でコンテナが構築されるので、登録漏れがあればここで落ちる。
        //
        // 解決するのはエンジンではなくファクトリ。エンジンは起動時復旧を済ませてからでないと
        // 手に入らず、生成に await が要るので DI には載せられない
        // （JobExecutionEngineFactory の注記を参照）。エンジンの依存が揃っているかは、
        // ファクトリが同じものを受け取っているのでここで一緒に検証される。
        Assert.NotNull(_factory.Services.GetRequiredService<JobExecutionEngineFactory>());
    }

    private async Task<JobDto> RegisterAsync(string name)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/jobs",
            new { name, jobType = "subtasks", parameters = "1 1" });

        response.EnsureSuccessStatusCode();

        JobDto? job = await response.Content.ReadFromJsonAsync<JobDto>();
        Assert.NotNull(job);
        return job;
    }
}
