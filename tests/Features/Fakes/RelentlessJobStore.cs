using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 書き戻しのたびに、対象の Job を横から 1 手先へ進める <see cref="IJobStore"/>。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InterferingJobStore"/> は 1 度しか割り込まないので、「競合し続けたときに
/// 読み直しのループが止まるか」は確かめられない。こちらは毎回割り込む。
/// </para>
/// <para>
/// 進め方は状態機械に従う。任意の状態を書き込むのではなく、その時点で許される契機の中から
/// 終端へ近づくものを選ぶ。ありえない状態を作って壊すのでは、実装が悪いのか
/// 割り込みが不当なのか区別が付かない。状態機械が一方通行である以上、
/// この割り込みは高々数回で終端に届き、そこから先は何も起こさなくなる。
/// </para>
/// </remarks>
internal sealed class RelentlessJobStore : IJobStore
{
    private readonly IJobStore _inner;
    private readonly DateTimeOffset _at;

    public RelentlessJobStore(IJobStore inner, DateTimeOffset at)
    {
        _inner = inner;
        _at = at;
    }

    /// <summary>割り込みを効かせるか。テストが窓を開けたい時点で立てる。</summary>
    public bool Interfering { get; set; }

    /// <summary>書き戻しが試みられた回数。ループが止まったことを回数で裏付ける。</summary>
    public int UpdateAttempts { get; private set; }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(Job job, JobStatus expectedStatus, CancellationToken cancellationToken)
    {
        if (Interfering)
        {
            await AdvanceAsync(job.Id, cancellationToken);
        }

        UpdateAttempts++;
        return await _inner.UpdateAsync(job, expectedStatus, cancellationToken);
    }

    /// <summary>
    /// 保存されている Job を、許される契機で 1 手だけ先へ進める。
    /// </summary>
    private async Task AdvanceAsync(JobId id, CancellationToken cancellationToken)
    {
        Job? current = await _inner.FindAsync(id, cancellationToken);
        if (current is null || current.Status.IsTerminal())
        {
            return;
        }

        JobStatus expected = current.Status;

        // 終端へ落とす契機は後回しにする。すぐ終端に届けてしまうと読み直しが 1 回で終わり、
        // 「競合し続けても止まるか」を確かめたことにならない。
        JobTrigger? chosen = Enum.GetValues<JobTrigger>()
            .Select(trigger => (Trigger: trigger, Result: JobStateMachine.Evaluate(expected, trigger)))
            .Where(candidate => candidate.Result.IsAllowed)
            .OrderBy(candidate => candidate.Result.Status.IsTerminal() ? 1 : 0)
            .Select(candidate => (JobTrigger?)candidate.Trigger)
            .FirstOrDefault();

        if (chosen is not { } advance)
        {
            return;
        }

        current.Apply(advance, _at, $"割り込みの {advance}");
        await _inner.UpdateAsync(current, expected, cancellationToken);
    }

    /// <inheritdoc />
    public Task AddAsync(Job job, CancellationToken cancellationToken) =>
        _inner.AddAsync(job, cancellationToken);

    /// <inheritdoc />
    public Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken) =>
        _inner.FindAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken) =>
        _inner.ListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Job?> FindOldestQueuedAsync(CancellationToken cancellationToken) =>
        _inner.FindOldestQueuedAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken) =>
        _inner.ListByStatusAsync(status, cancellationToken);
}
