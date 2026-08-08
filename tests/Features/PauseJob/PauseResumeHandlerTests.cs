using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.PauseJob;
using Netsoft.Jobs.Features.Tests.CancelJob;
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
    /// 待機中（Registered / Resumed / Resuming）は走っていないので止める対象が無く、拒否する。
    /// </summary>
    [Theory]
    [InlineData(nameof(JobStatus.InProgress), true, nameof(JobStatus.Pausing))]
    [InlineData(nameof(JobStatus.Pausing), true, nameof(JobStatus.Pausing))]
    [InlineData(nameof(JobStatus.Paused), true, nameof(JobStatus.Paused))]
    [InlineData(nameof(JobStatus.Registered), false, nameof(JobStatus.Registered))]
    [InlineData(nameof(JobStatus.Resumed), false, nameof(JobStatus.Resumed))]
    [InlineData(nameof(JobStatus.Resuming), false, nameof(JobStatus.Resuming))]
    [InlineData(nameof(JobStatus.Cancelling), false, nameof(JobStatus.Cancelling))]
    [InlineData(nameof(JobStatus.Completed), false, nameof(JobStatus.Completed))]
    public async Task 一時停止要求は実行中だけを受理し待機中は拒否する(
        string current, bool success, string expected)
    {
        await AddAsync(JobAt("job-1", current));

        JobControlResult result = await _pause.HandleAsync("job-1", CancellationToken.None);

        Assert.Equal(success, result.IsSuccess);
        Assert.Equal(expected, result.Job?.Status);
        Assert.Equal(expected, (await SavedAsync("job-1")).Status.ToString());
    }

    /// <summary>
    /// 再開要求の状態ごとの結末。停止中は Resumed へ、受理前は InProgress へ揺り戻す。
    /// 既に動いている（これから動く）ものへの要求は冪等に成功する。
    /// </summary>
    [Theory]
    [InlineData(nameof(JobStatus.Paused), true, nameof(JobStatus.Resumed))]
    [InlineData(nameof(JobStatus.Pausing), true, nameof(JobStatus.InProgress))]
    [InlineData(nameof(JobStatus.Registered), true, nameof(JobStatus.Registered))]
    [InlineData(nameof(JobStatus.Resumed), true, nameof(JobStatus.Resumed))]
    [InlineData(nameof(JobStatus.InProgress), true, nameof(JobStatus.InProgress))]
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

    /// <summary>
    /// ハンドラが居ない相手への要求も、一度は確定待ちとして保存される。
    /// </summary>
    /// <remarks>
    /// 確定まで 1 回の書き込みで済ませると、押した事実が変更通知に流れない。画面から見ると
    /// 「押したのに何も起きていない」と区別が付かず、利用者は同じボタンをもう一度押す。
    /// 結末だけを見るテストではこの 1 回を消しても気づけないので、書き込みの列で固定する。
    /// <para>
    /// 保留側にも同じ形のテストがあったが、待機中から保留できなくなって相手が居なくなった
    /// （保留の入口は実行中だけで、そこにはハンドラが居る）。残っているのは再開だけ。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 停止中の再開は確定待ちを一度保存してから落とす()
    {
        CancelJobCallLog log = new();
        ResumeJobHandler resume = new(
            new CallLoggingJobStore(_store, log),
            new FixedTimeProvider(Requested),
            NullLogger<ResumeJobHandler>.Instance);

        await AddAsync(JobAt("job-1", nameof(JobStatus.Paused)));

        JobControlResult result = await resume.HandleAsync("job-1", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(JobStatus.Resumed), result.Job?.Status);
        Assert.Equal(["update:job-1:Resuming", "update:job-1:Resumed"], log.Entries);
    }

    /// <summary>
    /// 実行中の Job への保留は確定させない。受理するのは境界まで来たハンドラ。
    /// </summary>
    [Fact]
    public async Task 実行中の保留は要求だけ書いて確定させない()
    {
        CancelJobCallLog log = new();
        PauseJobHandler pause = new(
            new CallLoggingJobStore(_store, log),
            new FixedTimeProvider(Requested),
            NullLogger<PauseJobHandler>.Instance);

        await AddAsync(JobAt("job-1", nameof(JobStatus.InProgress)));

        JobControlResult result = await pause.HandleAsync("job-1", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(JobStatus.Pausing), result.Job?.Status);
        Assert.Equal(["update:job-1:Pausing"], log.Entries);
    }

    [Fact]
    public async Task 停止中からの再開でも開始時刻は残る()
    {
        await AddAsync(JobAt("job-1", nameof(JobStatus.Paused)));

        JobControlResult result = await _resume.HandleAsync("job-1", CancellationToken.None);

        Assert.True(result.IsSuccess);

        // 待ち行列へ戻るが「走ったことがある」事実は残る。開始時刻は最初のまま。
        Job saved = await SavedAsync("job-1");
        Assert.Equal(JobStatus.Resumed, saved.Status);
        Assert.Equal(Created.AddMinutes(1), saved.StartedAt);
    }

    [Theory]
    [InlineData("job-9")]
    [InlineData("")]
    [InlineData(null)]
    public async Task 対象が無ければどちらの要求も対象なしとして返る(string? id)
    {
        await AddAsync(JobAt("job-1", nameof(JobStatus.InProgress)));

        JobControlResult paused = await _pause.HandleAsync(id!, CancellationToken.None);
        JobControlResult resumed = await _resume.HandleAsync(id!, CancellationToken.None);

        Assert.False(paused.IsSuccess);
        Assert.Null(paused.Job);
        Assert.False(resumed.IsSuccess);
        Assert.Null(resumed.Job);
    }

    /// <summary>
    /// 読み出しと保存の間に他所が書いていたら、書き戻さずに読み直してやり直す。
    /// </summary>
    /// <remarks>
    /// この再試行を外しても結末は「受理」で返るので、外から見ると成功したように見える
    /// （API は 200、画面には「一時停止しました」）。実際には DB が変わらないまま
    /// Job が走り続ける。**変異検査でこの穴が素通りしていた**ので、両方の口に置く。
    /// </remarks>
    [Fact]
    public async Task 読み出しと保存の間に書かれたら読み直してやり直す()
    {
        InterferingJobStore interfering = new(_store);
        FixedTimeProvider time = new(Requested);
        PauseJobHandler pause = new(interfering, time, NullLogger<PauseJobHandler>.Instance);

        await AddAsync(JobAt("job-1", nameof(JobStatus.InProgress)));

        // 書き戻す直前に、状態を動かさない書き込み（編集）を差し込む。版が進むので
        // 1 回目の書き戻しは通らない。状態は InProgress のままなので、読み直せば受理できる。
        interfering.BeforeNextUpdate = async () =>
        {
            Job edited = await SavedAsync("job-1");
            edited.ChangeParameters("9 9");
            Assert.True(await _store.UpdateAsync(edited, CancellationToken.None));
        };

        JobControlResult result = await pause.HandleAsync("job-1", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(JobStatus.Pausing), result.Job?.Status);

        // 保存まで届いていること（受理を返しただけ、では通らない）。
        Job saved = await SavedAsync("job-1");
        Assert.Equal(JobStatus.Pausing, saved.Status);
        Assert.Equal("9 9", saved.Parameters);
        Assert.Equal(2, interfering.UpdateAttempts);
    }

    /// <summary>再開側も同じ。<see cref="読み出しと保存の間に書かれたら読み直してやり直す"/> を参照。</summary>
    [Fact]
    public async Task 再開も読み出しと保存の間に書かれたら読み直してやり直す()
    {
        InterferingJobStore interfering = new(_store);
        FixedTimeProvider time = new(Requested);
        ResumeJobHandler resume = new(interfering, time, NullLogger<ResumeJobHandler>.Instance);

        await AddAsync(JobAt("job-1", nameof(JobStatus.Paused)));

        interfering.BeforeNextUpdate = async () =>
        {
            Job edited = await SavedAsync("job-1");
            edited.ChangeParameters("9 9");
            Assert.True(await _store.UpdateAsync(edited, CancellationToken.None));
        };

        JobControlResult result = await resume.HandleAsync("job-1", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(JobStatus.Resumed), (await SavedAsync("job-1")).Status.ToString());

        // 要求の書き戻しで 2 回（1 回目は編集に負ける）、確定の書き戻しで 1 回。
        Assert.Equal(3, interfering.UpdateAttempts);
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
            target == JobStatus.Registered ? null : Created.AddMinutes(1),
            target.IsTerminal() ? Created.AddMinutes(2) : null,
            null);
    }
}
