namespace Netsoft.Jobs.Ui;

/// <summary>
/// API ホストの URL のクライアント側の写し。
/// </summary>
/// <remarks>
/// サーバ側の定義は Features の JobApiRoutes にあるが、UI は Features を参照しない
/// （実装を抱えないことが分離の目的）ので、値をここに書き写している。
/// 値がずれてもコンパイルでは気づけない。ずれると tests/Ui の結合テストが
/// 実際の API ホストへこの URL で届かず（404）割れるので、そこが検出網になる。
/// </remarks>
internal static class JobApiRoutes
{
    /// <summary>Job のコレクション。登録（POST）と一覧（GET）。</summary>
    public const string Jobs = "/api/jobs";

    /// <summary>Job の変更通知（SSE）。</summary>
    public const string JobEvents = $"{Jobs}/events";

    /// <summary>登録済みの Job の種類の一覧（GET）。</summary>
    public const string JobTypes = $"{Jobs}/types";

    /// <summary>指定した Job のキャンセル（POST）。</summary>
    public static string CancelFor(string id) => $"{Jobs}/{Uri.EscapeDataString(id)}/cancel";

    /// <summary>指定した Job の一時停止（POST）。</summary>
    public static string PauseFor(string id) => $"{Jobs}/{Uri.EscapeDataString(id)}/pause";

    /// <summary>指定した Job の再開（POST）。</summary>
    public static string ResumeFor(string id) => $"{Jobs}/{Uri.EscapeDataString(id)}/resume";

    /// <summary>指定した Job のパラメータの差し替え（PUT）。</summary>
    public static string ParametersFor(string id) => $"{Jobs}/{Uri.EscapeDataString(id)}/parameters";

    /// <summary>指定した Job のサブタスクの一覧（GET）。</summary>
    public static string SubTasksFor(string id) => $"{Jobs}/{Uri.EscapeDataString(id)}/subtasks";
}
