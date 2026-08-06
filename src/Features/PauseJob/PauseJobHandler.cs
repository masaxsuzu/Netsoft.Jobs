using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.PauseJob;

/// <summary>
/// Job の一時停止を要求する。状態を Pausing へ進め、待つ相手が居なければ受理まで書く。
/// </summary>
/// <remarks>
/// 実行中の Job の受理はハンドラがサブタスクの境界で行う
/// （<see cref="Execution.JobPausedException"/>）。キャンセルと違い、実行中のハンドラへ
/// 即座に伝える口（トークン）は無い ── 停止の効き目は境界と決まっているので、
/// ハンドラが境界で状態を読み直せば足りる。
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

            Job settled = transition.Previous.IsHandlerActive()
                ? job
                : await SettleAsync(job, cancellationToken);

            return JobControlResult.Accepted(JobDto.From(settled));
        }
    }

    /// <summary>
    /// 要求を確定させる。ハンドラが居ない相手にだけ呼ぶ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 要求は必ず ing を経由するので（<see cref="JobStateMachine"/>）、待つ相手が居ない相手にも
    /// 一度は Pausing が書かれる。<b>その 1 回の書き込みを削ってはいけない</b> ── 押した事実が
    /// 状態に残らなくなり、変更通知にも流れないので、画面は「押したのに何も起きていない」
    /// 見え方になる。確定まで 1 回の書き込みで済ませる誘惑はここにある。
    /// </para>
    /// <para>
    /// 落とす先は「今の状態」から引く。要求を書いてから読み直すまでの間に別の要求
    /// （中止など）が重なると、確定すべき ing が入れ替わっているため。書けなかった場合も
    /// 読み直して同じ判断をやり直す。ing でなくなっていれば誰かが決着させたということなので、
    /// そのまま返す。
    /// </para>
    /// <para>
    /// 2 回の書き込みの間にプロセスが落ちると ing が静止したまま残るが、
    /// 起動時復旧が ing を対応する ed へ落とすので、次の起動で必ず閉じる。
    /// </para>
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
