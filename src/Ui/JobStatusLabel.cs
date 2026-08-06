using Netsoft.Jobs.Domain;

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
/// <see cref="JobStatus.InProgress"/> だけ英語の "Running" を出す。利用者が読んできた語を
/// 内部の改名に合わせて変える理由が無いため。<b>ここが状態名と一致していないのは意図的。</b>
/// </para>
/// <para>
/// 読み戻せない値は、そのまま出す。知らない状態を「不明」などに畳むと、
/// 画面を見ている人から見て何が起きているのか分からなくなる。
/// </para>
/// </remarks>
public static class JobStatusLabel
{
    /// <summary>状態の文字列表現を、画面に出す文言にする。</summary>
    public static string From(string status) =>
        JobStatusText.TryFromText(status, out JobStatus parsed) ? From(parsed) : status;

    /// <summary>状態を画面に出す文言にする。</summary>
    public static string From(JobStatus status) => status switch
    {
        JobStatus.Registered => "登録済み",
        JobStatus.InProgress => "Running",
        JobStatus.Pausing => "保留要求中",
        JobStatus.Paused => "保留中",
        JobStatus.Resuming => "再開要求中",
        JobStatus.Resumed => "再開待ち",
        JobStatus.Cancelling => "中止要求中",
        JobStatus.Cancelled => "中止済み",
        JobStatus.Completed => "完了",
        JobStatus.Failed => "失敗",

        // 状態を足して写像を忘れると、画面には enum の名前が出る。黙って空欄になるよりはよい。
        _ => JobStatusText.ToText(status),
    };
}
