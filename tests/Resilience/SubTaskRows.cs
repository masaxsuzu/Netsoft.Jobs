using Microsoft.Data.Sqlite;

namespace Netsoft.Jobs.Resilience.Tests;

/// <summary>
/// サブタスクの行を DB から直に読む。
/// </summary>
/// <remarks>
/// <para>
/// 行ごとの状態は API に出ていない（一覧が運ぶのは「N 個中 k 個完了」の数だけ）。
/// ここで見たいのは「強制終了で中断点が残るか」「キャンセルで畳まれるか」という
/// <b>行そのものの姿</b>なので、数では足りない。
/// </para>
/// <para>
/// ホストは別プロセスなので DI からも触れない。残る手は DB を直に読むことだけで、
/// その代わりスキーマ（表と列の名前）に結びつく。列を変えるとここが落ちる
/// ── 落ちてよい。行の形を検証している以上、形が変わったら見直すべきテストなので。
/// </para>
/// </remarks>
internal static class SubTaskRows
{
    /// <summary>連番順の状態を返す。行が無ければ空。</summary>
    public static async Task<IReadOnlyList<string>> ReadAsync(string databasePath, string jobId)
    {
        using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();

        SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM SubTasks WHERE JobId = $jobId ORDER BY Position;";
        command.Parameters.AddWithValue("$jobId", jobId);

        List<string> statuses = [];
        using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            statuses.Add(reader.GetString(0));
        }

        return statuses;
    }
}
