using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Audit;

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
    /// <remarks>
    /// <para>
    /// 監査ログは結末によらず 1 件。<b>拒否も「利用者が押した」という実施</b>なので、
    /// 記録しないと画面のボタンを押した事実が後から追えない。
    /// </para>
    /// <para>
    /// 確定（Pausing → Paused）をここで書かない。要求できるのは実行中の Job だけで、
    /// そこには必ずハンドラが居るので、受理するのはサブタスクの境界まで来たハンドラの側。
    /// かつては待ち行列からも要求でき、その相手にはここで確定を書いていたが、
    /// 要求の入口が実行中だけになって呼ばれなくなったので落としてある。
    /// </para>
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

                JobControlResult rejected = JobControlResult.Rejected(job.ToDto(), rejection);

                // AlreadyInEffect は成功として扱う（結果型の注記）。監査でも同じ扱いにする ──
                // 2 回押しただけの操作をエラーとして残すと、本当の拒否が埋もれる。
                return new Audited<JobControlResult>(
                    rejected,
                    new AuditLog(
                        AuditActor.User,
                        at,
                        Content,
                        jobId,
                        rejected.IsSuccess ? null : $"現在の状態（{job.Status}）では一時停止できません。"));
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

            return new Audited<JobControlResult>(
                JobControlResult.Accepted(job.ToDto()),
                new AuditLog(AuditActor.User, at, Content, jobId, Error: null));
        }
    }

    /// <summary>実施内容。結末によらず同じ ── 記録するのは「何をしたか」で、結果は Error 側。</summary>
    private const string Content = "一時停止を要求した";

    private static Audited<JobControlResult> NotFound(DateTimeOffset at) =>
        new(
            JobControlResult.NotFound(),
            // Job に紐づけない。存在しない Job の監査ログは誰にも読まれない
            //（画面が出すのは実在する行の分だけ）。
            new AuditLog(AuditActor.User, at, Content, JobId: null, "対象の Job が見つかりませんでした。"));
}
