using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.PauseJob;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.PauseJob;

/// <summary>
/// 一時停止・再開の要求 1 回の結末を、状態ごとに固定する。
/// </summary>
/// <remarks>
/// 遷移の可否そのものは Domain の全組み合わせ走査が持つ。ここで見るのは
/// 「要求の結果への写り方」（受理 / 冪等な成功 / 拒否 / 対象なし）。
/// HTTP への写しは #32 の API が持つ予定。
/// </remarks>
public sealed class PauseResumeHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Requested = new(2026, 8, 5, 9, 3, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly PauseJobHandler _pause;
    private readonly ResumeJobHandler _resume;

    public PauseResumeHandlerTests()
    {
        FixedTimeProvider time = new(Requested);
        _pause = new PauseJobHandler(_store, time, NullLogger<PauseJobHandler>.Instance);
        _resume = new ResumeJobHandler(_store, time, NullLogger<ResumeJobHandler>.Instance);
    }

    public void Dispose() => _store.Dispose();

    /// <summary>
    /// 一時停止要求の状態ごとの結末。要求済み・停止済みへの再要求は
    /// ボタンを 2 回押しただけなので成功として写る（キャンセルと同じ判断）。
    /// </summary>
    [Theory]
    [InlineData(nameof(JobStatus.Running), true, nameof(JobStatus.Pausing))]
    [InlineData(nameof(JobStatus.Pausing), true, nameof(JobStatus.Pausing))]
    [InlineData(nameof(JobStatus.Paused), true, nameof(JobStatus.Paused))]
    [InlineData(nameof(JobStatus.Queued), false, nameof(JobStatus.Queued))]
    [InlineData(nameof(JobStatus.Cancelling), false, nameof(JobStatus.Cancelling))]
    [InlineData(nameof(JobStatus.Completed), false, nameof(JobStatus.Completed))]
    public async Task 一時停止要求は実行中だけを受理し要求済みには冪等に成功する(
        string current, bool success, string expected)
    {
        await AddAsync(JobAt("job-1", current));

        JobControlResult result = await _pause.HandleAsync("job-1", CancellationToken.None);

        Assert.Equal(success, result.IsSuccess);
        Assert.Equal(expected, result.Job?.Status);
        Assert.Equal(expected, (await SavedAsync("job-1")).Status.ToString());
    }

    /// <summary>
    /// 再開要求の状態ごとの結末。停止中は Queued へ、受理前は Running へ揺り戻す。
    /// 既に動いている（これから動く）ものへの要求は冪等に成功する。
    /// </summary>
    [Theory]
    [InlineData(nameof(JobStatus.Paused), true, nameof(JobStatus.Queued))]
    [InlineData(nameof(JobStatus.Pausing), true, nameof(JobStatus.Running))]
    [InlineData(nameof(JobStatus.Queued), true, nameof(JobStatus.Queued))]
    [InlineData(nameof(JobStatus.Running), true, nameof(JobStatus.Running))]
    [InlineData(nameof(JobStatus.Cancelling), false, nameof(JobStatus.Cancelling))]
    [InlineData(nameof(JobStatus.Cancelled), false, nameof(JobStatus.Cancelled))]
    public async Task 再開要求は停止側の状態だけを動かし動いているものには冪等に成功する(
        string current, bool success, string expected)
    {
        await AddAsync(JobAt("job-1", current));

        JobControlResult result = await _resume.HandleAsync("job-1", CancellationToken.None);

        Assert.Equal(success, result.IsSuccess);
        Assert.Equal(expected, result.Job?.Status);
        Assert.Equal(expected, (await SavedAsync("job-1")).Status.ToString());
    }

    [Fact]
    public async Task 停止中からの再開はStartedAtを消して次の開始で入り直す()
    {
        await AddAsync(JobAt("job-1", nameof(JobStatus.Paused)));

        JobControlResult result = await _resume.HandleAsync("job-1", CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Queued へ戻った Job は「まだ開始していない」。前回の実行の開始時刻は残さない。
        Job saved = await SavedAsync("job-1");
        Assert.Equal(JobStatus.Queued, saved.Status);
        Assert.Null(saved.StartedAt);
    }

    [Theory]
    [InlineData("job-9")]
    [InlineData("")]
    [InlineData(null)]
    public async Task 対象が無ければどちらの要求も対象なしとして返る(string? id)
    {
        await AddAsync(JobAt("job-1", nameof(JobStatus.Running)));

        JobControlResult paused = await _pause.HandleAsync(id!, CancellationToken.None);
        JobControlResult resumed = await _resume.HandleAsync(id!, CancellationToken.None);

        Assert.False(paused.IsSuccess);
        Assert.Null(paused.Job);
        Assert.False(resumed.IsSuccess);
        Assert.Null(resumed.Job);
    }

    private Task AddAsync(Job job) => _store.AddAsync(job, CancellationToken.None);

    private async Task<Job> SavedAsync(string id) =>
        await _store.FindAsync(JobId.From(id), CancellationToken.None)
        ?? throw new InvalidOperationException($"Job {id} が保存されていません。");

    private static Job JobAt(string id, string status)
    {
        JobStatus target = Enum.Parse<JobStatus>(status);

        return Job.Rehydrate(
            JobId.From(id),
            $"集計 {id}",
            "subtasks",
            "2 1",
            target,
            Created,
            target == JobStatus.Queued ? null : Created.AddMinutes(1),
            target.IsTerminal() ? Created.AddMinutes(2) : null,
            null);
    }
}
