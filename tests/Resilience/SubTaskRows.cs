using Microsoft.Data.Sqlite;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Infrastructure;

namespace Netsoft.Jobs.Resilience.Tests;

/// <summary>
/// 走っているホストの DB から、サブタスクの行を読む。
/// </summary>
/// <remarks>
/// <para>
/// 行ごとの状態は API に出ていない（一覧が運ぶのは「N 個中 k 個完了」の数だけ）。
/// ここで見たいのは「強制終了で中断点が残るか」「キャンセルで畳まれるか」という
/// <b>行そのものの姿</b>なので、数では足りない。ホストは別プロセスなので DI からも触れない。
/// </para>
/// <para>
/// SQL は書かず、製品と同じ <see cref="SqliteSubTaskStore"/> を開いて読む。生の SELECT を
/// 書くと表と列の名前がテスト側にも写り、スキーマを変えるたびに直す場所が増える。
/// 連番順に返すことや状態の文字列表現も、store が持っているものをそのまま使える。
/// </para>
/// <para>
/// <see cref="SqliteSubTaskStore.InitializeAsync"/> は呼ばない。表を作るのはホストの仕事で、
/// ここが作りにいくと「ホストが作れていなくてもテストが通る」形になる。
/// </para>
/// </remarks>
internal static class SubTaskRows
{
    /// <summary>連番順の状態を返す。行が無ければ空。</summary>
    public static async Task<IReadOnlyList<string>> ReadAsync(string databasePath, string jobId) =>
        [.. (await new SqliteSubTaskStore(databasePath)
            .ListByJobAsync(JobId.From(jobId), CancellationToken.None))
            .Select(subTask => SubTaskStatusText.ToText(subTask.Status))];

    /// <summary>
    /// 読みに使った接続をプールから外す。DB のファイルを消す前に呼ぶ。
    /// </summary>
    /// <remarks>
    /// 閉じるのは自分の DB のプールだけ（理由は docs/build.md「テストの後始末」）。
    /// </remarks>
    public static void ClearPool(string databasePath) =>
        SqliteConnection.ClearPool(
            new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()));
}
