namespace Netsoft.Jobs.Ui;

/// <summary>
/// 状態を画面に出す文言にする。
/// </summary>
/// <remarks>
/// <para>
/// API が返す <c>Status</c> は enum の名前そのままで、そちらは外との契約なので変えない。
/// 変えてよいのは画面の中だけなので、写像はここに閉じている。
/// </para>
/// <para>
/// 受け取るのは状態の文字列で、enum へ読み戻さない。画面のプロセスが参照してよいのは
/// 契約（Contracts）だけで、そこに状態の列挙は無い。読み戻せたところで、ここがするのは
/// 名前から文言への写しだけなので、型に直す意味も無い。
/// </para>
/// <para>
/// "InProgress" だけ英語の "Running" を出す。利用者が読んできた語を内部の改名に合わせて
/// 変える理由が無いため。<b>ここが状態名と一致していないのは意図的。</b>
/// </para>
/// <para>
/// 知らない値は、そのまま出す。「不明」などに畳むと、画面を見ている人から見て
/// 何が起きているのか分からなくなる。状態を足して写像を忘れたときも、
/// 黙って空欄になるより enum の名前が出るほうがよい。
/// </para>
/// </remarks>
public static class JobStatusLabel
{
    /// <summary>状態の文字列表現を、画面に出す文言にする。</summary>
    public static string From(string status) => status switch
    {
        "Registered" => "登録済み",
        "InProgress" => "Running",
        "Pausing" => "保留要求中",
        "Paused" => "保留中",
        "Resuming" => "再開要求中",
        "Resumed" => "再開待ち",
        "Cancelling" => "中止要求中",
        "Cancelled" => "中止済み",
        "Completed" => "完了",
        "Failed" => "失敗",
        _ => status,
    };

    /// <summary>状態の文字列表現を、バッジの色を決める CSS クラスにする。</summary>
    /// <remarks>
    /// <para>
    /// 10 個の状態を 5 色に畳んである。<b>色で見分けたいのは「今どうなっているか」の大分類</b>で、
    /// 保留要求中と保留中を別の色にしても、一覧を眺める人が得るものが無い。
    /// 細かい違いは <see cref="From"/> の文言が持つ。
    /// </para>
    /// <para>
    /// 判定を <c>.razor</c> へ書かない。カバレッジが <c>**/*.razor</c> を除外していて、
    /// あちらに置いた分岐には床が一切かからない（<c>JobBoard</c> が Razor の外に居るのと同じ理由）。
    /// </para>
    /// <para>
    /// 知らない値は既定の色。<see cref="From"/> が名前をそのまま出すので、
    /// 色が付いていないこと自体が「写像を足し忘れた」の印になる。
    /// </para>
    /// </remarks>
    public static string ClassFor(string status) => status switch
    {
        "InProgress" or "Resuming" or "Resumed" => "badge-running",
        "Pausing" or "Paused" => "badge-paused",
        "Completed" => "badge-completed",
        "Failed" => "badge-failed",
        "Cancelling" or "Cancelled" => "badge-cancelled",
        _ => "badge-registered",
    };
}
