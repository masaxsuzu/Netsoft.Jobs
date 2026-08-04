using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.CancelJob;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// 実行エンジン・キャンセル・読み取りを、本物の store の上で同時に動かす。
/// </summary>
/// <remarks>
/// <para>
/// 部品ごとの試験は「その部品の中で競合が起きたら」しか見ていない。実際に壊れるのは
/// エンジンが結末を書こうとしているところへキャンセルが割り込む、といった
/// <b>部品をまたぐ</b>窓である。ここは本番と同じ組み合わせを丸ごと並行させて、
/// 出てきた状態の列と不変条件を検査する。
/// </para>
/// <para>
/// 終わり方は時間で決めない。エンジンは待機中の Job が尽きたら自分で止まり、
/// キャンセルと読み取りはエンジンが止まったら降りる。したがって試験は必ず終わる。
/// </para>
/// </remarks>
public sealed class JobLifecycleStressTests : IDisposable
{
    private const string HandledJobType = "test-job";
    private const int Seed = 20260803;
    private const int Jobs = 60;
    private const int Engines = 2;
    private const int Cancellers = 2;

    private static readonly DateTimeOffset Origin = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly TestMeterFactory _meterFactory = new();

    public void Dispose()
    {
        _meterFactory.Dispose();
        _store.Dispose();
    }

    [Fact]
    public async Task 実行とキャンセルと読み取りを同時に走らせても状態が壊れない()
    {
        JobObservationLog log = new();
        RecordingJobStore store = new(_store, log);

        // 時計は登録より後ろに固定する。エンジンが書く StartedAt / FinishedAt が
        // CreatedAt より前になると、実装ではなく試験の作り方で不変条件が破れる。
        FixedTimeProvider timeProvider = new(Origin.AddMinutes(1));

        CountingJobHandler handler = new(HandledJobType) { Yields = 3 };

        using JobExecutionInstrumentation instrumentation =
            new(_meterFactory, store, timeProvider);

        RunningJobRegistry[] registries = [.. Enumerable.Range(0, Engines).Select(_ => new RunningJobRegistry())];
        JobExecutionEngine[] engines =
        [
            .. registries.Select(registry => new JobExecutionEngine(
                store,
                new JobHandlerRegistry([handler]),
                registry,
                new JobQueueSignal(),
                timeProvider,
                instrumentation,
                new NullJobTraceContextStore(),
                NullLogger<JobExecutionEngine>.Instance)),
        ];

        // 復旧は Job を入れる前に済ませる。走行中に立ち上げる状況は別の話（そちらは
        // JobExecutionEngineConcurrencyTests に置いてある）。
        foreach (JobExecutionEngine engine in engines)
        {
            await engine.EnsureRecoveredAsync(CancellationToken.None);
        }

        CancelJobHandler cancelling = new(
            store,
            new CompositeRunningJobRegistry(registries),
            timeProvider,
            NullLogger<CancelJobHandler>.Instance);

        List<JobId> ids = [];
        for (int i = 0; i < Jobs; i++)
        {
            JobId id = JobId.From($"job-{i:D3}");
            await _store.AddAsync(
                Job.Create(id, $"Job {id.Value}", HandledJobType, id.Value, Origin.AddSeconds(i)),
                CancellationToken.None);
            ids.Add(id);
        }

        bool draining = true;
        List<Exception> failures = [];
        AsyncStartGate start = new(Engines + Cancellers + 1);

        Task running = Task.WhenAll(engines.Select(engine => Task.Run(async () =>
        {
            await start.SignalAndWaitAsync();

            try
            {
                while (await engine.RunOnceAsync(CancellationToken.None))
                {
                    // 待機中の Job が尽きるまで回す。
                }
            }
            catch (Exception exception)
            {
                Collect(failures, exception);
            }
        })));

        Task cancellers = Task.WhenAll(Enumerable.Range(0, Cancellers).Select(worker => Task.Run(async () =>
        {
            Random random = new(Seed + worker);
            await start.SignalAndWaitAsync();

            while (Volatile.Read(ref draining))
            {
                try
                {
                    await RequestCancelAsync(cancelling, store, ids[random.Next(ids.Count)], random);
                }
                catch (Exception exception)
                {
                    Collect(failures, exception);
                    return;
                }

                await Task.Yield();
            }
        })));

        Task observing = Task.Run(async () =>
        {
            Random random = new(Seed + 991);
            await start.SignalAndWaitAsync();

            while (Volatile.Read(ref draining))
            {
                JobId id = ids[random.Next(ids.Count)];
                await log.ObserveAsync(() => store.FindAsync(id, CancellationToken.None));
                await Task.Yield();
            }
        });

        await running;
        Volatile.Write(ref draining, false);
        await Task.WhenAll(cancellers, observing);

        // 最後の姿も観測に加える。終端まで含めた列で検査したい。
        foreach (JobId id in ids)
        {
            await log.ObserveAsync(() => store.FindAsync(id, CancellationToken.None));
        }

        Assert.True(
            failures.Count == 0,
            $"seed={Seed} 例外が漏れました:\n{string.Join("\n---\n", failures.Take(3))}");

        IReadOnlyList<string> violations = log.FindViolations();
        Assert.True(violations.Count == 0, $"seed={Seed}\n{string.Join("\n", violations.Take(10))}");

        foreach (JobId id in ids)
        {
            Job job = await _store.FindAsync(id, CancellationToken.None)
                ?? throw new InvalidOperationException($"Job {id} が保存されていません。");

            Assert.True(job.Status.IsTerminal(), $"seed={Seed} Job {id} が {job.Status} で残りました。");

            // 二重実行はここでしか捕まえられない。状態は最後のひとつしか残らないので、
            // 2 回動かしても状態からは分からない。
            int executions = handler.Executions.GetValueOrDefault(id.Value);
            Assert.True(executions <= 1, $"seed={Seed} Job {id} のハンドラが {executions} 回起動しました。");
        }

        Assert.True(log.AcceptedUpdateCount > 0, $"seed={Seed} 状態が 1 度も進んでいません。");
    }

