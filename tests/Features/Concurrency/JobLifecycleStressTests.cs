using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.CancelJob;
using Netsoft.Jobs.Features.EditJob;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.PauseJob;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// 実行エンジン・キャンセル・編集・一時停止・再開・読み取りを、本物の store の上で同時に動かす。
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
    private const int Operators = 2;
    private const int OperationsPerWorker = 200;

    private static readonly DateTimeOffset Origin = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly TemporarySubTaskStore _subTasks = new();
    private readonly TestMeterFactory _meterFactory = new();

    public void Dispose()
    {
        _meterFactory.Dispose();
        _subTasks.Dispose();
        _store.Dispose();
    }

    [Fact]
    public async Task 実行とキャンセルと編集と一時停止を同時に走らせても状態が壊れない()
    {
        JobObservationLog log = new();
        RecordingJobStore store = new(_store, log);

        // 時計は登録より後ろに固定する。エンジンが書く StartedAt / FinishedAt が
        // CreatedAt より前になると、実装ではなく試験の作り方で不変条件が破れる。
        FixedTimeProvider timeProvider = new(Origin.AddMinutes(1));

        CountingJobHandler handler = new(HandledJobType) { Yields = 3 };

        using JobExecutionInstrumentation instrumentation =
            new(_meterFactory, store, timeProvider, new NullJobTraceContextStore(), NullLogger<JobExecutionInstrumentation>.Instance);

        RunningJobRegistry[] registries = [.. Enumerable.Range(0, Engines).Select(_ => new RunningJobRegistry())];

        // 立ち上げの時点で復旧は済んでいる。Job を入れるのはこの後なので、
        // 走行中の Job を復旧が見ることはない（走行中に立ち上げる状況は別の話で、
        // そちらは JobExecutionEngineConcurrencyTests に置いてある）。
        JobExecutionEngine[] engines = await Task.WhenAll(
            registries.Select(registry => JobExecutionEngine.StartAsync(
                store,
                new JobHandlerRegistry([handler]),
                registry,
                new JobQueueSignal(),
                timeProvider,
                instrumentation,
                NullLogger<JobExecutionEngine>.Instance,
                CancellationToken.None)));

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

        // 画面から届く残りの操作。編集は状態を動かさずに版だけ進めるので、
        // 「読み出しから書き戻しまでの間に版が変わる」窓をエンジンとキャンセルの両方に作る
        // ── 版で守るようにしてから現れた形で、状態だけを見ていたころは素通りしていた。
        EditJobHandler editing = new(store, _subTasks, NullLogger<EditJobHandler>.Instance);
        PauseJobHandler pausing = new(store, timeProvider, NullLogger<PauseJobHandler>.Instance);
        ResumeJobHandler resuming = new(store, timeProvider, NullLogger<ResumeJobHandler>.Instance);

        bool draining = true;
        int acceptedOperations = 0;
        int operatorsLeft = Operators;

        // 最初の 1 操作が済むまでエンジンとキャンセルを待たせる。この時点では全部が待機中
        // なので、編集も一時停止も再開も必ず受理される（Queued はどれも受け付ける状態）。
        // 待たせないと、操作が始まる前に全部終端へ行き着く回ができて
        // 「1 度も受け付けられていません」でフレークする（実際に 8 回中 4 回落ちた）。
        // 残りの操作は今までどおり全部と重なるので、見ている窓は狭まらない。
        TaskCompletionSource firstOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Exception> failures = [];
        AsyncStartGate start = new(Engines + Cancellers + Operators + 1);

        // 待機中の Job が尽きても、操作が続いている限り降りない。保留（Queued → Paused）が
        // 入ると待ち行列は一瞬で空になるので、枯れたら即降りる作りだと操作の窓が消える
        //（実際に「1 度も受け付けられていません」でフレークした）。
        Task running = Task.WhenAll(engines.Select(engine => Task.Run(async () =>
        {
            await start.SignalAndWaitAsync();
            await firstOperation.Task;

            try
            {
                while (true)
                {
                    if (await engine.RunOnceAsync(CancellationToken.None))
                    {
                        continue;
                    }

                    if (Volatile.Read(ref operatorsLeft) == 0)
                    {
                        return;
                    }

                    await Task.Yield();
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
            await firstOperation.Task;

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

        Task operators = Task.WhenAll(Enumerable.Range(0, Operators).Select(worker => Task.Run(async () =>
        {
            Random random = new(Seed + 500 + worker);
            await start.SignalAndWaitAsync();

            for (int i = 0; i < OperationsPerWorker; i++)
            {
                JobId id = ids[random.Next(ids.Count)];

                try
                {
                    // 受け付けられるかは状態次第で、拒否も正常な結末。見ているのは
                    // 「どの順で割り込んでも状態が壊れないか」なので、拒否されても構わない。
                    // ただし 1 度も受け付けられていないなら割り込めていないので、数えておく
                    //（全部が拒否で終わる回では、この試験は何も見ていないことになる）。
                    bool accepted = random.Next(3) switch
                    {
                        0 => (await editing.HandleAsync(
                            id.Value, $"{random.Next(1, 5)} {random.Next(1, 5)}", CancellationToken.None)).IsSuccess,
                        1 => (await pausing.HandleAsync(id.Value, CancellationToken.None)).IsSuccess,
                        _ => (await resuming.HandleAsync(id.Value, CancellationToken.None)).IsSuccess,
                    };

                    if (accepted)
                    {
                        Interlocked.Increment(ref acceptedOperations);
                    }
                }
                catch (Exception exception)
                {
                    Collect(failures, exception);
                    break;
                }
                finally
                {
                    // 1 回目を終えたら他を放す。例外で抜けても放す（待たせたまま詰まらせない）。
                    firstOperation.TrySetResult();
                }

                await Task.Yield();
            }

            firstOperation.TrySetResult();
            Interlocked.Decrement(ref operatorsLeft);
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
        await Task.WhenAll(cancellers, operators, observing);

        // 保留された Job は終端に達しない（利用者が止めた状態なので、それが正しい姿）。
        // 全部再開してから、もう一度エンジンを回して決着させる。
        // 「止めたものを必ず動かし直せる」ことの確認でもあり、ここが通らなければ
        // 保留は行き止まりになっている。
        foreach (JobId id in ids)
        {
            await resuming.HandleAsync(id.Value, CancellationToken.None);
        }

        while (await engines[0].RunOnceAsync(CancellationToken.None))
        {
            // 再開したものが尽きるまで回す。
        }

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
        Assert.True(
            acceptedOperations > 0,
            $"seed={Seed} 編集・一時停止・再開が 1 度も受け付けられていません（割り込めていない）。"
            + $" 受理数={acceptedOperations}");
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
