namespace Netsoft.Jobs.Ui;

/// <summary>
/// UI ホストの設定。appsettings.json の <c>Ui</c> セクションから束ねる。
/// </summary>
/// <remarks>
/// IOptions を使わず起動時に 1 度だけ読む理由は Web 側の JobsOptions と同じで、
/// どの値も実行中に差し替えられないため、変更の再読込みという能力に払うものが無い。
/// </remarks>
public sealed class UiOptions
{
    /// <summary>設定セクションの名前。</summary>
    public const string SectionName = "Ui";

    /// <summary>
    /// API ホスト（Web）の口。Job の操作も変更通知（SSE）もすべてここへ向かう。
    /// </summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:5000";

    /// <summary>
    /// SSE と独立に合図を発火する間隔。SSE が生きていれば無駄打ちだが、
    /// 死んでいるときに画面を最新へ追いつかせる唯一の保険。
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// SSE が切れた（または繋がらなかった）あと、繋ぎ直すまでの間隔。
    /// </summary>
    /// <remarks>
    /// 短いほど push 更新の復帰は早いが、API ホストが落ちている間の空振りも増える。
    /// 取りこぼし自体はポーリングが拾うので、ここを攻める必要はない。
    /// </remarks>
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 変更通知の購読サービスを常駐させるか。
    /// </summary>
    /// <remarks>
    /// テストが false にして止める。購読サービスが実時間の間隔で勝手に合図を発火すると、
    /// 「合図はいつ何回発火するか」を検証するテストが時間に左右される。
    /// </remarks>
    public bool SubscribeToChanges { get; set; } = true;
}