    /// <summary>
    /// キャンセルを 1 件要求する。待機中の Job はたいてい見送る。
    /// </summary>
    /// <remarks>
    /// 何も見ずに要求すると、待機中のうちに全部が Cancelled へ落ちてエンジンが
    /// 1 件も動かない回ができる（実測でそうなった）。それでは実行とキャンセルが
    /// ぶつかる窓を見たことにならない。画面から実行中の Job を止める操作に近づける。
    /// </remarks>
    private static async Task RequestCancelAsync(
        CancelJobHandler cancelling,
        IJobStore store,
        JobId id,
        Random random)
    {
        Job? job = await store.FindAsync(id, CancellationToken.None);
        if (job is { Status: JobStatus.Queued } && random.Next(8) != 0)
        {
            return;
        }

        await cancelling.HandleAsync(id.Value, CancellationToken.None);
    }

    private static void Collect(List<Exception> failures, Exception exception)
    {
        lock (failures)
        {
            failures.Add(exception);
        }
    }

    /// <summary>
    /// 複数のエンジンの登録簿へまとめて伝える。
    /// </summary>
    /// <remarks>
    /// 本番ではエンジンは 1 プロセスに 1 つで、登録簿も 1 つ。ここではエンジンを
    /// 複数立てているので、キャンセルの伝達先も同じ数だけある。
    /// どれか 1 つが持っていれば伝わったことにする。
    /// </remarks>
    private sealed class CompositeRunningJobRegistry : IRunningJobRegistry
    {
        private readonly IReadOnlyList<RunningJobRegistry> _registries;

        public CompositeRunningJobRegistry(IReadOnlyList<RunningJobRegistry> registries) =>
            _registries = registries;

        public bool TryRequestCancel(JobId id)
        {
            bool delivered = false;
            foreach (RunningJobRegistry registry in _registries)
            {
                delivered |= registry.TryRequestCancel(id);
            }

            return delivered;
        }
    }
}
