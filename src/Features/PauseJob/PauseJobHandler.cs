using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.PauseJob;

/// <summary>
/// Job の一時停止を要求する。状態を Pausing へ進めるだけで、受理はハンドラが
/// サブタスクの境界で行う（<see cref="Execution.JobPausedException"/>）。
/// </summary>
/// <remarks>
/// キャンセルと違い、実行中のハンドラへ即座に伝える口（トークン）は無い。
/// 停止の効き目は境界と決まっているので、ハンドラが境界で状態を読み直せば足りる。
/// 伝達が要らないぶん、このハンドラは状態遷移の CAS ループだけでできている。
/// やり直しが決着する理由は <see cref="CancelJob.CancelJobHandler.HandleAsync"/> と同じ
/// （どの状態でも評価が答えを持つ）。
/// </remarks>
public sealed class PauseJobHandler
{
    private readonly IJobStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PauseJobHandler> _logger;

    public PauseJobHandler(IJobStore store, TimeProvider timeProvider, ILogger<PauseJobHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>一時停止を要求する。拒否された場合は保存しない。</summary>
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

            JobTransitionResult transition = job.Apply(JobTrigger.RequestPause, _timeProvider.GetUtcNow());
            if (!transition.IsAllowed)
            {
                JobTransitionRejection rejection = transition.Rejection.Value;

                // 要求の痕跡は Job 行に残らない。事実と時刻はこのログだけが持つ（キャンセルと同じ）。
                _logger.LogInformation(
                    "Job {JobId} の一時停止要求を受け付けませんでした。理由は {Rejection}、現在の状態は {Status} です。",
                    jobId.Value,
                    rejection,
                    job.Status);

                return JobControlResult.Rejected(JobDto.From(job), rejection);
            }

            if (!await _store.UpdateAsync(job, cancellationToken))
            {
                // 読み出しから保存までの間に他所が状態を進めた。読み直して評価をやり直す。
                continue;
            }

            _logger.LogInformation(
                "Job {JobId} の一時停止要求を受理しました。状態は {Status} になりました。",
                jobId.Value,
                job.Status);

            return JobControlResult.Accepted(JobDto.From(job));
        }
    }
}
