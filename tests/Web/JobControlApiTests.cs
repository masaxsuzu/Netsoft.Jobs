using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// 一時停止・再開・編集・サブタスク読み出しの HTTP API の結合テスト。
/// </summary>
/// <remarks>
/// エンジンは止まっているので、Running などの途中状態は store を直接進めて作る
/// （実行そのものは Features の結合テストの領分。ここで見るのは HTTP への写し方）。
/// </remarks>
public sealed class JobControlApiTests : IDisposable
{
    private readonly JobsWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public JobControlApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task 実行中のJobは200で一時停止できPausingになる()
    {
        JobDto registered = await RegisterAsync();
        await AdvanceAsync(registered.Id, JobTrigger.Start);

        HttpResponseMessage response = await _client.PostAsync($"/api/jobs/{registered.Id}/pause", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JobDto? job = await response.Content.ReadFromJsonAsync<JobDto>();
        Assert.Equal("Pausing", job?.Status);
    }

    /// <summary>
    /// 登録直後でも保留できる。走る前に止めたい場面（順番を入れ替えたい、準備が整っていない）は
    /// 普通にあり、そこで「消す」しか手が無いのは筋が悪い。
    /// </summary>
    [Fact]
    public async Task 登録直後のJobは200で保留でき再開すると再開待ちへ戻る()
    {
        JobDto registered = await RegisterAsync();

        // 走り出す前は受理を待つ相手がいないので、Pausing を経ずに直接 Paused。
        HttpResponseMessage paused = await _client.PostAsync($"/api/jobs/{registered.Id}/pause", content: null);

        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.Equal("Paused", (await paused.Content.ReadFromJsonAsync<JobDto>())?.Status);

        // 消さずに保留できることに意味があるので、戻せるところまで見る。
        // 一度も走っていなくても、戻り先は Registered ではなく Resumed。
        HttpResponseMessage resumed = await _client.PostAsync($"/api/jobs/{registered.Id}/resume", content: null);

        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal("Resumed", (await resumed.Content.ReadFromJsonAsync<JobDto>())?.Status);
    }

    [Fact]
    public async Task 再開待ちのJobも200で保留できる()
    {
        JobDto registered = await RegisterAsync();
        await AdvanceAsync(
            registered.Id, JobTrigger.Start, JobTrigger.RequestPause, JobTrigger.ConfirmPaused, JobTrigger.Resume);

        HttpResponseMessage paused = await _client.PostAsync($"/api/jobs/{registered.Id}/pause", content: null);

        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.Equal("Paused", (await paused.Content.ReadFromJsonAsync<JobDto>())?.Status);
    }

    [Fact]
    public async Task 停止中のJobは200で再開でき待ち行列へ戻る()
    {
        JobDto registered = await RegisterAsync();
        await AdvanceAsync(registered.Id, JobTrigger.Start, JobTrigger.RequestPause, JobTrigger.ConfirmPaused);

        HttpResponseMessage response = await _client.PostAsync($"/api/jobs/{registered.Id}/resume", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JobDto? job = await response.Content.ReadFromJsonAsync<JobDto>();
        Assert.Equal("Resumed", job?.Status);
    }

    [Fact]
    public async Task 存在しないJobの一時停止と再開は404()
    {
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _client.PostAsync("/api/jobs/does-not-exist/pause", content: null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _client.PostAsync("/api/jobs/does-not-exist/resume", content: null)).StatusCode);
    }

    [Fact]
    public async Task パラメータは200で差し替えられ取得し直しても残る()
    {
        JobDto registered = await RegisterAsync();

        HttpResponseMessage response = await _client.PutAsJsonAsync(
            $"/api/jobs/{registered.Id}/parameters", new { parameters = "5 2" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JobDto? job = await _client.GetFromJsonAsync<JobDto>($"/api/jobs/{registered.Id}");
        Assert.Equal("5 2", job?.Parameters);
    }

    [Fact]
    public async Task 読めないパラメータは400で項目別のエラーが返る()
    {
        JobDto registered = await RegisterAsync();

        HttpResponseMessage response = await _client.PutAsJsonAsync(
            $"/api/jobs/{registered.Id}/parameters", new { parameters = "junk" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // 登録と同じ ValidationProblem（RFC 9457）の形で、項目名 parameters に載る。
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("parameters", out _));
    }

    [Fact]
    public async Task 終端のJobの編集は409()
    {
        JobDto registered = await RegisterAsync();
        await AdvanceAsync(registered.Id, JobTrigger.RequestCancel);

        HttpResponseMessage response = await _client.PutAsJsonAsync(
            $"/api/jobs/{registered.Id}/parameters", new { parameters = "5 2" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        JobDto? job = await response.Content.ReadFromJsonAsync<JobDto>();
        Assert.Equal("Cancelled", job?.Status);
    }

    /// <summary>
    /// 一覧の応答が進捗を運ぶこと。画面はこれを見て行ごとの取得をやめているので、
    /// 落ちると N+1 が黙って戻る（表示は「-」のままになって気づきにくい）。
    /// </summary>
    [Fact]
    public async Task 一覧の応答は各Jobの進捗を含む()
    {
        JobDto registered = await RegisterAsync();

        IReadOnlyList<JobListItemDto>? beforeRows =
            await _client.GetFromJsonAsync<IReadOnlyList<JobListItemDto>>("/api/jobs");
        JobListItemDto before = Assert.Single(beforeRows!);

        // 行が作られる前は 0/0。「進捗ゼロ」ではなく「まだ分割されていない」。
        Assert.Equal(0, before.TotalSubTasks);

        ISubTaskStore subTasks = _factory.Services.GetRequiredService<ISubTaskStore>();
        SubTask first = SubTask.Create(JobId.From(registered.Id), 0);
        await subTasks.AddRangeAsync([first, SubTask.Create(JobId.From(registered.Id), 1)], CancellationToken.None);
        SubTaskTransition started = first.Apply(SubTaskTrigger.Start);
        Assert.True(await subTasks.UpdateAsync(first, started.Previous, CancellationToken.None));
        SubTaskTransition completed = first.Apply(SubTaskTrigger.Complete);
        Assert.True(await subTasks.UpdateAsync(first, completed.Previous, CancellationToken.None));

        IReadOnlyList<JobListItemDto>? afterRows =
            await _client.GetFromJsonAsync<IReadOnlyList<JobListItemDto>>("/api/jobs");
        JobListItemDto after = Assert.Single(afterRows!);

        Assert.Equal(1, after.CompletedSubTasks);
        Assert.Equal(2, after.TotalSubTasks);
    }

    private async Task<JobDto> RegisterAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/jobs",
            new { name = "操作対象", jobType = "subtasks", parameters = "3 1" });

        response.EnsureSuccessStatusCode();

        JobDto? job = await response.Content.ReadFromJsonAsync<JobDto>();
        Assert.NotNull(job);
        return job;
    }

    /// <summary>
    /// store を直接進めて途中状態を作る。エンジンは止まっているので、これが唯一の道。
    /// </summary>
    private async Task AdvanceAsync(string id, params JobTrigger[] triggers)
    {
        IJobStore store = _factory.Services.GetRequiredService<IJobStore>();
        DateTimeOffset now = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

        foreach (JobTrigger trigger in triggers)
        {
            Job job = await store.FindAsync(JobId.From(id), CancellationToken.None)
                ?? throw new InvalidOperationException($"Job {id} が保存されていません。");
            now = now.AddMinutes(1);

            JobTransitionResult result = job.Apply(trigger, now);
            Assert.True(result.IsAllowed);
            Assert.True(await store.UpdateAsync(job, CancellationToken.None));
        }
    }
}
