namespace Netsoft.Jobs.Domain;

/// <summary>
/// Job の状態。
/// </summary>
/// <remarks>
/// 永続化では名前で保存する。数値には意味を持たせないので、
/// 並べ替えても既存データが壊れない。逆に、順序比較（Status &gt; Running など）で
/// 判定を書くと並べ替えで壊れるため、判定は必ず <see cref="JobStatusExtensions"/> を通すこと。
/// </remarks>
public enum JobStatus
{
    /// <summary>待機中。まだ実行スロットが割り当てられていない。</summary>
    Queued,

    /// <summary>実行中。</summary>
    Running,

    /// <summary>キャンセル要求済み。ハンドラがまだ動いている。</summary>
    Cancelling,

    /// <summary>正常終了。</summary>
    Completed,

    /// <summary>異常終了。</summary>
    Failed,

    /// <summary>キャンセルにより終了。</summary>
    Cancelled,

    /// <summary>一時停止要求済み。ハンドラはまだ動いていて、サブタスクの境界での受理を待つ。</summary>
    Pausing,

    /// <summary>一時停止中。ハンドラは居らず、再開されるまで誰も走らせない。</summary>
    Paused,
}

/// <summary>
/// <see cref="JobStatus"/> の判定。状態の意味を各層に散らさないため、ここに集約する。
/// </summary>
public static class JobStatusExtensions
{
    /// <summary>
    /// 終端状態か。終端に達した Job は以後いかなる契機でも遷移しない。
    /// </summary>
    public static bool IsTerminal(this JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary>
    /// ハンドラが動いている（動いていたはずの）状態か。
    /// </summary>
    /// <remarks>
    /// Queued は含めない。この判定は起動時復旧の基準に使う。
    /// プロセスが落ちた時点で Queued だった Job はハンドラを起動していないので、
    /// 次のプロセスがそのまま拾って実行すればよく、Failed にしてはいけない。
    /// </remarks>
    /// <remarks>
    /// Paused も含めない。一時停止の受理はハンドラが抜けた後なので、
    /// プロセスが落ちても失われた実行は無く、次のプロセスは再開要求を待てばよい。
    /// Pausing は含める。受理前に落ちたならハンドラは走っていた。
    /// </remarks>
    public static bool IsHandlerActive(this JobStatus status) =>
        status is JobStatus.Running or JobStatus.Cancelling or JobStatus.Pausing;
}
