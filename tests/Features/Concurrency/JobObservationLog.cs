using System.Globalization;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// 並行に動くスレッドが見たものを Job ごとに集め、最後にまとめて検査する。
/// </summary>
/// <remarks>
/// <para>
/// <b>観測の順序をどう決めるかがこの型の肝である。</b> 別々のスレッドが読んだ結果を
/// 記録した順に並べても、それは「読んだ順」ではない（読み終えてから記録するまでに
/// 他のスレッドが割り込む）。順序の分からない観測を状態の列として検査すると、
/// 実装が正しくても偽の後退が見えて flaky になる。
/// </para>
/// <para>
/// そこで読み取りは <see cref="ObserveAsync"/> に集約し、この中で直列化する。
/// 読み取りどうしが重ならなくなるので、記録の順序がそのまま観測の順序になる。
/// 書き込みは直列化しない。競合させたいのは書き込みなので、そこを止めては意味が無い。
/// </para>
/// <para>
/// 条件付き更新が成功したことも <see cref="RecordAcceptedUpdate"/> で記録する。
/// こちらは順序を知らなくても検査できる。ある Job のある状態から書き戻せるのは
/// 高々 1 回のはずで（状態機械は後戻りしないので同じ状態には二度と入らない）、
/// 同じ <c>expectedStatus</c> で 2 回成功したら、それは更新がひとつ失われた証拠になる。
/// </para>
/// </remarks>
public sealed class JobObservationLog
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly Dictionary<string, List<Job>> _observations = [];
    private readonly Dictionary<string, List<(JobStatus From, JobStatus To)>> _acceptedUpdates = [];

    /// <summary>
    /// 読み取りを直列化して観測する。読み取り自体の実行もこの中で行うこと。
    /// </summary>
    /// <remarks>
    /// 読み取りと記録を呼び出し側で分けると、その間に他の読み取りが割り込んで
    /// 順序が崩れる。まとめて受け取るのはそれを防ぐため。
    /// </remarks>
    public async Task<Job?> ObserveAsync(Func<Task<Job?>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        await _readGate.WaitAsync();
        try
        {
            Job? job = await read();
            if (job is not null)
            {
                Record(job);
            }

            return job;
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>
    /// 条件付き更新が成功したことを記録する。
    /// </summary>
    public void RecordAcceptedUpdate(JobId id, JobStatus from, JobStatus to)
    {
        lock (_gate)
        {
            if (!_acceptedUpdates.TryGetValue(id.Value, out List<(JobStatus, JobStatus)>? edges))
            {
                edges = [];
                _acceptedUpdates[id.Value] = edges;
            }

            edges.Add((from, to));
        }
    }

    /// <summary>
    /// 集めたものをすべて検査する。破れていることの説明を並べて返す。
    /// </summary>
    /// <remarks>
    /// 例外を投げずに一覧で返すのは、失敗したときに「どの Job の何が壊れたか」を
    /// まとめて見たいから。1 件目で落とすと、次の実行でまた次の 1 件を見ることになる。
    /// </remarks>
    public IReadOnlyList<string> FindViolations()
    {
        List<string> violations = [];

        lock (_gate)
        {
            foreach ((string id, List<Job> snapshots) in _observations)
            {
                violations.AddRange(FindSnapshotViolations(snapshots));

                if (JobStateSequenceOracle.FindViolation([.. snapshots.Select(job => job.Status)]) is { } sequence)
                {
                    violations.Add($"Job {id}: {sequence}");
                }
            }

            foreach ((string id, List<(JobStatus From, JobStatus To)> edges) in _acceptedUpdates)
            {
                violations.AddRange(FindUpdateChainViolations(id, edges));
            }
        }

        return violations;
    }

    /// <summary>観測された Job の総数。ストレスが実際に走ったことの裏付けに使う。</summary>
    public int ObservedJobCount
    {
        get
        {
            lock (_gate)
            {
                return _observations.Count;
            }
        }
    }

    /// <summary>条件付き更新が成功した回数。</summary>
    public int AcceptedUpdateCount
    {
        get
        {
            lock (_gate)
            {
                return _acceptedUpdates.Values.Sum(edges => edges.Count);
            }
        }
    }

    private void Record(Job job)
    {
        lock (_gate)
        {
            if (!_observations.TryGetValue(job.Id.Value, out List<Job>? snapshots))
            {
                snapshots = [];
                _observations[job.Id.Value] = snapshots;
            }

            snapshots.Add(job);
        }
    }

    /// <summary>
    /// スナップショット単体の不変条件と、終端に達した後の不変性を見る。
    /// </summary>
    private static IEnumerable<string> FindSnapshotViolations(List<Job> snapshots)
    {
        Job? settled = null;

        foreach (Job job in snapshots)
        {
            if (JobInvariants.FindViolation(job) is { } invariant)
            {
                yield return invariant;
            }

            if (settled is { } terminal && Differs(terminal, job))
            {
                yield return $"Job {job.Id.Value}: 終端 {terminal.Status} に達した後で"
                    + $" {Describe(job)} へ変化しました（終端は {Describe(terminal)}）。";
            }

            settled ??= job.Status.IsTerminal() ? job : null;
        }
    }

    /// <summary>
    /// 条件付き更新の成功が、Registered から始まる 1 本の道として並べられるかを見る。
    /// </summary>
    /// <remarks>
    /// <para>
    /// かつては「状態 → 次の状態」の辞書（関数）で確かめていた。同じ状態から 2 回
    /// 書き戻せたら喪失、という判定だったが、一時停止と再開の閉路ができてからは
    /// 同じ状態を正当に 2 度通れる（再開後の Resumed → InProgress など）。関数の形の検査は
    /// 閉路で嘘をつき、鎖を辿るループは無限に回る（実際にテストホストが固まった）。
    /// </para>
    /// <para>
    /// いまの検査は、観測された辺の全部を 1 回ずつ使って Registered から一筆書きできるか
    ///（多重グラフの道の存在）。更新の喪失（X → A と X → B が両方成功したのに
    /// X へは 1 度しか入っていない）はどちらかの辺が使えず道が完成しないので捕まる。
    /// 記録の並びには依存しない。並びはコミット順と入れ替わりうるため。
    /// </para>
    /// </remarks>
    private static IEnumerable<string> FindUpdateChainViolations(
        string id,
        List<(JobStatus From, JobStatus To)> edges)
    {
        if (edges.Count > 0 && !CanWalkAll(JobStatus.Registered, edges))
        {
            yield return $"Job {id}: 成功した更新 {edges.Count} 件を Registered から"
                + $" 1 本の道として並べられません（{FormatEdges(edges)}）。"
                + "更新の喪失か、状態機械を迂回した書き込みがあります。";
        }
    }

    /// <summary>
    /// <paramref name="remaining"/> の辺を全部 1 回ずつ使って
    /// <paramref name="current"/> から歩き切れるか。
    /// </summary>
    /// <remarks>
    /// 全順序を試す後戻り探索。辺の数は 1 つの Job の一生ぶん（高々十数本）なので、
    /// 効率より「閉路があっても正しい」ことを優先する。
    /// </remarks>
    private static bool CanWalkAll(JobStatus current, List<(JobStatus From, JobStatus To)> remaining)
    {
        if (remaining.Count == 0)
        {
            return true;
        }

        for (int i = 0; i < remaining.Count; i++)
        {
            if (remaining[i].From != current)
            {
                continue;
            }

            List<(JobStatus From, JobStatus To)> rest = [.. remaining];
            rest.RemoveAt(i);

            if (CanWalkAll(remaining[i].To, rest))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Differs(Job left, Job right) =>
        left.Status != right.Status
        || left.StartedAt != right.StartedAt
        || left.FinishedAt != right.FinishedAt
        || left.FailureMessage != right.FailureMessage;

    private static string Describe(Job job) => string.Create(
        CultureInfo.InvariantCulture,
        $"{job.Status} (StartedAt={job.StartedAt:O}, FinishedAt={job.FinishedAt:O}, FailureMessage={job.FailureMessage})");

    private static string FormatEdges(List<(JobStatus From, JobStatus To)> edges) =>
        string.Join(", ", edges.Select(edge => $"{edge.From}→{edge.To}"));
}
