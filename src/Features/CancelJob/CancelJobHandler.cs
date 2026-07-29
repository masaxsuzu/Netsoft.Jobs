using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Features.CancelJob;

/// <summary>
/// Job のキャンセルを要求する。状態を進めてから、実行中のハンドラへ伝える。
/// </summary>
/// <remarks>
/// HTTP エンドポイントと画面（Blazor）の両方がこのクラスを直接呼ぶ。
/// どの状態でキャンセルできるかは <see cref="JobStateMachine"/> が決めているので、
/// ここでは状態を見て分岐しない。見て分岐すると仕様が 2 か所に分かれる。
/// </remarks>
public sealed class CancelJobHandler
{
    private readonly IJobStore _store;
    private readonly IRunningJobRegistry _runningJobs;
    private readonly TimeProvider _timeProvider;

    public CancelJobHandler(IJobStore store, IRunningJobRegistry runningJobs, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runningJobs);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _store = store;
        _runningJobs = runningJobs;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// キャンセルを要求する。拒否された場合は保存も伝達もしない。
    /// </summary>
    /// <remarks>
    /// 読み出しと保存の間に実行エンジンが結末を書き込むことがある。条件付き更新で書き戻せなければ
    /// 先頭から（読み直しから）やり直す。状態機械は終端へ向かう一方通行なので、
    /// 書き戻せなかったということは相手が状態を先へ進めたということ。
    /// 終端に達していればやり直した先で <see cref="JobTransitionRejection.JobAlreadyFinished"/> として
    /// 拒否されるので、やり直しは必ず有限で止まる。
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

            // Apply は Job を破壊的に変えるので、読み出したときの状態をここで控える。
            JobStatus expected = job.Status;

            JobTransitionResult transition = job.Apply(JobTrigger.RequestCancel, _timeProvider.GetUtcNow());
            if (!transition.IsAllowed)
            {
                // Job は拒否時に自身を変更しないので、保存しなければ store の内容も変わらない。
                // 理由は Domain が決めたものをそのまま渡す。ここで作り直さない。
                // 拒否には必ず理由が付く。無いなら状態機械側の不整合なので、既定値で埋めずに落とす。
                JobTransitionRejection rejection = transition.Rejection
                    ?? throw new InvalidOperationException("拒否された遷移に理由がありません。");

                return CancelJobResult.Rejected(JobDto.From(job), rejection);
            }

            // 保存が先。実行中のハンドラのトークンを先に発火させると、こちらが Cancelling を
            // 書き終える前にハンドラが終端（Cancelled や Completed）を書き込みうる。
            // そうなると後から書くこの更新が終端を Cancelling で上書きして、
            // 終わっているのに終わっていない Job ができる。
            // 条件付き更新にしたことで、その追い越しが起きても上書きは成立しなくなった。
            // 順序自体は保つ。伝達を先にすると、上書きこそ防げても
            // 「まだ Cancelling を書けていない Job にキャンセルが届く」ことになる。
            if (!await _store.UpdateAsync(job, expected, cancellationToken))
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

            return CancelJobResult.Accepted(JobDto.From(job));
        }
    }
}
