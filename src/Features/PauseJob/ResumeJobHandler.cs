using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Audit;

namespace Netsoft.Jobs.Features.PauseJob;

/// <summary>
/// Job の再開を要求する。停止中（Paused）なら Resuming を経て待ち行列へ戻し、
/// 受理前（Pausing）なら InProgress へ揺り戻すだけで済ませる。
/// </summary>
/// <remarks>
/// どちらへ進むかは状態機械が決める。ここでは分岐しない。
/// 待ち行列へ戻った Job を実行エンジンが拾えるのは、書き込みがホストの結線
/// （NotifyingJobStore → JobChangeFeed → JobQueueSignal）を通って合図になるから。
/// このハンドラが合図を鳴らすのではない。
/// </remarks>
public sealed class ResumeJobHandler
{
    private readonly IJobStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ResumeJobHandler> _logger;

    public ResumeJobHandler(IJobStore store, TimeProvider timeProvider, ILogger<ResumeJobHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>再開を要求する。拒否された場合は保存しない。</summary>
    /// <remarks>
    /// <b>停止中からの再開は監査ログが 2 件になる。</b>利用者が押したのは 1 回だが、
    /// このコマンドは要求（Resuming）を書いたあと確定（Resumed）まで自分で済ませる。
    /// 要求と確定は別の出来事なので分けて記録する ── 畳むと「誰が押したか」と
    /// 「いつ待ち行列へ戻ったか」のどちらも読めなくなる。
    /// 受理前の揺り戻し（Pausing → InProgress）は確定を伴わないので 1 件。
    /// </remarks>
    public async Task<Audited<JobControlResult>> HandleAsync(string id, CancellationToken cancellationToken)
    {
        DateTimeOffset at = _timeProvider.GetUtcNow();

        if (!JobId.TryFrom(id, out JobId jobId))
        {
            return NotFound(at);
        }

        while (true)
        {
            Job? job = await _store.FindAsync(jobId, cancellationToken);
            if (job is null)
            {
                return NotFound(at);
            }

            JobTransitionResult transition = job.Apply(JobTrigger.Resume, _timeProvider.GetUtcNow());
            if (!transition.IsAllowed)
            {
                JobTransitionRejection rejection = transition.Rejection.Value;

                _logger.LogInformation(
                    "Job {JobId} の再開要求を受け付けませんでした。理由は {Rejection}、現在の状態は {Status} です。",
                    jobId.Value,
                    rejection,
                    job.Status);

                JobControlResult rejected = JobControlResult.Rejected(job.ToDto(), rejection);

                return new Audited<JobControlResult>(
                    rejected,
                    new AuditLog(
                        AuditActor.User,
                        at,
                        Content,
                        jobId,
                        rejected.IsSuccess ? null : $"現在の状態（{job.Status}）では再開できません。"));
            }

            if (!await _store.UpdateAsync(job, cancellationToken))
            {
                continue;
            }

            // Resuming（停止中からの再開）か InProgress（受理前の取り消し）かは遷移後の状態で読み取れる。
            _logger.LogInformation(
                "Job {JobId} の再開要求を受理しました。状態は {Status} になりました。",
                jobId.Value,
                job.Status);

            AuditLog requested = new(AuditActor.User, at, Content, jobId, Error: null);

            // 受理前の揺り戻し（Pausing → InProgress）は確定を伴わない。走っているハンドラの
            // ところへ戻すだけなので、記録するのは要求 1 件。
            if (transition.Previous.IsHandlerActive())
            {
                return new Audited<JobControlResult>(JobControlResult.Accepted(job.ToDto()), requested);
            }

            Job settled = await SettleAsync(job, cancellationToken);

            return new Audited<JobControlResult>(
                JobControlResult.Accepted(settled.ToDto()),
                [
                    requested,
                    new AuditLog(
                        AuditActor.System,
                        _timeProvider.GetUtcNow(),
                        $"再開を{(settled.Status == JobStatus.Resumed ? "確定し、待ち行列へ戻した" : "確定できなかった")}",
                        jobId,
                        settled.Status == JobStatus.Resumed
                            ? null
                            : $"確定しようとしたときには状態が {settled.Status} になっていました。"),
                ]);
        }
    }

    /// <summary>実施内容。結末によらず同じ ── 記録するのは「何をしたか」で、結果は Error 側。</summary>
    private const string Content = "再開を要求した";

    private static Audited<JobControlResult> NotFound(DateTimeOffset at) =>
        new(
            JobControlResult.NotFound(),
            new AuditLog(AuditActor.User, at, Content, JobId: null, "対象の Job が見つかりませんでした。"));

    /// <summary>
    /// 要求を確定させる。ハンドラが居ない相手にだけ呼ぶ。
    /// </summary>
    /// <remarks>
    /// 判断は <see cref="PauseJobHandler"/> の同名メソッドと同じで、理由もそちらに書いてある。
    /// 呼ぶ相手が違う（ここは Resuming → Resumed）だけなので、共通化せずに写してある。
    /// </remarks>
    private async Task<Job> SettleAsync(Job requested, CancellationToken cancellationToken)
    {
        while (true)
        {
            Job? job = await _store.FindAsync(requested.Id, cancellationToken);
            if (job is null)
            {
                return requested;
            }

            if (job.Status.SettlementTrigger() is not { } confirm
                || !job.Apply(confirm, _timeProvider.GetUtcNow()).IsAllowed)
            {
                return job;
            }

            if (await _store.UpdateAsync(job, cancellationToken))
            {
                return job;
            }
        }
    }
}
