using System.Net;
using System.Net.Http.Json;

using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Ui;

/// <summary>
/// API ホスト（Web）を呼ぶ型付きクライアント。画面が Job を操作する唯一の経路。
/// </summary>
/// <remarks>
/// <para>
/// 画面（Blazor）はかつて同一プロセスのハンドラを直接呼んでいたが、UI を別プロセスへ
/// 分離したので HTTP を経由する。HTTP の形（ステータスコードと本文）をここで
/// 画面が扱える結果型へ写し、画面には HTTP を見せない。
/// </para>
/// <para>
/// 契約どおりの失敗（400 / 404 / 409）は結果型で返し、それ以外（5xx など）は
/// 例外にする。前者は画面が表示に写す正常な分岐で、後者は復旧のしようがない異常。
/// </para>
/// </remarks>
public sealed class JobsApiClient
{
    /// <summary>
    /// DI（AddHttpClient）に登録する名前。テストがこの名前で
    /// HttpMessageHandler を差し替えて、実 HTTP なしで API ホストへ繋ぐ。
    /// </summary>
    public const string HttpClientName = "JobsApi";

    private readonly HttpClient _client;

    public JobsApiClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
    }

    /// <summary>すべての Job を取得する。</summary>
    public async Task<IReadOnlyList<JobDto>> ListJobsAsync(CancellationToken cancellationToken) =>
        await _client.GetFromJsonAsync<IReadOnlyList<JobDto>>(JobApiRoutes.Jobs, cancellationToken) ?? [];

    /// <summary>
    /// 登録されている Job の種類を取得する。並び順はサーバが決めた順（名前順）のまま。
    /// </summary>
    /// <remarks>
    /// 応答が「何を意味するか」だけを写して返す。どう説明して見せるかは画面の関心で、
    /// ここで文言を足したり並べ替えたりしない。
    /// </remarks>
    public async Task<IReadOnlyList<JobTypeDto>> ListJobTypesAsync(CancellationToken cancellationToken) =>
        await _client.GetFromJsonAsync<IReadOnlyList<JobTypeDto>>(JobApiRoutes.JobTypes, cancellationToken) ?? [];

    /// <summary>Job を登録する。検証エラー（400）は結果型の Errors で返す。</summary>
    public async Task<RegisterJobResponse> RegisterJobAsync(
        string name, string jobType, string parameters, CancellationToken cancellationToken)
    {
        // 本文の形は Features の RegisterJobCommand と一致していなければならないが、
        // 型は共有できない（UI は Features を参照しない）。ずれたら tests/Ui の
        // 結合テストが割れる（JobApiRoutes の注記と同じ検出網）。
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            JobApiRoutes.Jobs, new { name, jobType, parameters }, cancellationToken);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            HttpValidationProblemDetails? problem =
                await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(cancellationToken);
            return RegisterJobResponse.Invalid(problem?.Errors ?? new Dictionary<string, string[]>());
        }

        response.EnsureSuccessStatusCode();

        return RegisterJobResponse.Registered(await ReadJobAsync(response, cancellationToken));
    }

    /// <summary>Job のキャンセルを要求する。404 と 409 は結果型で返す。</summary>
    public async Task<CancelJobResponse> CancelJobAsync(string id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.PostAsync(
            JobApiRoutes.CancelFor(id), content: null, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return CancelJobResponse.NotFound();
        }

        // 409 は「ボタンを出した後に状態が進んだ」競合で、本文に現在の Job が載る契約。
        // 読み直しの往復をせずに、画面へ「今どうなっているのか」を渡せる。
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return CancelJobResponse.Rejected(await ReadJobAsync(response, cancellationToken));
        }

        response.EnsureSuccessStatusCode();

        return CancelJobResponse.Accepted(await ReadJobAsync(response, cancellationToken));
    }

    /// <summary>Job の一時停止を要求する。404 と 409 は結果型で返す。</summary>
    public Task<JobControlResponse> PauseJobAsync(string id, CancellationToken cancellationToken) =>
        ControlAsync(JobApiRoutes.PauseFor(id), cancellationToken);

    /// <summary>Job の再開を要求する。404 と 409 は結果型で返す。</summary>
    public Task<JobControlResponse> ResumeJobAsync(string id, CancellationToken cancellationToken) =>
        ControlAsync(JobApiRoutes.ResumeFor(id), cancellationToken);

    /// <summary>Job のパラメータを差し替える。400 / 404 / 409 は結果型で返す。</summary>
    public async Task<EditJobResponse> EditJobParametersAsync(
        string id, string parameters, CancellationToken cancellationToken)
    {
        // 本文の形はサーバ（EditJobEndpoint の要求型）の写し。ずれたら tests/Ui の結合テストが割れる。
        using HttpResponseMessage response = await _client.PutAsJsonAsync(
            JobApiRoutes.ParametersFor(id), new { parameters }, cancellationToken);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            HttpValidationProblemDetails? problem =
                await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(cancellationToken);
            return EditJobResponse.Invalid(problem?.Errors ?? new Dictionary<string, string[]>());
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return EditJobResponse.NotFound();
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return EditJobResponse.Rejected(await ReadJobAsync(response, cancellationToken));
        }

        response.EnsureSuccessStatusCode();

        return EditJobResponse.Accepted(await ReadJobAsync(response, cancellationToken));
    }

    /// <summary>
    /// Job のサブタスクを連番順で取得する。Job が無い（404）場合は null、
    /// まだ行が無い場合は空（登録直後の正常な姿）。
    /// </summary>
    public async Task<IReadOnlyList<SubTaskDto>?> ListSubTasksAsync(string id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            JobApiRoutes.SubTasksFor(id), cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<IReadOnlyList<SubTaskDto>>(cancellationToken) ?? [];
    }

    private async Task<JobControlResponse> ControlAsync(string route, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.PostAsync(route, content: null, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return JobControlResponse.NotFound();
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return JobControlResponse.Rejected(await ReadJobAsync(response, cancellationToken));
        }

        response.EnsureSuccessStatusCode();

        return JobControlResponse.Accepted(await ReadJobAsync(response, cancellationToken));
    }

    private static async Task<JobDto> ReadJobAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<JobDto>(cancellationToken)
            ?? throw new InvalidOperationException("API の応答に Job が入っていません。");
}
