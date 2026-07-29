namespace Netsoft.Jobs.Web;

/// <summary>
/// Job の変更を画面へ伝える。書き込み側（エンジンや API のスレッド）が発火し、
/// 各画面の回路が購読して再描画の契機にする。
/// </summary>
/// <remarks>
/// 「何が変わったか」は運ばない。画面は一覧を取り直すだけなので差分に使い道が無く、
/// 運ぶと購読側がその形に依存して、変更の種類が増えるたびにここも直すことになる。
/// event への購読・解除はデリゲートの差し替えなので、発火と並行しても安全。
/// </remarks>
public sealed class JobChangeFeed
{
    /// <summary>いずれかの Job が追加・更新された。</summary>
    public event Action? Changed;

    /// <summary>
    /// 変更を知らせる。
    /// </summary>
    /// <remarks>
    /// 購読側は例外を漏らさないこと。ここで漏れると、発火元である store の書き込みが
    /// 成功しているのに失敗したように見えてしまう。
    /// </remarks>
    public void Publish() => Changed?.Invoke();
}
