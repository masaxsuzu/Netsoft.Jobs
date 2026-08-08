namespace Netsoft.Jobs.Domain;

/// <summary>
/// Job の状態。
/// </summary>
/// <remarks>
/// <para>
/// 永続化では名前で保存する。数値には意味を持たせないので、
/// 並べ替えても既存データが壊れない。逆に、順序比較（Status &gt; InProgress など）で
/// 判定を書くと並べ替えで壊れるため、判定は必ず <see cref="JobStatusExtensions"/> を通すこと。
/// </para>
/// <para>
/// 状態は 3 種に分かれる。<b>静止（ed）</b>は誰も何もしていない状態
/// ── <see cref="Registered"/> <see cref="Resumed"/> <see cref="Paused"/> と 3 つの終端。
/// <b>確定待ち（ing）</b>は要求を受け付けて確定を待っている状態
/// ── <see cref="Pausing"/> <see cref="Cancelling"/> <see cref="Resuming"/>
/// （<see cref="JobStatusExtensions.IsSettling"/>）。<see cref="InProgress"/> はハンドラが実際に働いている唯一の状態で、
/// 落ち先が 2 つ（<see cref="Completed"/> / <see cref="Failed"/>）あることだけが他の ing と違う。
/// </para>
/// </remarks>
public enum JobStatus
{
    /// <summary>登録済み。まだ一度も実行されていない。</summary>
    Registered,

    /// <summary>実行中。ハンドラが動いている。</summary>
    InProgress,

    /// <summary>キャンセル要求済み。確定を待っている。</summary>
    Cancelling,

    /// <summary>正常終了。</summary>
    Completed,

    /// <summary>異常終了。</summary>
    Failed,

    /// <summary>キャンセルにより終了。</summary>
    Cancelled,

    /// <summary>一時停止要求済み。確定を待っている（実行中なら、サブタスクの境界での受理を待つ）。</summary>
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

    /// <summary>再開要求済み。確定を待っている。</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Paused"/> から待ち行列（<see cref="Resumed"/>）へは 1 手で行けるので、
    /// この状態は<b>行き先のためではなく、要求を受け付けた事実を残すために在る</b>。
    /// 要求がすべて ing を経由するようになると、押した直後に何が起きているかが
    /// 相手の都合（ハンドラが居るかどうか）で変わらなくなる。
    /// </para>
    /// <para>
    /// ただし<b>役割は 1 つ増えている</b>。この状態は
    /// <see cref="JobStatusExtensions.BlocksQueue"/> に含まれ、待ち行列を止める
    /// ── <see cref="Paused"/> から手を離してから <see cref="Resumed"/> を掴むまでの間、
    /// 席を空けないため。
    /// </para>
    /// </remarks>
    Resuming,
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
    /// <para>
    /// 要求を受けたコマンドが「確定を誰に任せるか」を決めるのに使う。false なら待つ相手が
    /// 居ないので、コマンド自身が続けて確定を書く（Features の各ハンドラ）。
    /// </para>
    /// <para>
    /// <see cref="JobStatus.Cancelling"/> と <see cref="JobStatus.Pausing"/> は、
    /// 待ち行列からの要求でも一瞬は通る状態だが、そこは同じコマンドがその場で確定させるので
    /// <b>静止している ing はハンドラ由来しかありえない</b>。この判定が成り立つのはそのため。
    /// 確定を非同期にしたくなったら、ここの前提が先に壊れる。
    /// </para>
    /// <para>
    /// <see cref="JobStatus.Resuming"/> は含めない。<see cref="JobStatus.Paused"/> からしか
    /// 来ないので、ハンドラは居ない。
    /// </para>
    /// </remarks>
    public static bool IsHandlerActive(this JobStatus status) =>
        status is JobStatus.InProgress or JobStatus.Cancelling or JobStatus.Pausing;

    /// <summary>
    /// 確定待ちの状態（ing）を、対応する静止状態（ed）へ落とす契機。ing でなければ null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ing と ed の対応そのものは <see cref="JobStateMachine"/> の表が持つ。ここが持つのは
    /// 「どの契機を引けばその対応をたどれるか」で、確定を書く側（コマンド・エンジン・起動時復旧）が
    /// 状態ごとの分岐を各自で書かずに済むようにしてある。<b>3 か所に写すと、ing を増やしたときに
    /// 増やし忘れた 1 か所だけが静かに滞留する。</b>
    /// </para>
    /// <para>
    /// <see cref="JobStatus.InProgress"/> は含めない。あれは要求ではなく実行そのもので、
    /// 落ち先が 1 つに決まらない（<see cref="JobStatus"/> の注記）。
    /// </para>
    /// </remarks>
    public static JobTrigger? SettlementTrigger(this JobStatus status) => status switch
    {
        JobStatus.Pausing => JobTrigger.ConfirmPaused,
        JobStatus.Cancelling => JobTrigger.ConfirmCancelled,
        JobStatus.Resuming => JobTrigger.ConfirmResumed,
        _ => null,
    };

    /// <summary>
    /// 要求を受け付けて確定を待っている状態（ing）か。
    /// </summary>
    public static bool IsSettling(this JobStatus status) => status.SettlementTrigger() is not null;

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
    /// 待ち行列を止める状態か。1 件でもあれば、待機中が居ても次は動かさない。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="JobStatus.Paused"/> は利用者が決めた規則（止めたなら次も流さない）。
    /// <see cref="JobStatus.Resuming"/> はその<b>取りこぼしを塞ぐため</b>に居る。
    /// 再開は 2 回の書き込みに分かれていて（Paused → Resuming → Resumed）、
    /// 1 回目が変更通知でエンジンを起こす。その瞬間、再開する Job は
    /// <see cref="IsWaiting"/> でも <see cref="JobStatus.Paused"/> でもない
    /// ── <b>列に並んでもいないし、列を止めてもいない</b>。ここを塞がないと、
    /// 起きたエンジンが後から登録された Job を先に走らせる。
    /// </para>
    /// <para>
    /// <see cref="JobStatus.Pausing"/> は含めない。あれは <see cref="JobStatus.InProgress"/>
    /// からしか来ないので、含めると「実行中が 1 件あるだけで次が出ない」という別の規則になる。
    /// そして実行中はエンジンがハンドラの中に居るので、そもそも塞ぐ窓が無い。
    /// </para>
    /// </remarks>
    public static bool BlocksQueue(this JobStatus status) =>
        status is JobStatus.Paused or JobStatus.Resuming;

    /// <summary>
    /// パラメータを編集できる状態か。
    /// </summary>
    /// <remarks>
    /// 終端は不可（結果が確定した後の書き換えは記録の改竄になる）。
    /// Cancelling も不可。捨てると決まった Job の定義を直しても、誰の役にも立たない。
    /// それ以外（Registered / Resumed / InProgress / Pausing / Paused / Resuming）は編集できる。
    /// 実行中の反映はサブタスクの境界の突き合わせが引き受ける。
    /// </remarks>
    public static bool CanEditParameters(this JobStatus status) =>
        !status.IsTerminal() && status != JobStatus.Cancelling;
}
