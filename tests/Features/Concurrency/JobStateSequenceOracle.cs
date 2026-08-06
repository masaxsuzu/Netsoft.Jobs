using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// 観測された状態の列が <see cref="JobStateMachine"/> で説明できるかを検査する。
/// </summary>
/// <remarks>
/// <para>
/// 競合が状態を壊したことは、壊れた瞬間を捕まえなくても後から証明できる。
/// ひとつの Job について観測された状態を並べ、隣り合う 2 つが状態機械の遷移で
/// 繋がらなければ、その間に誰かが状態機械を迂回して書いたということになる。
/// </para>
/// <para>
/// 判定に使うのは「1 手で行けるか」ではなく「何手かで行けるか」（到達可能性）である。
/// 観測は連続していないので、<c>Registered</c> の次に <c>Completed</c> を見ることは普通に起きる。
/// これを非合法にすると、正しい実装でも観測の間隔次第で落ちる（＝ flaky な）検査になる。
/// 逆に <c>Completed → Cancelling</c> のような終端からの後退は何手かけても到達できないので、
/// 到達可能性で見ても確実に捕まる。<c>InProgress → Resumed</c> は<b>もう後退ではない</b> ──
/// 一時停止からの再開（<c>InProgress → Pausing → Paused → Resumed</c>）で到達するので、
/// この検査は通す。閉路が入った時点で「一方通行だから捕まる」という言い方は成り立たなくなった。
/// </para>
/// <para>
/// 到達可能性は <see cref="JobStateMachine"/> から組み立てる。遷移表をここに書き写すと
/// 状態機械が変わったときに黙って乖離し、オラクル自身が嘘をつくようになる。
/// </para>
/// </remarks>
public static class JobStateSequenceOracle
{
    private static readonly IReadOnlyDictionary<JobStatus, IReadOnlySet<JobStatus>> ReachableFrom = BuildReachability();

    /// <summary>
    /// <paramref name="from"/> から 0 手以上で <paramref name="to"/> へ行けるか。
    /// </summary>
    /// <remarks>
    /// 0 手を含めるのは、状態が変わっていない 2 回の観測を合法とするため。
    /// </remarks>
    public static bool IsReachable(JobStatus from, JobStatus to) =>
        from == to || ReachableFrom[from].Contains(to);

    /// <summary>
    /// 観測された状態の列を検査する。説明できない箇所があればその説明、無ければ null。
    /// </summary>
    /// <remarks>
    /// 見つけた最初の 1 件だけを返す。1 箇所でも壊れていればそれで十分な証拠であり、
    /// 壊れた後の列は既に信用できないので、続きを数えても意味が無い。
    /// </remarks>
    public static string? FindViolation(IReadOnlyList<JobStatus> observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        // 最初の観測は Registered から到達できていなければならない。Job は必ず Registered で作られる。
        if (observed.Count > 0 && !IsReachable(JobStatus.Registered, observed[0]))
        {
            return $"最初の観測 {observed[0]} は Registered から到達できません。";
        }

        for (int i = 1; i < observed.Count; i++)
        {
            if (!IsReachable(observed[i - 1], observed[i]))
            {
                return $"観測 {i - 1} → {i} の {observed[i - 1]} → {observed[i]} は状態機械で説明できません。"
                    + $" 列: {string.Join(" → ", observed)}";
            }
        }

        return null;
    }

    /// <summary>
    /// 状態機械を歩いて到達可能性を作る。
    /// </summary>
    private static IReadOnlyDictionary<JobStatus, IReadOnlySet<JobStatus>> BuildReachability()
    {
        Dictionary<JobStatus, IReadOnlySet<JobStatus>> reachable = [];

        foreach (JobStatus origin in Enum.GetValues<JobStatus>())
        {
            HashSet<JobStatus> visited = [];
            Queue<JobStatus> pending = new();
            pending.Enqueue(origin);

            while (pending.TryDequeue(out JobStatus current))
            {
                foreach (JobTrigger trigger in Enum.GetValues<JobTrigger>())
                {
                    JobTransitionResult result = JobStateMachine.Evaluate(current, trigger);
                    if (result.IsAllowed && visited.Add(result.Status))
                    {
                        pending.Enqueue(result.Status);
                    }
                }
            }

            reachable[origin] = visited;
        }

        return reachable;
    }
}
