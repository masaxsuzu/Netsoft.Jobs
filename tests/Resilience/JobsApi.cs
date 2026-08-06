using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Netsoft.Jobs.Resilience.Tests;

/// <summary>
/// 起動しているホストへ、画面と同じ HTTP の口から操作する。
/// </summary>
/// <remarks>
/// <para>
/// Contracts を参照せず、応答の JSON から必要な項目だけを読む。相手はプロセスの外に
/// 居るので、型を共有せず<b>外から見える形</b>だけで確かめる。型を共有すると、
/// 「送っている JSON が実は違う」種類の壊れ方が素通りする。
/// </para>
/// <para>
/// 状態を待つ口（<see cref="WaitForAsync"/>）は時間で仮定しない。並行して叩いている
/// 最中は状態がいくらでも動くので、「N 秒待てばこうなっているはず」は必ず嘘になる。
/// </para>
/// </remarks>
internal sealed class JobsApi : IDisposable
{
    private static readonly string[] TerminalStatuses = ["Completed", "Failed", "Cancelled"];

    private readonly JobsHost _host;
    private readonly HttpClient _client;

    public JobsApi(JobsHost host)
    {
        _host = host;
        _client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
    }

    public void Dispose() => _client.Dispose();

    public async Task<JobDto> RegisterAsync(string parameters, string name = "障害テスト")
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/jobs",
            new { name, jobType = "subtasks", parameters });

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JobDto>()
            ?? throw new InvalidOperationException($"登録の応答を読めません。\n{_host.Output}");
    }

    public Task<HttpResponseMessage> CancelAsync(string id) =>
        _client.PostAsync($"/api/jobs/{id}/cancel", content: null);

    public Task<HttpResponseMessage> PauseAsync(string id) =>
        _client.PostAsync($"/api/jobs/{id}/pause", content: null);

    public Task<HttpResponseMessage> ResumeAsync(string id) =>
        _client.PostAsync($"/api/jobs/{id}/resume", content: null);

    public Task<HttpResponseMessage> EditAsync(string id, string parameters) =>
        _client.PutAsJsonAsync($"/api/jobs/{id}/parameters", new { parameters });

    public async Task<IReadOnlyList<SubTaskDto>> SubTasksAsync(string id) =>
        await _client.GetFromJsonAsync<IReadOnlyList<SubTaskDto>>($"/api/jobs/{id}/subtasks") ?? [];

    /// <summary>いずれかの状態になるまで待つ。</summary>
    public Task<JobDto> WaitForAsync(string id, params string[] statuses) =>
        _host.PollAsync(
            async client => await client.GetFromJsonAsync<JobDto>($"/api/jobs/{id}") is { } job
                && statuses.Contains(job.Status)
                ? job
                : null,
            $"Job {id} が {string.Join(" か ", statuses)} になること");

    /// <summary>終端に達するまで待つ。</summary>
    public Task<JobDto> WaitForTerminalAsync(string id) => WaitForAsync(id, TerminalStatuses);

    public static bool IsTerminal(string status) => TerminalStatuses.Contains(status);

    internal sealed record JobDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("parameters")] string Parameters,
        [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAt,
        [property: JsonPropertyName("failureMessage")] string? FailureMessage);

    internal sealed record SubTaskDto(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("status")] string Status);
}
