using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.QueryJobs;

/// <summary>
/// Job のサブタスクの読み出し。
/// </summary>
/// <remarks>
/// 「Job が無い」（null）と「サブタスクがまだ無い」（空）を区別する。
/// 前者は 404、後者は登録直後の正常な姿（行は実行の開始時に作られる）。
/// </remarks>
public sealed class QuerySubTasksHandler
{
    private readonly IJobStore _jobs;
    private readonly ISubTaskStore _subTasks;

    public QuerySubTasksHandler(IJobStore jobs, ISubTaskStore subTasks)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(subTasks);

        _jobs = jobs;
        _subTasks = subTasks;
    }

    /// <summary>連番順のサブタスクを返す。Job が見つからなければ null。</summary>
    public async Task<IReadOnlyList<SubTaskDto>?> ListAsync(string id, CancellationToken cancellationToken)
    {
        if (!JobId.TryFrom(id, out JobId jobId)
            || await _jobs.FindAsync(jobId, cancellationToken) is null)
        {
            return null;
        }

        return [.. (await _subTasks.ListByJobAsync(jobId, cancellationToken)).Select(SubTaskDto.From)];
    }
}
