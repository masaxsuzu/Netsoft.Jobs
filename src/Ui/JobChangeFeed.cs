namespace Netsoft.Jobs.Ui;

/// <summary>
/// Job の変更を画面へ伝える。SSE の購読サービスとフォールバックポーリングが発火し、
/// 各画面の回路が購読して再描画の契機にする。
/// </summary>
/// <remarks>
/// <para>
/// 「何が変わったか」は運ばない。画面は一覧を取り直すだけなので差分に使い道が無く、
/// 運ぶと購読側がその形に依存して、変更の種類が増えるたびにここも直すことになる。
/// event への購読・解除はデリゲートの差し替えなので、発火と並行しても安全。
/// </para>
/// <para>
/// Web 側にも同型のクラスがあるが、共有はしない。これはプロセス内の合図という
/// ホスト実装の道具で、プロセスをまたぐ契約ではないから、共有の置き場である
/// Contracts（Domain のみに依存し、線の上を流れる形だけを置く）には入れられない。
/// 25 行のために共有プロジェクトを増やすより、各ホストが自分の物を持つ方が安い。
/// 発火元も違う（Web は store の書き込み、こちらは SSE とポーリング）ので、
/// 将来別々に育っても互いを壊さない。
/// </para>
/// </remarks>
[SingleInstance]
public sealed class JobChangeFeed
{
    /// <summary>いずれかの Job が追加・更新された（可能性がある）。</summary>
    public event Action? Changed;

    /// <summary>
    /// 変更を知らせる。
    /// </summary>
    /// <remarks>
    /// 購読側は例外を漏らさないこと。ここで漏れると、発火元である購読サービスの
    /// ループが例外で止まり、以後の通知が全部死ぬ。
    /// </remarks>
    public void Publish() => Changed?.Invoke();
}
