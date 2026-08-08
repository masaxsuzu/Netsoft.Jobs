using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Audit;
using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Features.CancelJob;

/// <summary>
/// Job のキャンセルを要求する。状態を進めてから、実行中のハンドラへ伝える。
/// </summary>
/// <remarks>
/// 呼び出すのは HTTP エンドポイントだけ。画面は別プロセス（src/Ui）にあり、API 越しに使う。
/// どの状態でキャンセルできるかは <see cref="JobStateMachine"/> が決めているので、
/// ここでは状態を見て分岐しない。見て分岐すると仕様が 2 か所に分かれる。
/// </remarks>
public sealed class CancelJobHandler
{
    private readonly IJobStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CancelJobHandler> _logger;

    public CancelJobHandler(
        IJobStore store,
        TimeProvider timeProvider,
        ILogger<CancelJobHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// キャンセルを要求する。拒否された場合は保存も伝達もしない。
    /// </summary>
    /// <remarks>
    /// 読み出しと保存の間に実行エンジンが結末を書き込むことがある。条件付き更新で書き戻せなければ
    /// 先頭から（読み直しから）やり直す。やり直しが決着するのは、どの状態でもキャンセルの
    /// 評価が答えを持つから ── 非終端なら受理される（行き先は必ず Cancelling）か既に効いていて
    /// （<see cref="JobTransitionRejection.AlreadyInEffect"/>）、終端なら
    /// <see cref="JobTransitionRejection.JobAlreadyFinished"/> として拒否されて抜ける。
    /// 読み直した先がどこであれ、次の評価で必ず止まるか書ける。
    /// <para>
    /// 期待値が版になったので、状態が動かない書き込み（編集）でもやり直しが起きる。
    /// このとき読み直した先は同じ状態なので、上の議論はそのまま通り、次の周回で書ける
    /// （やり直しの回数が編集の回数だけ増えるが、それは利用者の操作の回数で頭打ちになる）。
    /// </para>
    /// </remarks>
    public async Task<Audited<CancelJobResult>> HandleAsync(string id, CancellationToken cancellationToken)
    {
        DateTimeOffset at = _timeProvider.GetUtcNow();

        // 識別子の形にならない値は何も指し示していない。読み出しと同じく「無い」として扱う。
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

            JobTransitionResult transition = job.Apply(JobTrigger.RequestCancel, _timeProvider.GetUtcNow());
            if (!transition.IsAllowed)
            {
                // Job は拒否時に自身を変更しないので、保存しなければ store の内容も変わらない。
                // 理由は Domain が決めたものをそのまま渡す。ここで作り直さない。
                // 拒否には必ず理由が付くことは JobTransitionResult が型で保証している
                //（Rejected は非 null しか受け取らない）。ここで確かめ直さない。
                JobTransitionRejection rejection = transition.Rejection.Value;

                // 既に終わっていた・不正な状態への要求は、利用者の操作として普通に起きること。
                // 異常ではないので Warning にしない。Job 行には要求の痕跡が残らないため、
                // 「要求はあったが受け付けなかった」事実と時刻はこのログだけが持つ。
                _logger.LogInformation(
                    "Job {JobId} のキャンセル要求を受け付けませんでした。理由は {Rejection}、現在の状態は {Status} です。",
                    jobId.Value,
                    rejection,
                    job.Status);

                CancelJobResult rejected = CancelJobResult.Rejected(job.ToDto(), rejection);

                // AlreadyInEffect は成功として扱う（結果型の注記）。2 回押しただけの操作を
                // エラーとして残すと、本当の拒否が埋もれる。
                return new Audited<CancelJobResult>(
                    rejected,
                    new AuditLog(
                        AuditActor.User,
                        at,
                        Content,
                        jobId,
                        rejected.IsSuccess ? null : $"現在の状態（{job.Status}）ではキャンセルできません。"));
            }

            // 保存が伝達そのもの。ハンドラは Cancelling を読んで抜けるので、書けた時点で
            // 要求は届いている（書く前に届く順序が存在しない）。かつては保存と発火が
            // 2 手に分かれており、どちらを先にするかに正しさが乗っていた。
            if (!await _store.UpdateAsync(job, cancellationToken))
            {
                // 読み出しから保存までの間に他所が状態を進めた。前提が崩れただけなので、
                // 読み直して評価をやり直す。相手が終端まで進めていたなら、
                // 次の周回で状態機械が JobAlreadyFinished として拒否する。
                continue;
            }

            // 保存した時点で伝達は済んでいる。走っているハンドラは自分の行を読みに来るので、
            // ここから誰かを突く手順は無い（かつては実行中の Job の登録簿へトークンの発火を
            // 頼んでいた）。待機中で誰も走っていない場合は、下の確定がそのまま終端まで進める。

            // Job 行に Cancelling の時刻列は無いので、要求が受理された時刻はこのログだけが持つ。
            _logger.LogInformation(
                "Job {JobId} のキャンセル要求を受理しました。状態は {Status} になりました。",
                jobId.Value,
                job.Status);

            AuditLog requested = new(AuditActor.User, at, Content, jobId, Error: null);

            // ハンドラが居れば確定を書くのは実行エンジン。居なければここで閉じるので、
            // その確定はシステムの実施として 1 件足す（要求と別の出来事）。
            if (transition.Previous.IsHandlerActive())
            {
                return new Audited<CancelJobResult>(CancelJobResult.Accepted(job.ToDto()), requested);
            }

            Job settled = await SettleAsync(job, cancellationToken);

            return new Audited<CancelJobResult>(
                CancelJobResult.Accepted(settled.ToDto()),
                [
                    requested,
                    new AuditLog(
                        AuditActor.System,
                        _timeProvider.GetUtcNow(),
                        $"キャンセルを{(settled.Status == JobStatus.Cancelled ? "確定した" : "確定できなかった")}",
                        jobId,
                        settled.Status == JobStatus.Cancelled
                            ? null
                            : $"確定しようとしたときには状態が {settled.Status} になっていました。"),
                ]);
        }
    }

    /// <summary>実施内容。結末によらず同じ ── 記録するのは「何をしたか」で、結果は Error 側。</summary>
    private const string Content = "キャンセルを要求した";

    private static Audited<CancelJobResult> NotFound(DateTimeOffset at) =>
        new(
            CancelJobResult.NotFound(),
            new AuditLog(AuditActor.User, at, Content, JobId: null, "対象の Job が見つかりませんでした。"));

    /// <summary>
    /// 要求を確定させる。ハンドラが居ない相手にだけ呼ぶ。
    /// </summary>
    /// <remarks>
    /// 判断は <see cref="PauseJob.PauseJobHandler"/> の同名メソッドと同じで、理由もそちらに書いてある。
    /// 呼ぶ相手が違う（ここは Cancelling → Cancelled）だけなので、共通化せずに写してある。
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
