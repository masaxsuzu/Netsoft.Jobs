using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// 不変条件の検査自身のテストと、実装がそれを満たしていることの網羅確認。
/// </summary>
public sealed class JobInvariantsTests
{
    private static readonly DateTimeOffset Created = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 登録直後のJobは不変条件を満たす()
    {
        Assert.Null(JobInvariants.FindViolation(CreateQueued()));
    }

    /// <summary>
    /// 状態機械が許す遷移だけを辿って作れるすべての Job が、不変条件を満たすこと。
    /// </summary>
    /// <remarks>
    /// 個別のケースを並べるのではなく総当たりにするのは、状態機械に遷移を足したときに
    /// テストを書き足さなくても検査が追随するようにするため。
    /// 遷移は終端で必ず止まるので、深さを切らなくても列挙は有限で終わる。
    /// </remarks>
    [Fact]
    public void 状態機械を通して作れるJobはすべて不変条件を満たす()
    {
        List<string> violations = [];
        Walk(CreateQueued(), [], violations);

        Assert.Empty(violations);
    }

    [Fact]
    public void 終端でないのにFinishedAtがあれば見つかる()
    {
        Job broken = Rehydrate(JobStatus.Running, startedAt: Created, finishedAt: Created);

        Assert.Contains("FinishedAt", JobInvariants.FindViolation(broken));
    }

    [Fact]
    public void 終端なのにFinishedAtが無ければ見つかる()
    {
        Job broken = Rehydrate(JobStatus.Completed, startedAt: Created, finishedAt: null);

        Assert.Contains("FinishedAt", JobInvariants.FindViolation(broken));
    }

    [Fact]
    public void Failedなのに理由が無ければ見つかる()
    {
        Job broken = Rehydrate(JobStatus.Failed, startedAt: Created, finishedAt: Created, failureMessage: null);

        Assert.Contains("FailureMessage", JobInvariants.FindViolation(broken));
    }

    [Fact]
    public void Failedでないのに理由があれば見つかる()
    {
        Job broken = Rehydrate(JobStatus.Completed, startedAt: Created, finishedAt: Created, failureMessage: "なぜ");

        Assert.Contains("FailureMessage", JobInvariants.FindViolation(broken));
    }

    [Fact]
    public void 実行中なのにStartedAtが無ければ見つかる()
    {
        Job broken = Rehydrate(JobStatus.Running, startedAt: null, finishedAt: null);

        Assert.Contains("StartedAt", JobInvariants.FindViolation(broken));
    }

    [Fact]
    public void 待機中なのにStartedAtがあれば見つかる()
    {
        Job broken = Rehydrate(JobStatus.Queued, startedAt: Created, finishedAt: null);

        Assert.Contains("StartedAt", JobInvariants.FindViolation(broken));
    }

    [Fact]
    public void 時刻が逆転していれば見つかる()
    {
        Assert.Contains(
            "StartedAt",
            JobInvariants.FindViolation(Rehydrate(JobStatus.Running, startedAt: Created.AddSeconds(-1), finishedAt: null)));

        Assert.Contains(
            "FinishedAt",
            JobInvariants.FindViolation(Rehydrate(
                JobStatus.Completed,
                startedAt: Created.AddMinutes(5),
                finishedAt: Created.AddMinutes(1))));

        Assert.Contains(
            "FinishedAt",
            JobInvariants.FindViolation(Rehydrate(
                JobStatus.Cancelled,
                startedAt: null,
                finishedAt: Created.AddSeconds(-1))));
    }

    /// <summary>
    /// 状態機械が許す遷移をすべて辿り、行き着いた Job を検査する。
    /// </summary>
    private static void Walk(Job job, List<JobTrigger> path, List<string> violations)
    {
        if (JobInvariants.FindViolation(job) is { } violation)
        {
            violations.Add($"[{string.Join(" → ", path)}] {violation}");
        }

        if (job.Status.IsTerminal())
        {
            return;
        }

        foreach (JobTrigger trigger in Enum.GetValues<JobTrigger>())
        {
            // 遷移のたびに時刻を進める。同じ時刻のままだと、時刻の前後関係を
            // 取り違えている実装を「たまたま等しい」で見逃してしまう。
            Job branch = Replay(path);
            DateTimeOffset at = Created.AddMinutes(path.Count + 1);

            if (!branch.Apply(trigger, at, $"{trigger} による失敗").IsAllowed)
            {
                continue;
            }

            Walk(branch, [.. path, trigger], violations);
        }
    }

    /// <summary>
    /// 契機の列を最初から辿り直して Job を作る。<see cref="Job"/> は破壊的に変わるので、
    /// 枝分かれのたびに複製する代わりに作り直す。
    /// </summary>
    private static Job Replay(List<JobTrigger> path)
    {
        Job job = CreateQueued();
        for (int i = 0; i < path.Count; i++)
        {
            job.Apply(path[i], Created.AddMinutes(i + 1), $"{path[i]} による失敗");
        }

        return job;
    }

    private static Job CreateQueued() =>
        Job.Create(JobId.From("job-1"), "検査対象", "test-job", string.Empty, Created);

    private static Job Rehydrate(
        JobStatus status,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        string? failureMessage = null) =>
        Job.Rehydrate(
            JobId.From("job-1"),
            "検査対象",
            "test-job",
            string.Empty,
            status,
            Created,
            startedAt,
            finishedAt,
            failureMessage);
}
