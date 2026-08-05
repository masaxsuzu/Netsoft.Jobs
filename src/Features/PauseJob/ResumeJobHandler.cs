using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.PauseJob;

/// <summary>
/// Job の再開を要求する。停止中（Paused）なら Queued へ戻して既存のディスパッチに乗せ、
/// 受理前（Pausing）なら Running へ揺り戻すだけで済ませる。
/// </summary>
/// <remarks>
/// どちらへ進むかは状態機械が決める。ここでは分岐しない。
/// Queued へ戻った Job を実行エンジンが拾えるのは、書き込みがホストの結線
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
    public async Task<JobControlResult> HandleAsync(string id, CancellationToken cancellationToken)
    {
        if (!JobId.TryFrom(id, out JobId jobId))
        {
            return JobControlResult.NotFound();
        }

        while (true)
        {
            Job? job = await _store.FindAsync(jobId, cancellationToken);
            if (job is null)
            {
                return JobControlResult.NotFound();
            }

            JobTransitionResult transition = job.Apply(JobTrigger.Resume, _timeProvider.GetUtcNow());
            if (!transition.IsAllowed)
            {
                JobTransitionRejection rejection = transition.Rejection
                    ?? throw new InvalidOperationException("拒否された遷移に理由がありません。");

                _logger.LogInformation(
                    "Job {JobId} の再開要求を受け付けませんでした。理由は {Rejection}、現在の状態は {Status} です。",
                    jobId.Value,
                    rejection,
                    job.Status);

                return JobControlResult.Rejected(JobDto.From(job), rejection);
            }

            if (!await _store.UpdateAsync(job, transition.Previous, cancellationToken))
            {
                continue;
            }

            // Queued（停止中からの再開）か Running（受理前の取り消し）かは遷移後の状態で読み取れる。
            _logger.LogInformation(
                "Job {JobId} の再開要求を受理しました。状態は {Status} になりました。",
                jobId.Value,
                job.Status);

            return JobControlResult.Accepted(JobDto.From(job));
        }
    }
}
