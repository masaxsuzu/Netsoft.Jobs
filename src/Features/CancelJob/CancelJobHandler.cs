using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;
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
    private readonly IRunningJobRegistry _runningJobs;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CancelJobHandler> _logger;

    public CancelJobHandler(
        IJobStore store,
        IRunningJobRegistry runningJobs,
        TimeProvider timeProvider,
        ILogger<CancelJobHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runningJobs);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _runningJobs = runningJobs;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// キャンセルを要求する。拒否された場合は保存も伝達もしない。
    /// </summary>
    /// <remarks>
    /// 読み出しと保存の間に実行エンジンが結末を書き込むことがある。条件付き更新で書き戻せなければ
    /// 先頭から（読み直しから）やり直す。やり直しが決着するのは、どの状態でもキャンセルの
    /// 評価が答えを持つから ── 非終端なら受理される（Queued / Paused は即終端へ、
    /// Running / Pausing は Cancelling へ）か既に効いていて
    /// （<see cref="JobTransitionRejection.AlreadyInEffect"/>）、終端なら
    /// <see cref="JobTransitionRejection.JobAlreadyFinished"/> として拒否されて抜ける。
    /// 読み直した先がどこであれ、次の評価で必ず止まるか書ける。
    /// <para>
    /// 期待値が版になったので、状態が動かない書き込み（編集）でもやり直しが起きる。
    /// このとき読み直した先は同じ状態なので、上の議論はそのまま通り、次の周回で書ける
    /// （やり直しの回数が編集の回数だけ増えるが、それは利用者の操作の回数で頭打ちになる）。
    /// </para>
    /// </remarks>
    public async Task<CancelJobResult> HandleAsync(string id, CancellationToken cancellationToken)
    {
        // 識別子の形にならない値は何も指し示していない。読み出しと同じく「無い」として扱う。
        if (!JobId.TryFrom(id, out JobId jobId))
        {
            return CancelJobResult.NotFound();
        }

        while (true)
        {
            Job? job = await _store.FindAsync(jobId, cancellationToken);
            if (job is null)
            {
                return CancelJobResult.NotFound();
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

                return CancelJobResult.Rejected(JobDto.From(job), rejection);
            }

            // 保存が先。実行中のハンドラのトークンを先に発火させると、こちらが Cancelling を
            // 書き終える前にハンドラが終端（Cancelled や Completed）を書き込みうる。
            // そうなると後から書くこの更新が終端を Cancelling で上書きして、
            // 終わっているのに終わっていない Job ができる。
            // 条件付き更新にしたことで、その追い越しが起きても上書きは成立しなくなった。
            // 順序自体は保つ。伝達を先にすると、上書きこそ防げても
            // 「まだ Cancelling を書けていない Job にキャンセルが届く」ことになる。
            if (!await _store.UpdateAsync(job, cancellationToken))
            {
                // 読み出しから保存までの間に他所が状態を進めた。前提が崩れただけなので、
                // 読み直して評価をやり直す。相手が終端まで進めていたなら、
                // 次の周回で状態機械が JobAlreadyFinished として拒否する。
                continue;
            }

            // 保存が済んでから伝える。戻り値は見ない。false は「このプロセスで実行中ではない」
            // （待機中だった、既に終わった、別プロセス）というだけで失敗ではないので、
            // これを理由に状態遷移を巻き戻さない。待機中の Job は必ず false になるが、
            // その場合は状態機械が既に Cancelled まで進めていて、止める相手がいない。
            _runningJobs.TryRequestCancel(job.Id);

            // Job 行に Cancelling の時刻列は無いので、要求が受理された時刻はこのログだけが持つ。
            // Cancelled へ直行した（待機中だった）か、Cancelling でハンドラの受理待ちかは
            // 遷移後の状態で読み取れる。
            _logger.LogInformation(
                "Job {JobId} のキャンセル要求を受理しました。状態は {Status} になりました。",
                jobId.Value,
                job.Status);

            return CancelJobResult.Accepted(JobDto.From(job));
        }
    }
}
