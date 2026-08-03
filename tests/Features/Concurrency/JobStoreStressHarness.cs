using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// 複数スレッドから <see cref="IJobStore"/> を同時に叩き、観測を
/// <see cref="JobObservationLog"/> へ集めるハーネス。
/// </summary>
/// <remarks>
/// <para>
/// 乱数は種を固定する。失敗したときに同じ操作列を作り直せないと、
/// 「たまに落ちる」以上のことが分からない。種はワーカーごとにずらして与えるので、
/// ワーカー内の操作列は再現する（スレッドの噛み合い方までは再現しない）。
/// </para>
/// <para>
/// 時刻は実時計を使わずに、単調増加する連番から作る。ここで
/// <see cref="DateTimeOffset.UtcNow"/> を使うと、<c>FinishedAt &gt;= StartedAt</c> の
/// 検査が時計の粒度や補正に左右されて、競合とは無関係に落ちるようになる。
/// 番号は読み出しの<b>後</b>に採る。先に採ると、他のスレッドが先に進めた Job へ
/// 過去の時刻を書いてしまい、実装の落ち度でない時刻の逆転が起きる。
/// </para>
/// </remarks>
public sealed class JobStoreStressHarness
{
    private const string StressJobType = "stress";

    private readonly IJobStore _store;
    private readonly int _seed;
    private readonly DateTimeOffset _origin;

    // 登録済みの Id。読み書きが並行するので、素の List では壊れる。
    private readonly List<JobId> _known = [];
    private readonly Lock _knownGate = new();

    private int _tick;

