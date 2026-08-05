using System.Diagnostics;

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
/// （起動時復旧だけは別の前提を置いている。<see cref="StartAsync"/> の注記を参照）。
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
    private readonly JobExecutionInstrumentation _instrumentation;
    private readonly ILogger<JobExecutionEngine> _logger;

    // 復旧が済んだかを表すフラグは持たない。このインスタンスが在ること自体が
    // 「復旧は済んだ」の証拠になっている（理由は StartAsync の注記に）。
    private JobExecutionEngine(
        IJobStore store,
        JobHandlerRegistry handlers,
        RunningJobRegistry runningJobs,
        JobQueueSignal signal,
        TimeProvider timeProvider,
        JobExecutionInstrumentation instrumentation,
        ILogger<JobExecutionEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(runningJobs);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(instrumentation);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _handlers = handlers;
        _runningJobs = runningJobs;
        _signal = signal;
        _timeProvider = timeProvider;
        _instrumentation = instrumentation;
        _logger = logger;
    }

    /// <summary>
    /// 起動時復旧をやり切ってから、実行できるエンジンを返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 前回のプロセスが異常終了すると、実際には誰も動いていないのに
    /// Running / Cancelling のまま残った Job ができる。結果が分からない以上 Failed で閉じる。
    /// Queued は対象外。ハンドラを起動していないので副作用が無く、このプロセスがそのまま実行する。
    /// </para>
    /// <para>
    /// <b>復旧を経ないとインスタンスが手に入らない形にしてある。</b>
    /// 以前は「済んだか」の bool を持ち、実行の入口で毎回それを見ていた。その作りだと
    /// 「まだ」と「走っている最中」を区別できず、復旧が走っている最中に実行が入ると
    /// 二重に復旧が走る。片方が Job を Running にした直後にもう片方がそれを読み、
    /// 「前回の残骸」とみなして Failed で閉じてしまう。
    /// 条件付き更新はこれを防げない。期待した状態（Running）は合っていて、
    /// 間違っているのは「Running ＝ 残骸」という見立ての方だから。
    /// </para>
    /// <para>
    /// ここでは復旧中にインスタンスがまだ存在しないので、その重なりは起こしようがない。
    /// 排他で守るのではなく、表現できなくする。「復旧を呼び忘れたまま実行する」も同時に消える。
    /// </para>
    /// <para>
    /// 復旧は Running / Cancelling をすべて「前回の残骸」とみなす。条件付き更新はこの見立て自体は
    /// 守ってくれないので、既に別のエンジンが動いている最中に新しいエンジンを立てると、
    /// 本当に走っている Job まで Failed で閉じてしまう。復旧はプロセスの立ち上げ時、
    /// つまり誰も走っていないところから始めるときのものである。
    /// <b>これはインスタンスを 2 つ作らないという運用の前提で、型では守れない。</b>
    /// </para>
    /// </remarks>
    public static async Task<JobExecutionEngine> StartAsync(
        IJobStore store,
        JobHandlerRegistry handlers,
        RunningJobRegistry runningJobs,
        JobQueueSignal signal,
        TimeProvider timeProvider,
        JobExecutionInstrumentation instrumentation,
        ILogger<JobExecutionEngine> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        await RecoverAsync(store, timeProvider, logger, cancellationToken);

        return new JobExecutionEngine(
            store, handlers, runningJobs, signal, timeProvider, instrumentation, logger);
    }

    /// <summary>
    /// 前回のプロセスがやり残した Job を Failed で閉じる。
    /// </summary>
    /// <remarks>
    /// インスタンスを取らない static にしてあるのは、これが「エンジンが動く前」の仕事だから。
    /// フィールドから読めてしまうと、実行中にも呼べる形に戻る余地が残る。
    /// </remarks>
    private static async Task RecoverAsync(
        IJobStore store,
        TimeProvider timeProvider,
        ILogger<JobExecutionEngine> logger,
        CancellationToken cancellationToken)
    {
        foreach (JobStatus status in HandlerActiveStatuses)
        {
            IReadOnlyList<Job> jobs = await store.ListByStatusAsync(status, cancellationToken);

            foreach (Job job in jobs)
            {
                JobTransitionResult result = job.Apply(
                    JobTrigger.RecoverAfterCrash,
                    timeProvider.GetUtcNow(),
                    CrashRecoveryMessage);

                if (!result.IsAllowed)
                {
                    continue;
                }

                // 書き戻せなかったのは他が先にこの Job を処理したということなので、
                // 復旧の対象ではなくなっている。読み直して試し直さずに次へ進む。
                if (await store.UpdateAsync(job, result.Previous, cancellationToken))
                {
                    logger.LogWarning("Job {JobId} を前回プロセスの異常終了として Failed にしました。", job.Id.Value);
                }
            }
        }

        // 例外が出たらそのまま外へ出す。StartAsync がインスタンスを返さないので、
        // 復旧しそこねたまま実行が始まることはない。呼び出し側は作り直せばやり直せる。
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
        // 起動時復旧の呼び出しはここに無い。StartAsync が済ませてからでないと
        // このインスタンスが存在しないので、呼び忘れも重なりも起こりえない。
        //
        // 取れる候補が無くなるまで繰り返す。書き戻せなかったということは、
        // その Job は他によって Queued から先へ進められたということなので、
        // 次の FindOldestQueuedAsync はもう同じ Job を返さない。
        // Queued へ戻る道は一時停止からの再開（Paused + Resume）だけで、そこへ行くには
        // どこかのエンジンが実行し、利用者が停止と再開を要求する必要がある。
        // 負けた候補がこの周回中に戻ってくるとしたら、それは新しい仕事が届いたのと
        // 同じことで、空転ではない。同一プロセスでは Running を作るのは自分だけなので、
        // 自分が負けた候補が自分の周回中に Queued へ戻ることも無い。
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

            // キャンセルの受け口は Running を書き戻すより前に用意する。順序が逆だと、
            // 「DB は Running（＝画面はキャンセルを受け付ける）なのに、まだ登録されていない」
            // 窓ができる。その窓に要求が届くと Cancelling は書けてしまうのに
            // TryRequestCancel が false を返し、トークンが永久に発火しない。
            // 状態は壊れないが、キャンセルが黙って効かない Job ができる。
            //
            // この順序なら窓は構造的に開かない。要求が来られるのは Running が見えてからで、
            // Running が見えるのは書き戻しの後、登録はその前に済んでいる。
            // 間に何を挟んでも（観測の記録など）壊れないのが、隣接を約束で守るより強い。
            //
            // このトークンはループの停止トークンとは繋がない。上の RunAsync の注記のとおり、
            // プロセス停止はキャンセル要求ではない。ここが発火するのは利用者のキャンセルだけ。
            using CancellationTokenSource cancellation = new();
            using (_runningJobs.Track(job.Id, cancellation))
            {
                // ここで書き戻せた 1 つだけがこの Job を実行する。
                // 負けた場合は using が登録を外すので、他が実行する Job に
                // こちらのトークンが残ることはない。
                if (!await _store.UpdateAsync(job, started.Previous, cancellationToken))
                {
                    _logger.LogInformation("Job {JobId} は他から開始されました。次の候補を探します。", job.Id.Value);
                    continue;
                }

                // Running の確定＝待ち行列を抜けた点。待ち時間はここで確定する。
                _instrumentation.RecordStarted(job);

                await RunHandlerAsync(job, cancellation);
                return true;
            }
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
    /// <param name="job">Running を書き戻せた Job。</param>
    /// <param name="cancellation">
    /// キャンセル要求の受け口。呼び出し側が Running を書き戻す<b>前</b>に作って
    /// <see cref="RunningJobRegistry"/> へ登録済みのもの（理由は <see cref="RunOnceAsync"/> に）。
    /// 登録の解除も呼び出し側が持つので、結末を書き終えるまで受け口は生きている。
    /// </param>
    private async Task RunHandlerAsync(Job job, CancellationTokenSource cancellation)
    {
        // ハンドラには識別子と parameters だけを渡す（Job 全体は渡さない。状態を
        // 触られる形にしない）。ログへの JobId の付与はこのスコープが担い、
        // ハンドラや await の継続が将来書くログ行すべてに JobId と JobType が自動で付く。
        using IDisposable? scope = _logger.BeginScope("Job {JobId} ({JobType})", job.Id.Value, job.JobType);

        // 購読者がいなければ null が返り、以降の activity?. はすべて no-op になる。
        // つまり購読者ゼロのときは挙動が一切変わらない。スパンの開始（機構）は
        // instrumentation の仕事で、エンジンが持つのは結末に応じた終わり方の判断だけ。
        // トークンを渡さないのは FinishAsync と同じ理由で、プロセス停止は
        // 利用者のキャンセル要求ではないから。
        using Activity? activity =
            await _instrumentation.StartExecuteActivityAsync(job, CancellationToken.None);

        // Running を書き戻せた直後、つまりこの Job を実行すると確定した点の記録。
        _logger.LogInformation("Job {JobId} ({JobType}) の実行を開始します。", job.Id.Value, job.JobType);

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
                await handler.ExecuteAsync(job.Id, job.Parameters, cancellation.Token);

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

        // Error にするのはハンドラの失敗（ハンドラ不在を含む）だけ。キャンセルは利用者が
        // 意図した結末であって失敗ではないので、Error にすると誤検知の山になる。
        if (trigger == JobTrigger.Fail)
        {
            activity?.SetStatus(ActivityStatusCode.Error, failureMessage);
        }

        JobStatus? finished = await FinishAsync(job.Id, trigger, failureMessage);
        if (finished is { } status)
        {
            activity?.SetTag("job.status", status.ToString());
        }
    }

    /// <summary>
    /// 実行の結末を保存する。確定した終端の状態を返す。Job が見つからなければ null。
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
    /// 読み直してから書き戻すまでの間にも状態は動きうる（キャンセルや一時停止の要求が
    /// 届く、など）ので、書き戻せなければ読み直して評価をやり直す。相手が動かした先が
    /// Running / Cancelling / Pausing のどれであっても結末（Complete / Fail）は受理されるので、
    /// やり直した次の評価は必ず決着に向かう。終端に達していれば拒否されて抜ける。
    /// 無限にやり直すには書き戻すたびに他所が先に書き続ける必要があり、
    /// それは停止と再開の要求が無限に交互に届き続けるときだけで、実行の停滞ではない。
    /// </para>
    /// </remarks>
    private async Task<JobStatus?> FinishAsync(JobId id, JobTrigger trigger, string? failureMessage)
    {
        while (true)
        {
            Job? job = await _store.FindAsync(id, CancellationToken.None);
            if (job is null)
            {
                _logger.LogWarning("Job {JobId} は実行後に見つかりませんでした。結末を記録できません。", id.Value);
                return null;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();

            JobTransitionResult result = job.Apply(trigger, now, failureMessage);
            if (!result.IsAllowed)
            {
                // 状態機械の判断に従う。ただし終端に達していない Job を放置すると
                // 誰も動かしていないのに Running のまま残るので、失敗として閉じる。
                if (job.Status.IsTerminal())
                {
                    // 終端は他所（別プロセスの復旧など）が書いた。こちらは何も記録していないので
                    // メトリクスにも数えない。書いた側が自分の分を数える。
                    return job.Status;
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
                // 結末の確定＝終端の書き戻しに成功した点。所要時間と終端到達数はここで確定する。
                // 起動時復旧（StartAsync）が閉じる残骸はここを通らないので数えない。
                // 残骸の FinishedAt - StartedAt は前回プロセスの停止時間を含み、所要時間として
                // 意味を成さないため。
                _instrumentation.RecordFinished(job);

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

                return job.Status;
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
