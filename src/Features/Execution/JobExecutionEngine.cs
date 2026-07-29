using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 待機中の Job を 1 件ずつ取り出して実行する。
/// </summary>
/// <remarks>
/// <para>
/// <b>このエンジンはプロセス内で 1 つだけ動かすこと。同時実行数は 1 である。</b>
/// <see cref="IJobStore.FindOldestQueuedAsync"/> は「取得」であって「取得して予約」ではないため、
/// ディスパッチが同時に 2 つ動くと同じ Job を二重に拾える。取得と Start の間に
/// 他者を締め出す仕組みは store 側にも無い。
/// </para>
/// <para>
/// 将来並列化するなら、まず store 側に予約操作（Queued の 1 件を条件付き更新で Running にし、
/// 更新できた側だけがその Job を得る）を足すこと。ここでロックを持っても、
/// プロセスが 2 つ立った瞬間に意味を失う。あわせて
/// <see cref="RunningJobRegistry"/> を Job 単位の辞書に変える必要がある。
/// </para>
/// <para>
/// ホスティング（常駐させる殻）には依存しない。1 回分を進める <see cref="RunOnceAsync"/> と、
/// それを繰り返す <see cref="RunAsync"/> を外から呼ぶ形にしてある。
/// こうしておかないとテストがホストを立てないと回せない。
/// </para>
/// </remarks>
public sealed class JobExecutionEngine
{
    /// <summary>起動時復旧で記録する失敗理由。</summary>
    private const string CrashRecoveryMessage = "前回のプロセスが異常終了したため、実行結果を確認できません。";

    // ハンドラが動いていたはずの状態。Queued を含めないことの理由は
    // JobStatusExtensions.IsHandlerActive に書いてある。
    private static readonly JobStatus[] HandlerActiveStatuses =
        [.. Enum.GetValues<JobStatus>().Where(status => status.IsHandlerActive())];

    private readonly IJobStore _store;
    private readonly JobHandlerRegistry _handlers;
    private readonly RunningJobRegistry _runningJobs;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JobExecutionEngine> _logger;

    // 起動時復旧が済んだか。エンジンは 1 つしか動かない前提なので排他で守らない。
    private bool _recovered;

    public JobExecutionEngine(
        IJobStore store,
        JobHandlerRegistry handlers,
        RunningJobRegistry runningJobs,
        TimeProvider timeProvider,
        ILogger<JobExecutionEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(runningJobs);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _handlers = handlers;
        _runningJobs = runningJobs;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// 起動時復旧を 1 度だけ実行する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 前回のプロセスが異常終了すると、実際には誰も動いていないのに
    /// Running / Cancelling のまま残った Job ができる。結果が分からない以上 Failed で閉じる。
    /// Queued は対象外。ハンドラを起動していないので副作用が無く、このプロセスがそのまま実行する。
    /// </para>
    /// <para>
    /// 実行より先に走ることは <see cref="RunOnceAsync"/> と <see cref="RunAsync"/> の
    /// 先頭でこれを await することで保証している。復旧を呼び忘れたまま実行できる経路を作らない。
    /// 逆順にすると、このプロセスが Running にした Job を復旧が Failed で上書きしうる。
    /// 明示的に呼ぶこともできるが、呼ばなくても実行前に必ず走る。
    /// </para>
    /// </remarks>
    public async Task EnsureRecoveredAsync(CancellationToken cancellationToken)
    {
        if (_recovered)
        {
            return;
        }

        foreach (JobStatus status in HandlerActiveStatuses)
        {
            IReadOnlyList<Job> jobs = await _store.ListByStatusAsync(status, cancellationToken);

            foreach (Job job in jobs)
            {
                JobTransitionResult result = job.Apply(
                    JobTrigger.RecoverAfterCrash,
                    _timeProvider.GetUtcNow(),
                    CrashRecoveryMessage);

                if (result.IsAllowed)
                {
                    await _store.UpdateAsync(job, cancellationToken);
                    _logger.LogWarning("Job {JobId} を前回プロセスの異常終了として Failed にしました。", job.Id.Value);
                }
            }
        }

        // 途中で例外が出たときは立てない。次の呼び出しでもう一度試す。
        _recovered = true;
    }

    /// <summary>
    /// 1 回分の処理を進める。待機中の Job があれば 1 件だけ実行し、終わるまで待つ。
    /// </summary>
    /// <returns>Job を 1 件実行したなら true。実行対象が無ければ false。</returns>
    /// <remarks>
    /// ハンドラが投げた例外はこのメソッドの外に出ない。Job の失敗として記録して正常に返す。
    /// 1 件の失敗が次の Job の実行を妨げてはいけないため。
    /// </remarks>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await EnsureRecoveredAsync(cancellationToken);

        Job? job = await _store.FindOldestQueuedAsync(cancellationToken);
        if (job is null)
        {
            return false;
        }

        JobTransitionResult started = job.Apply(JobTrigger.Start, _timeProvider.GetUtcNow());
        if (!started.IsAllowed)
        {
            // Queued を指定して取ったものが Queued でない。同時実行数 1 の前提が破れている。
            // 二重に実行するより、今回は何もしなかったことにして次の周回に任せる。
            _logger.LogWarning("Job {JobId} は既に他から開始されています。今回は実行しません。", job.Id.Value);
            return false;
        }

        await _store.UpdateAsync(job, cancellationToken);

        await RunHandlerAsync(job);
        return true;
    }

