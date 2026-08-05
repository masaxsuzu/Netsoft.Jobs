using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Contracts;

/// <summary>
/// 画面が一時停止・再開のボタンを押せる状態にするかの判定。判断は Domain の状態機械に委ねる
/// （理由は <see cref="JobCancelability"/> と同じ。状態名をここに並べない。
/// 状態の文字列を受け取るのも同じ理由）。
/// </summary>
public static class JobPausability
{
    /// <summary>この状態の Job に一時停止を要求できるか。</summary>
    public static bool CanRequestPause(string status) =>
        JobStatusText.TryFromText(status, out JobStatus parsed)
            && JobStateMachine.Evaluate(parsed, JobTrigger.RequestPause).IsAllowed;

    /// <summary>
    /// この状態の Job に再開を要求できるか。
    /// </summary>
    /// <remarks>
    /// 遷移が許可される場合（Paused / Pausing）だけ true。Queued / Running への再開は
    /// 冪等に成功するが、押しても何も変わらないボタンを出す理由が無い。
    /// </remarks>
    public static bool CanRequestResume(string status) =>
        JobStatusText.TryFromText(status, out JobStatus parsed)
            && JobStateMachine.Evaluate(parsed, JobTrigger.Resume).IsAllowed;
}
