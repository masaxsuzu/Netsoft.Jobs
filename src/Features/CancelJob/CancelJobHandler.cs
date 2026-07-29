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
    public async Task<CancelJobResult> HandleAsync(string id, CancellationToken cancellationToken)
    {
        // 識別子の形にならない値は何も指し示していない。読み出しと同じく「無い」として扱う。
        if (!JobId.TryFrom(id, out JobId jobId))
        {
            return CancelJobResult.NotFound();
        }

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
            // 拒否には必ず理由が付く。無いなら状態機械側の不整合なので、既定値で埋めずに落とす。
            JobTransitionRejection rejection = transition.Rejection
                ?? throw new InvalidOperationException("拒否された遷移に理由がありません。");

            return CancelJobResult.Rejected(JobDto.From(job), rejection);
        }

        // 保存が先。実行中のハンドラのトークンを先に発火させると、こちらが Cancelling を
        // 書き終える前にハンドラが終端（Cancelled や Completed）を書き込みうる。
        // そうなると後から書くこの更新が終端を Cancelling で上書きして、
        // 終わっているのに終わっていない Job ができる。
        await _store.UpdateAsync(job, cancellationToken);

        // 保存が済んでから伝える。戻り値は見ない。false は「このプロセスで実行中ではない」
        // （待機中だった、既に終わった、別プロセス）というだけで失敗ではないので、
        // これを理由に状態遷移を巻き戻さない。待機中の Job は必ず false になるが、
        // その場合は状態機械が既に Cancelled まで進めていて、止める相手がいない。
        _runningJobs.TryRequestCancel(job.Id);

        return CancelJobResult.Accepted(JobDto.From(job));
    }
}
