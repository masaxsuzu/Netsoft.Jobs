using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Features.EditJob;

/// <summary>
/// subtasks Job のパラメータ（個数 N と秒数 m）を編集する。実行中でも停止中でもよい。
/// </summary>
/// <remarks>
/// <para>
/// 書くのは Job 行の parameters だけ。サブタスクの行はここでは触らない。
/// 行の突き合わせ（増やす・未着手を消す・m の採り直し）は、実行しているハンドラが
/// サブタスクの境界で行う（<see cref="SubTaskJobHandler"/> の注記を参照）。
/// 編集と実行が別の書き手として同じ行を触ると、条件付き更新の期待が二重になる。
/// </para>
/// <para>
/// 「N ≥ 着手済み」はここでも検証するが、これは利用者への親切（早い 400）。
/// 検証と反映の間に次のサブタスクが走り出す窓があるので、本当の守りは
/// <see cref="ISubTaskStore.RemovePendingFromAsync"/> の SQL が持つ（未着手の行しか消さない）。
/// CAS の「検証は親切、確定は書く場所で」と同じ構図。
/// </para>
/// </remarks>
public sealed class EditJobHandler
{
    private readonly IJobStore _store;
    private readonly ISubTaskStore _subTasks;
    private readonly ILogger<EditJobHandler> _logger;

    public EditJobHandler(IJobStore store, ISubTaskStore subTasks, ILogger<EditJobHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(subTasks);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _subTasks = subTasks;
        _logger = logger;
    }

    /// <summary>パラメータの編集を要求する。拒否・不正の場合は保存しない。</summary>
    public async Task<EditJobResult> HandleAsync(string id, string parameters, CancellationToken cancellationToken)
    {
        if (!JobId.TryFrom(id, out JobId jobId))
        {
            return EditJobResult.NotFound();
        }

        if (!SubTaskParameters.TryParse(parameters, out int count, out _))
        {
            return EditJobResult.Invalid([new ValidationError("parameters", SubTaskParameters.Expectation)]);
        }

        while (true)
        {
            Job? job = await _store.FindAsync(jobId, cancellationToken);
            if (job is null)
            {
                return EditJobResult.NotFound();
            }

            if (!job.Status.CanEditParameters())
            {
                // 終端は「もう終わっている」、Cancelling は「今の状態では無理」。
                // 画面が 409 の意味を出し分けられるよう、キャンセルと同じ理由の型で返す。
                JobTransitionRejection rejection = job.Status.IsTerminal()
                    ? JobTransitionRejection.JobAlreadyFinished
                    : JobTransitionRejection.InvalidForCurrentStatus;

                _logger.LogInformation(
                    "Job {JobId} の編集を受け付けませんでした。理由は {Rejection}、現在の状態は {Status} です。",
                    jobId.Value,
                    rejection,
                    job.Status);

                return EditJobResult.Rejected(JobDto.From(job), rejection);
            }

            // 着手済み（Pending でない行）は編集で消せない。定義の変更であって履歴の書き換えではない。
            int started = (await _subTasks.ListByJobAsync(jobId, cancellationToken))
                .Count(subTask => subTask.Status != SubTaskStatus.Pending);
            if (count < started)
            {
                return EditJobResult.Invalid(
                    [new ValidationError(
                        "parameters",
                        $"サブタスクの個数は着手済みの {started} 個より小さくできません。")]);
            }

            job.ChangeParameters(parameters);

            // 期待は読み出したときの状態。編集は状態を変えないが、読み出しから書き込みまでの
            // 間に状態が動いていたら（終端に達した、など）書かずに読み直して評価をやり直す。
            if (!await _store.UpdateAsync(job, cancellationToken))
            {
                continue;
            }

            // 反映はサブタスクの境界なので、この時点で行はまだ動いていない。
            _logger.LogInformation(
                "Job {JobId} のパラメータを \"{Parameters}\" に編集しました。反映は次のサブタスクの境界です。",
                jobId.Value,
                parameters);

            return EditJobResult.Accepted(JobDto.From(job));
        }
    }
}
