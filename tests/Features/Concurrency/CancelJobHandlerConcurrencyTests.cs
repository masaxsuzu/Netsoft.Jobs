using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.CancelJob;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// キャンセル要求の読み直しループを、競合し続ける状況で調べる。
/// </summary>
/// <remarks>
/// <see cref="CancelJobHandler"/> にも実行エンジンと同じ「状態機械が一方通行だから
/// やり直しは有限で止まる」という注記がある。毎回横から状態を進める store を当てて確かめる。
/// </remarks>
public sealed class CancelJobHandlerConcurrencyTests : IDisposable
{
    /// <summary>止まらないループを試験の停止に変えるための保険。実装が正しければ発火しない。</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset Now = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task 書き戻しが競合し続けてもやり直しは有限で止まる()
    {
        await _store.AddAsync(
            Job.Create(JobId.From("job-1"), "競合", "test-job", string.Empty, Now),
            CancellationToken.None);

        RelentlessJobStore hostile = new(_store, Now) { Interfering = true };
        CancelJobHandler handler = new(
            hostile,
            new RunningJobRegistry(),
            new FixedTimeProvider(Now),
            NullLogger<CancelJobHandler>.Instance);

        CancelJobResult result = await handler.HandleAsync("job-1", CancellationToken.None).WaitAsync(HangGuard);

        // 追い抜かれ続けた末に、状態機械が「もう効いている」か「もう終わっている」で決着させる。
        Assert.NotNull(result.Rejection);

        Job job = await FindAsync("job-1");
        Assert.Null(JobInvariants.FindViolation(job));

        // Queued → Running → Cancelling と進む間に高々 3 回。上限を置いて青天井でないことを示す。
        Assert.InRange(hostile.UpdateAttempts, 1, 5);
    }

    /// <summary>
    /// 実行エンジンが結末を書いた直後にキャンセルが来ても、終端を上書きしないこと。
    /// </summary>
    /// <remarks>
    /// 条件付き更新を入れた元の動機がこれ。割り込みで決定的に起こす。
    /// </remarks>
    [Fact]
    public async Task 終端が書かれた後のキャンセルは上書きせずに拒否される()
    {
        Job job = Job.Create(JobId.From("job-1"), "競合", "test-job", string.Empty, Now);
        await _store.AddAsync(job, CancellationToken.None);

        Assert.True(job.Apply(JobTrigger.Start, Now).IsAllowed);
        Assert.True(await _store.UpdateAsync(job, JobStatus.Queued, CancellationToken.None));

        InterferingJobStore interfering = new(_store);
        CancelJobHandler handler = new(
            interfering,
            new RunningJobRegistry(),
            new FixedTimeProvider(Now),
            NullLogger<CancelJobHandler>.Instance);

        // 読み出しの後、書き戻しの直前にエンジンが完了を書き込む。
        interfering.BeforeNextUpdate = async () =>
        {
            Job finishing = await FindAsync("job-1");
            Assert.True(finishing.Apply(JobTrigger.Complete, Now).IsAllowed);
            Assert.True(await _store.UpdateAsync(finishing, JobStatus.Running, CancellationToken.None));
        };

        CancelJobResult result = await handler.HandleAsync("job-1", CancellationToken.None).WaitAsync(HangGuard);

        Assert.False(result.IsSuccess);
        Assert.Equal(JobTransitionRejection.JobAlreadyFinished, result.Rejection);
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-1")).Status);
    }

    /// <summary>
    /// エンジンが Running を書き戻した直後、まだハンドラを起動していない時点で
    /// キャンセルが来ても、トークンが発火して Job が Cancelled で終わること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 状態を公開してから受け口（<see cref="RunningJobRegistry"/> への登録）を用意するまでに
    /// 窓があると、要求は受理されて Cancelling が書けるのに
    /// <see cref="IRunningJobRegistry.TryRequestCancel"/> が false を返し、
    /// トークンが永久に発火しない。状態は壊れないが、キャンセルが黙って効かない
    /// Job ができる（E2E が 30 秒待たされる形で実際に踏んだ）。
    /// </para>
    /// <para>
    /// 登録を書き戻しより前に済ませていれば、窓は構造的に開かない。
    /// このテストはその順序が保たれていることを、最悪の瞬間を決定的に作って固定する。
    /// </para>
    /// <para>
    /// 判定は結末（Cancelled で終わったか）ではなく<b>不変条件そのもの</b>で行う。
    /// 「Running が見えた瞬間に受け口が在る」を割り込みの中で直接見る。結末だけを見ると、
    /// 順序を戻したときの症状が「ハンドラが永久に待つ」＝<see cref="HangGuard"/> での
    /// タイムアウトになり、原因を何も言わないメッセージが 30 秒かけて出る。
    /// 原因の側で落とせば即座に、しかも名指しで落ちる。
    /// 結末の確認は不変条件が守られた結果として後ろに残してある。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 実行開始の直後に届いたキャンセルもハンドラに伝わる()
    {
        await _store.AddAsync(
            Job.Create(JobId.From("job-1"), "窓", "test-job", string.Empty, Now),
            CancellationToken.None);

        RunningJobRegistry runningJobs = new();
        InterferingJobStore interfering = new(_store);
        ControllableJobHandler handler = new("test-job");

        CancelJobHandler cancel = new(
            _store,
            runningJobs,
            new FixedTimeProvider(Now),
            NullLogger<CancelJobHandler>.Instance);

        using TestMeterFactory meterFactory = new();
        using JobExecutionInstrumentation instrumentation = new(
            meterFactory,
            _store,
            new FixedTimeProvider(Now),
            new NullJobTraceContextStore(),
            NullLogger<JobExecutionInstrumentation>.Instance);

        JobExecutionEngine engine = await JobExecutionEngine.StartAsync(
            interfering,
            new JobHandlerRegistry([handler]),
            runningJobs,
            new JobQueueSignal(),
            new FixedTimeProvider(Now),
            instrumentation,
            NullLogger<JobExecutionEngine>.Instance,
            CancellationToken.None);

        // Running が保存された瞬間＝画面がキャンセルを受け付けられるようになった瞬間に要求する。
        interfering.AfterNextUpdate = async () =>
        {
            CancelJobResult result = await cancel.HandleAsync("job-1", CancellationToken.None);

            // ここは順序が壊れていても通る。CancelJobHandler は TryRequestCancel の戻り値を
            // 見ない（見ないのが正しい）ので、受け口が無くても状態は Cancelling まで進む。
            Assert.True(result.IsSuccess);

            // 守りたい不変条件はこちら。状態が Running として公開されたこの瞬間に、
            // 受け口が既に在ること。既にキャンセル済みの CTS でも true が返るので、
            // 上の要求と二重になっても壊れない。
            Assert.True(
                runningJobs.TryRequestCancel(JobId.From("job-1")),
                "Running が見えているのに RunningJobRegistry に登録が無い。"
                    + "Track が書き戻しより後ろに戻っている（JobExecutionEngine.RunOnceAsync の注記を参照）。");
        };

        Assert.True(await engine.RunOnceAsync(CancellationToken.None).WaitAsync(HangGuard));

        // ハンドラは最後まで走らずキャンセルを観測した。効かなければ Completed になる。
        Assert.Equal(JobStatus.Cancelled, (await FindAsync("job-1")).Status);
        Assert.True(handler.CancellationObserved);
    }

    private async Task<Job> FindAsync(string id) =>
        await _store.FindAsync(JobId.From(id), CancellationToken.None)
        ?? throw new InvalidOperationException($"Job {id} が保存されていません。");
}
