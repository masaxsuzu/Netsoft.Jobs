using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.CancelJob;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// キャンセル要求の読み直しループを、競合し続ける状況で調べる。
/// </summary>
/// <remarks>
/// <see cref="CancelJobHandler"/> にも実行エンジンと同じ「状態機械が一方通行だから
/// やり直しは有限で止まる」という注記がある。毎回横から状態を進める store を当てて確かめる。
/// </remarks>
public sealed class CancelJobHandlerConcurrencyTests : IDisposable
{
    /// <summary>止まらないループを試験の停止に変えるための保険。実装が正しければ発火しない。</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset Now = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task 書き戻しが競合し続けてもやり直しは有限で止まる()
    {
        await _store.AddAsync(
            Job.Create(JobId.From("job-1"), "競合", "test-job", string.Empty, Now),
            CancellationToken.None);

        RelentlessJobStore hostile = new(_store, Now) { Interfering = true };
        CancelJobHandler handler = new(
            hostile,
            new RunningJobRegistry(),
            new FixedTimeProvider(Now),
            NullLogger<CancelJobHandler>.Instance);

        CancelJobResult result = await handler.HandleAsync("job-1", CancellationToken.None).WaitAsync(HangGuard);

        // 追い抜かれ続けた末に、状態機械が「もう効いている」か「もう終わっている」で決着させる。
        Assert.NotNull(result.Rejection);

        Job job = await FindAsync("job-1");
        Assert.Null(JobInvariants.FindViolation(job));

        // Queued → Running → Cancelling と進む間に高々 3 回。上限を置いて青天井でないことを示す。
        Assert.InRange(hostile.UpdateAttempts, 1, 5);
    }

    /// <summary>
    /// 実行エンジンが結末を書いた直後にキャンセルが来ても、終端を上書きしないこと。
    /// </summary>
    /// <remarks>
    /// 条件付き更新を入れた元の動機がこれ。割り込みで決定的に起こす。
    /// </remarks>
    [Fact]
    public async Task 終端が書かれた後のキャンセルは上書きせずに拒否される()
    {
        Job job = Job.Create(JobId.From("job-1"), "競合", "test-job", string.Empty, Now);
        await _store.AddAsync(job, CancellationToken.None);

        Assert.True(job.Apply(JobTrigger.Start, Now).IsAllowed);
        Assert.True(await _store.UpdateAsync(job, JobStatus.Queued, CancellationToken.None));

        InterferingJobStore interfering = new(_store);
        CancelJobHandler handler = new(
            interfering,
            new RunningJobRegistry(),
            new FixedTimeProvider(Now),
            NullLogger<CancelJobHandler>.Instance);

        // 読み出しの後、書き戻しの直前にエンジンが完了を書き込む。
        interfering.BeforeNextUpdate = async () =>
        {
            Job finishing = await FindAsync("job-1");
            Assert.True(finishing.Apply(JobTrigger.Complete, Now).IsAllowed);
            Assert.True(await _store.UpdateAsync(finishing, JobStatus.Running, CancellationToken.None));
        };

        CancelJobResult result = await handler.HandleAsync("job-1", CancellationToken.None).WaitAsync(HangGuard);

        Assert.False(result.IsSuccess);
        Assert.Equal(JobTransitionRejection.JobAlreadyFinished, result.Rejection);
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-1")).Status);
    }

    private async Task<Job> FindAsync(string id) =>
        await _store.FindAsync(JobId.From(id), CancellationToken.None)
        ?? throw new InvalidOperationException($"Job {id} が保存されていません。");
}
