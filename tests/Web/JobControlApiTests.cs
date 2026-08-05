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

    [Fact]
    public async Task 待機中のJobへの一時停止は409で現在のJobが返る()
    {
        JobDto registered = await RegisterAsync();

        HttpResponseMessage response = await _client.PostAsync($"/api/jobs/{registered.Id}/pause", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        JobDto? job = await response.Content.ReadFromJsonAsync<JobDto>();
        Assert.Equal("Queued", job?.Status);
    }

    [Fact]
    public async Task 停止中のJobは200で再開できQueuedへ戻る()
    {
        JobDto registered = await RegisterAsync();
        await AdvanceAsync(registered.Id, JobTrigger.Start, JobTrigger.RequestPause, JobTrigger.ConfirmPaused);

        HttpResponseMessage response = await _client.PostAsync($"/api/jobs/{registered.Id}/resume", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JobDto? job = await response.Content.ReadFromJsonAsync<JobDto>();
        Assert.Equal("Queued", job?.Status);
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

    [Fact]
    public async Task サブタスクは連番順で返り存在しないJobは404()
    {
        JobDto registered = await RegisterAsync();

        // 登録直後は行が無い（実行の開始時に作られる）。空の 200 が正常な姿。
        IReadOnlyList<SubTaskDto>? empty =
            await _client.GetFromJsonAsync<IReadOnlyList<SubTaskDto>>($"/api/jobs/{registered.Id}/subtasks");
        Assert.NotNull(empty);
        Assert.Empty(empty);

        ISubTaskStore subTasks = _factory.Services.GetRequiredService<ISubTaskStore>();
        await subTasks.AddRangeAsync(
            [SubTask.Create(JobId.From(registered.Id), 0), SubTask.Create(JobId.From(registered.Id), 1)],
            CancellationToken.None);

        IReadOnlyList<SubTaskDto>? listed =
            await _client.GetFromJsonAsync<IReadOnlyList<SubTaskDto>>($"/api/jobs/{registered.Id}/subtasks");
        Assert.Equal([new SubTaskDto(0, "Pending"), new SubTaskDto(1, "Pending")], listed);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _client.GetAsync("/api/jobs/does-not-exist/subtasks")).StatusCode);
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
            Assert.True(await store.UpdateAsync(job, result.Previous, CancellationToken.None));
        }
    }
}