    /// <summary>
    /// 待機中の Job が無くなるまで実行し、無ければ <paramref name="idleInterval"/> だけ待って繰り返す。
    /// </summary>
    /// <remarks>
    /// 止めるのは <paramref name="cancellationToken"/> だけ。停止要求は実行中のハンドラには伝えない。
    /// プロセスの停止は利用者のキャンセル要求ではないので、Cancelled として記録すると嘘になる。
    /// 途中で強制終了された Job は、次の起動時に復旧が Failed として閉じる。
    /// </remarks>
    public async Task RunAsync(TimeSpan idleInterval, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleInterval, TimeSpan.Zero);

        await EnsureRecoveredAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            bool executed;
            try
            {
                executed = await RunOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // store の障害などループ自体の例外。1 回の失敗でループを終わらせると、
                // 復旧した後も Job が一切動かないプロセスが残ってしまう。
                _logger.LogError(exception, "Job の実行に失敗しました。待機してから再試行します。");
                executed = false;
            }

            if (executed)
            {
                // 続けて次を拾う。溜まっている間は待たない。
                continue;
            }

            try
            {
                await Task.Delay(idleInterval, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// ハンドラを呼び、その結末を状態遷移として記録する。
    /// </summary>
    private async Task RunHandlerAsync(Job job)
    {
        // ループの停止トークンとは繋がない。上の RunAsync の注記のとおり、
        // プロセス停止はキャンセル要求ではない。ここが発火するのは利用者のキャンセルだけ。
        using CancellationTokenSource cancellation = new();

        JobTrigger trigger;
        string? failureMessage = null;

        try
        {
            IJobHandler? handler = _handlers.Find(job.JobType);
            if (handler is null)
            {
                // 例外にしない。エンジンが止まると他の Job まで動かなくなるので、
                // この Job だけを失敗として閉じる。
                trigger = JobTrigger.Fail;
                failureMessage = $"JobType \"{job.JobType}\" に対応するハンドラが登録されていません。";
            }
            else
            {
                using (_runningJobs.Track(job.Id, cancellation))
                {
                    await handler.ExecuteAsync(job.Parameters, cancellation.Token);
                }

                trigger = JobTrigger.Complete;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 自分が渡したトークンで終わった場合だけキャンセルとして扱う。
            // 別のトークンで中断されたのなら、それは利用者の意図ではないので失敗。
            trigger = JobTrigger.ConfirmCancelled;
        }
        catch (Exception exception)
        {
            trigger = JobTrigger.Fail;
            failureMessage = Describe(exception);
        }

        await FinishAsync(job.Id, trigger, failureMessage);
    }

    /// <summary>
    /// 実行の結末を保存する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 手元の <see cref="Job"/> を使わずに読み直すのは、ハンドラが動いている間に
    /// キャンセル要求が状態を Cancelling へ進めている可能性があるから。
    /// 古いインスタンスに遷移を適用すると、その更新でキャンセル要求を消してしまう。
    /// </para>
    /// <para>
    /// ここだけは停止要求で中断しない。既に起きたことを書き残す処理なので、
    /// 中断すると完了した Job が Running のまま残り、次の起動で復旧に Failed とされてしまう。
    /// </para>
    /// </remarks>
    private async Task FinishAsync(JobId id, JobTrigger trigger, string? failureMessage)
    {
        Job? job = await _store.FindAsync(id, CancellationToken.None);
        if (job is null)
        {
            _logger.LogWarning("Job {JobId} は実行後に見つかりませんでした。結末を記録できません。", id.Value);
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        JobTransitionResult result = job.Apply(trigger, now, failureMessage);
        if (!result.IsAllowed)
        {
            // 状態機械の判断に従う。ただし終端に達していない Job を放置すると
            // 誰も動かしていないのに Running のまま残るので、失敗として閉じる。
            if (job.Status.IsTerminal())
            {
                return;
            }

            job.Apply(
                JobTrigger.Fail,
                now,
                $"実行の結末 {trigger} を状態 {job.Status} に記録できませんでした。");
        }

        await _store.UpdateAsync(job, CancellationToken.None);
    }

    /// <summary>
    /// 例外を失敗理由の文言にする。
    /// </summary>
    /// <remarks>
    /// 型名を添えるのは、Message が空の例外でも何が起きたか分かるようにするため。
    /// スタックトレースは利用者に見せる情報ではないので含めない。
    /// </remarks>
    private static string Describe(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : $"{exception.GetType().Name}: {exception.Message}";
}