    public JobStoreStressHarness(IJobStore store, int seed, DateTimeOffset origin)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _seed = seed;
        _origin = origin;
    }

    /// <summary>同時に走らせるスレッド数。</summary>
    public int Workers { get; init; } = 4;

    /// <summary>1 スレッドあたりの操作数。</summary>
    public int OperationsPerWorker { get; init; } = 120;

    /// <summary>
    /// 操作の対象に選ぶ「直近に登録された Job」の数。
    /// </summary>
    /// <remarks>
    /// 登録済みの全件から一様に選ぶと、Job が散らばって同じ Job の同じ状態に
    /// 2 本以上が同時に届かなくなる。実測では、条件付き更新を外した store を
    /// 当てても検査が何も見つけられなかった（＝競合が起きていなかった）。
    /// 直近の数件に絞ることで、状態が動いている Job へ全員が集まるようにする。
    /// </remarks>
    public int PickWindow { get; init; } = 5;

    /// <summary>集めた観測。</summary>
    public JobObservationLog Log { get; } = new();

    /// <summary>ワーカーの中で漏れた例外。1 件でもあれば実装が同時実行に耐えていない。</summary>
    public IReadOnlyList<Exception> Failures => _failures;

    private readonly List<Exception> _failures = [];

    /// <summary>
    /// ストレスを走らせる。すべてのワーカーが終わるまで待つ。
    /// </summary>
    public async Task RunAsync()
    {
        // 全ワーカーが揃ってから始める。先に走り出した 1 本が全部終えてしまうと、
        // 名前だけストレスで実際には競合が起きない。
        AsyncStartGate start = new(Workers);

        await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            Random random = new(_seed + (worker * 7919));
            await start.SignalAndWaitAsync();

            for (int operation = 0; operation < OperationsPerWorker; operation++)
            {
                try
                {
                    await StepAsync(random, worker, operation);
                }
                catch (Exception exception)
                {
                    lock (_failures)
                    {
                        _failures.Add(exception);
                    }
                }
            }
        })));
    }

    /// <summary>
    /// 最後にもう一度すべての Job を読み、終端まで含めた姿を観測に加える。
    /// </summary>
    public async Task ObserveAllAsync()
    {
        foreach (JobId id in Snapshot())
        {
            await Log.ObserveAsync(() => _store.FindAsync(id, CancellationToken.None));
        }
    }

    private Task StepAsync(Random random, int worker, int operation) => random.Next(10) switch
    {
        // 登録は少なめにする。多いと Job が散らばって、同じ Job への競合が起きにくくなる。
        < 2 => RegisterAsync(worker, operation),
        < 7 => UpdateAsync(random),
        _ => ObserveAsync(random),
    };

    private async Task RegisterAsync(int worker, int operation)
    {
        JobId id = JobId.From($"w{worker}-{operation:D4}");

        // 採番を先に済ませてから公開する。公開後に他のスレッドが触るので、
        // CreatedAt はその時点より必ず前になる。
        Job job = Job.Create(id, $"Job {id.Value}", StressJobType, string.Empty, NextInstant());
        await _store.AddAsync(job, CancellationToken.None);

        lock (_knownGate)
        {
            _known.Add(id);
        }
    }

    private async Task UpdateAsync(Random random)
    {
        if (Pick(random) is not { } id)
        {
            return;
        }

        Job? job = await _store.FindAsync(id, CancellationToken.None);
        if (job is null)
        {
            return;
        }

        // Apply は Job を破壊的に変えるので、読み出したときの状態をここで控える。
        JobStatus expected = job.Status;
        JobTrigger trigger = ChooseTrigger(random, expected);

        // 読み出しと書き戻しの間で必ず一度譲る。ここを詰めると、SQLite の I/O が
        // 直列化する分だけ 1 本ずつ read-apply-write を終えてしまい、競合が起きない。
        // 実測では、条件付き更新を外した store を当てても検査が何も見つけなかった。
        // 本番のこの区間にはハンドラの実行や HTTP の往復が挟まるので、
        // 窓を開けておくほうが現実に近い。
        await Task.Yield();

        if (!job.Apply(trigger, NextInstant(), $"ストレス試験の {trigger}").IsAllowed)
        {
            // 実際の呼び出し側も拒否されたら書かない。ここで書くと、
            // 検査したい経路（条件付き更新の競合）とは別の話になる。
            return;
        }

        // 書き戻せなかった場合は何も記録しない。負けた側は状態を進めていないので、
        // 観測の列に足すものが無い。競合そのものを確実に起こして確かめるのは、
        // 窓を狙い撃ちしている JobStoreConcurrencyStressTests の別のテストの仕事。
        if (await _store.UpdateAsync(job, expected, CancellationToken.None))
        {
            Log.RecordAcceptedUpdate(id, expected, job.Status);
        }
    }

    private async Task ObserveAsync(Random random)
    {
        if (Pick(random) is not { } id)
        {
            return;
        }

        await Log.ObserveAsync(() => _store.FindAsync(id, CancellationToken.None));
    }

    /// <summary>
    /// 契機を選ぶ。多くは今の状態で通るものから選び、たまに通らないものも混ぜる。
    /// </summary>
    /// <remarks>
    /// 全体から一様に選ぶと、ほとんどが拒否されて Job が Queued のまま動かない。
    /// 逆に通るものだけにすると、拒否された契機を書き込んでしまう誤りを見逃す。
    /// </remarks>
    private static JobTrigger ChooseTrigger(Random random, JobStatus status)
    {
        JobTrigger[] all = Enum.GetValues<JobTrigger>();
        if (random.Next(10) == 0)
        {
            return all[random.Next(all.Length)];
        }

        JobTrigger[] allowed =
            [.. all.Where(trigger => JobStateMachine.Evaluate(status, trigger).IsAllowed)];

        return allowed.Length == 0 ? all[random.Next(all.Length)] : allowed[random.Next(allowed.Length)];
    }

    private JobId? Pick(Random random)
    {
        lock (_knownGate)
        {
            if (_known.Count == 0)
            {
                return null;
            }

            int window = Math.Min(PickWindow, _known.Count);
            return _known[_known.Count - 1 - random.Next(window)];
        }
    }

    private IReadOnlyList<JobId> Snapshot()
    {
        lock (_knownGate)
        {
            return [.. _known];
        }
    }

    private DateTimeOffset NextInstant() => _origin.AddSeconds(Interlocked.Increment(ref _tick));
}
