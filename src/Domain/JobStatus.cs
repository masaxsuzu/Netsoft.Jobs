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
    /// <summary>登録済み。まだ一度も実行されていない。</summary>
    Registered,

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

    /// <summary>
    /// 再開待ち。一度 <see cref="Paused"/> になった Job が、待ち行列へ戻って実行を待っている。
    /// </summary>
    /// <remarks>
    /// <see cref="Registered"/> と分けてあるのは、<b>できることの違いではなく、経てきた道の違い</b>。
    /// 待ち行列としての扱いも、受け付ける操作も 2 つは同じで
    /// （<see cref="JobStatusExtensions.IsWaiting"/> がまとめている）、
    /// 分けることで <see cref="Registered"/> を「<c>Create</c> でしか作られない」状態に保っている。
    /// 合流させると登録直後と再開待ちが混ざり、「まだ一度も待ち行列を出ていない」と
    /// 言える状態がどこにも無くなる。
    /// </remarks>
    Resumed,
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
    /// 待ち行列（<see cref="IsWaiting"/>）は含めない。この判定は起動時復旧の基準に使う。
    /// プロセスが落ちた時点で待っていた Job はハンドラを起動していないので、
    /// 次のプロセスがそのまま拾って実行すればよく、Failed にしてはいけない。
    /// </remarks>
    /// <remarks>
    /// Paused も含めない。一時停止の受理はハンドラが抜けた後なので、
    /// プロセスが落ちても失われた実行は無く、次のプロセスは再開要求を待てばよい。
    /// Pausing は含める。受理前に落ちたならハンドラは走っていた。
    /// </remarks>
    public static bool IsHandlerActive(this JobStatus status) =>
        status is JobStatus.Running or JobStatus.Cancelling or JobStatus.Pausing;

    /// <summary>
    /// 実行を待っている状態か。エンジンが拾う対象はこれ。
    /// </summary>
    /// <remarks>
    /// 登録されたまま（<see cref="JobStatus.Registered"/>）か、一時停止から戻ってきた
    /// （<see cref="JobStatus.Resumed"/>）か。<b>どちらも扱いは同じ</b>で、
    /// 分かれている理由は <see cref="JobStatus.Resumed"/> の注記に。
    /// </remarks>
    public static bool IsWaiting(this JobStatus status) =>
        status is JobStatus.Registered or JobStatus.Resumed;

    /// <summary>
    /// パラメータを編集できる状態か。
    /// </summary>
    /// <remarks>
    /// 終端は不可（結果が確定した後の書き換えは記録の改竄になる）。
    /// Cancelling も不可。捨てると決まった Job の定義を直しても、誰の役にも立たない。
    /// それ以外（Registered / Resumed / Running / Pausing / Paused）は編集できる。実行中の反映は
    /// サブタスクの境界の突き合わせが引き受ける。
    /// </remarks>
    public static bool CanEditParameters(this JobStatus status) =>
        !status.IsTerminal() && status != JobStatus.Cancelling;
}
