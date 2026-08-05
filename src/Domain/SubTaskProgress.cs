namespace Netsoft.Jobs.Domain;

/// <summary>
/// 1 つの Job のサブタスクの進み具合。完了した数と、いま存在する行の総数。
/// </summary>
/// <remarks>
/// <para>
/// 総数を parameters の N ではなく<b>行数</b>で持つ。編集で N を減らしたときは未着手の行が
/// 消えるので、行数が「いま何個で終わる予定か」の答えになる。parameters を読み直して
/// 数え直すと、編集から境界での突き合わせまでの間だけ表示が実態とずれる。
/// </para>
/// <para>
/// 行が 1 つも無い（登録直後・実行前）は <c>Total = 0</c> で表す。これは「進捗ゼロ」ではなく
/// 「まだ分割されていない」で、画面はこの区別を「-」として出す。
/// </para>
/// </remarks>
/// <param name="Completed">完了した（Completed の）サブタスクの数。</param>
/// <param name="Total">その Job のサブタスクの行数。</param>
public readonly record struct SubTaskProgress(int Completed, int Total)
{
    /// <summary>まだ行が無い状態。</summary>
    public static SubTaskProgress None => new(0, 0);
}
