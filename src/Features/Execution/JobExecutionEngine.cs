using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 待機中の Job を 1 件ずつ取り出して実行する。
/// </summary>
/// <remarks>
/// <para>
/// <b>同時実行数が 1 なのは、このエンジン 1 インスタンスあたりの話である。</b>
/// システム全体で 1 件ずつしか動かないという意味ではない。エンジンが複数動けば
/// その数だけ Job が並行する。
/// </para>
/// <para>
/// 二重実行が起きないことと、状態が壊れないことは <see cref="IJobStore.UpdateAsync"/> の
/// 条件付き更新が保証する。Queued の候補を取ってから Running を書き戻すまでに他が先に取っていれば
/// 書き戻しに失敗するので、エンジンが複数動いても（別プロセスでも）同じ Job を 2 回は実行しない。
/// 正しさをエンジンの数の前提に置いていない
/// （起動時復旧だけは別の前提を置いている。<see cref="EnsureRecoveredAsync"/> の注記を参照）。
/// </para>
/// <para>
/// ただし<b>キャンセルの伝達は同一プロセス内に限る</b>。<see cref="RunningJobRegistry"/> は
/// プロセス内の辞書なので、別プロセスで走っている Job にトークンは届かない。
/// その場合その Job は <see cref="JobStatus.Cancelling"/> のまま、実際に走らせているプロセスが
/// 完了か失敗を書くまで止まらない（状態機械が Cancelling からの Complete / Fail を
/// 認めているのは、この決着を許すため）。プロセスをまたいでキャンセルを効かせたいなら、
/// 伝達の口を DB など共有の場所へ移すこと。
/// </para>
/// <para>
/// ホスティング（常駐させる殻）には依存しない。1 回分を進める <see cref="RunOnceAsync"/> と、
/// <see cref="JobQueueSignal"/> の合図を待ちながらそれを繰り返す <see cref="RunAsync"/> を
/// 外から呼ぶ形にしてある。こうしておかないとテストがホストを立てないと回せない。
/// 合図を誰が鳴らすか（store の書き込みとの結線）はホスト側の仕事。
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
    private readonly JobQueueSignal _signal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JobExecutionEngine> _logger;

    // 起動時復旧が済んだか。インスタンスごとに持つので、エンジンを複数立てれば
    // その数だけ復旧が走る。復旧も条件付き更新で書くため、重なっても壊れない
    // （先に書けた 1 つだけが Failed を記録し、残りは書き戻せずに見送る）。
    // 1 インスタンス内では実行が始まる前に 1 度で済ませたいだけなので、排他で守らない。
    private bool _recovered;

    public JobExecutionEngine(
        IJobStore store,
        JobHandlerRegistry handlers,
        RunningJobRegistry runningJobs,
        JobQueueSignal signal,
        TimeProvider timeProvider,
        ILogger<JobExecutionEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(runningJobs);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _handlers = handlers;
        _runningJobs = runningJobs;
        _signal = signal;
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
    /// <para>
    /// 復旧は Running / Cancelling をすべて「前回の残骸」とみなす。条件付き更新はこの見立て自体は
    /// 守ってくれないので、既に別のエンジンが動いている最中に新しいエンジンを立てると、
    /// 本当に走っている Job まで Failed で閉じてしまう。復旧はプロセスの立ち上げ時、
    /// つまり誰も走っていないところから始めるときのものである。
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

                if (!result.IsAllowed)
                {
                    continue;
                }

                // 書き戻せなかったのは他が先にこの Job を処理したということなので、
                // 復旧の対象ではなくなっている。読み直して試し直さずに次へ進む。
                if (await _store.UpdateAsync(job, result.Previous, cancellationToken))
                {
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
    /// <para>
    /// ハンドラが投げた例外はこのメソッドの外に出ない。Job の失敗として記録して正常に返す。
    /// 1 件の失敗が次の Job の実行を妨げてはいけないため。
    /// </para>
    /// <para>
    /// 候補の取得と Running の書き戻しの間に他が同じ Job を取ることはありうる。
    /// 取れた側だけが実行するので、二重に実行されることはない。
    /// </para>
    /// </remarks>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await EnsureRecoveredAsync(cancellationToken);

        // 取れる候補が無くなるまで繰り返す。書き戻せなかったということは、
        // その Job は他によって Queued から先へ進められたということなので、
        // 次の FindOldestQueuedAsync はもう同じ Job を返さない。
        // 状態機械は終端へ向かう一方通行で、Queued へ戻る遷移は無い。
        // よって候補は減る一方であり、このループは必ず止まる。
        while (true)
        {
            Job? job = await _store.FindOldestQueuedAsync(cancellationToken);
            if (job is null)
            {
                return false;
            }

            JobTransitionResult started = job.Apply(JobTrigger.Start, _timeProvider.GetUtcNow());
            if (!started.IsAllowed)
            {
                // Queued で絞って取ったものが Queued でない。store の実装が契約を守っていない。
                // ここで次の候補へ進むと同じ Job を延々と拾い続けるので、今回の周回を諦める。
                _logger.LogWarning("Job {JobId} は既に他から開始されています。今回は実行しません。", job.Id.Value);
                return false;
            }

            // ここで書き戻せた 1 つだけがこの Job を実行する。
            if (!await _store.UpdateAsync(job, started.Previous, cancellationToken))
            {
                _logger.LogInformation("Job {JobId} は他から開始されました。次の候補を探します。", job.Id.Value);
                continue;
            }

            await RunHandlerAsync(job);
            return true;
        }
    }

    /// <summary>
    /// 待機中の Job が無くなるまで実行し、無ければ <see cref="JobQueueSignal"/> の合図を待って繰り返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// アイドル時のポーリングは持たない。Job の全書き込みは store のデコレータ
    /// （Web の NotifyingJobStore）を通って合図に繋がっており、エンジンとは同一プロセスなので、
    /// 合図は取りこぼしなく届く（<see cref="JobQueueSignal"/> の注記を参照）。
    /// 合図が消えるのはプロセスが死ぬときだけで、その分は次の起動でこのループの先頭の
    /// 「無くなるまで実行」が拾う。安全網のポーリングは意図的に置いていない。
    /// </para>
    /// <para>
    /// 止めるのは <paramref name="cancellationToken"/> だけ。停止要求は実行中のハンドラには伝えない。
    /// プロセスの停止は利用者のキャンセル要求ではないので、Cancelled として記録すると嘘になる。
    /// 途中で強制終了された Job は、次の起動時に復旧が Failed として閉じる。
    /// </para>
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
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
                // 合図待ちへ落ちても再試行の機会は失われない。store が壊れている間は
                // 書き込み（= 新しい仕事の供給）も失敗しているはずで、回復後の最初の
                // 書き込みが合図をくれる。
                _logger.LogError(exception, "Job の実行に失敗しました。次の合図で再試行します。");
                executed = false;
            }

            if (executed)
            {
                // 続けて次を拾う。溜まっている間は待たない。
                continue;
            }

            // 待つのは FindOldestQueuedAsync が null を返した後（executed が false になった後）
            // であって、待ちに入る前に確認し直すことはしない。「null を見た直後に登録された」
            // 場合、その合図はこの WaitAsync より先に発火しているが、トークンが箱に残るので
            // この待ちは即座に返る。確認と待機の間に取りこぼしの窓は無い。
            // エンジン自身の書き込み（Running / 終端）も合図を発火させるため余分に起きることが
            // あるが、空スキャン 1 回（実測 27 µs）で待ちに戻るだけで無害。
            try
            {
                await _signal.WaitAsync(cancellationToken);
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
        // ハンドラは parameters しか受け取らず、自分がどの Job かを知らない（意図的な設計）。
        // スコープに積んでおけば、ハンドラや await の継続が将来書くログ行すべてに
        // JobId と JobType が自動で付き、JobId で絞れば Job の一生が並ぶ。
        using IDisposable? scope = _logger.BeginScope("Job {JobId} ({JobType})", job.Id.Value, job.JobType);

        // Running を書き戻せた直後、つまりこの Job を実行すると確定した点の記録。
        _logger.LogInformation("Job {JobId} ({JobType}) の実行を開始します。", job.Id.Value, job.JobType);

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
    /// <para>
    /// 読み直してから書き戻すまでの間にも状態は動きうる（キャンセル要求が届く、など）ので、
    /// 書き戻せなければ読み直して評価をやり直す。状態機械は終端へ向かう一方通行で、
    /// 書き戻せなかったということは相手が状態を先へ進めたということ。
    /// 終端に達すれば以後は動かず、そこでは遷移が拒否されて抜けるので、やり直しは必ず有限で止まる。
    /// </para>
    /// </remarks>
    private async Task FinishAsync(JobId id, JobTrigger trigger, string? failureMessage)
    {
        while (true)
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

            // 拒否されて Fail を再適用した場合も、状態は 1 回目の拒否で変わっていないので
            // 期待状態は同じ。どちらの経路でも result.Previous が読み出したときの状態を指す。
            if (await _store.UpdateAsync(job, result.Previous, CancellationToken.None))
            {
                // Cancelling からの完走は、Job 行からキャンセル要求の痕跡が消える唯一のケース
                // （Cancelling の時刻はどの列にも残らない）。「要求はあったが完走が勝った」ことの
                // 記録はこのログだけになるので、文言で区別できるようにする。
                if (result.Previous == JobStatus.Cancelling && job.Status == JobStatus.Completed)
                {
                    _logger.LogInformation(
                        "Job {JobId} はキャンセル要求より完走が勝ち、{Status} で終了しました。契機は {Trigger} です。",
                        id.Value,
                        job.Status,
                        trigger);
                }
                else
                {
                    _logger.LogInformation(
                        "Job {JobId} は {Status} で終了しました。契機は {Trigger} です。",
                        id.Value,
                        job.Status,
                        trigger);
                }

                return;
            }

            _logger.LogInformation("Job {JobId} の状態が書き換わっていました。読み直して記録し直します。", id.Value);
        }
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
