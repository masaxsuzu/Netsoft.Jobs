using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.CancelJob;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.CancelJob;

/// <summary>
/// キャンセル要求 1 回の結末を、シナリオ単位で固定する。
/// </summary>
/// <remarks>
/// 結果の形・保存・呼び出し順・ログは同じ 1 回の要求の別々の面なので、
/// 1 シナリオ 1 テストに束ねてある。分けても守れるものは増えず、数だけが増える。
/// HTTP への写し（200/404/409）は tests/Web の JobApiTests、遷移表そのものは
/// tests/Domain、読み直しループの停止性は Concurrency 側のテストが持つ。
/// </remarks>
public sealed class CancelJobHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = new(2026, 7, 29, 9, 1, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Finished = new(2026, 7, 29, 9, 2, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Requested = new(2026, 7, 29, 9, 3, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _jobs = new();
    private readonly CancelJobCallLog _log = new();
    private readonly InterferingJobStore _interference;
    private readonly CallLoggingJobStore _store;
    private readonly RecordingRunningJobRegistry _runningJobs;
    private readonly FixedTimeProvider _timeProvider = new(Requested);
    private readonly RecordingLogger<CancelJobHandler> _logger = new();
    private readonly CancelJobHandler _handler;

    public CancelJobHandlerTests()
    {
        // 割り込みを仕掛けなければ素通しなので、競合を扱わないテストの見え方は変わらない。
        _interference = new InterferingJobStore(_jobs);
        _store = new CallLoggingJobStore(_interference, _log);
        _runningJobs = new RecordingRunningJobRegistry(_log);
        _handler = new CancelJobHandler(_store, _runningJobs, _timeProvider, _logger);
    }

    public void Dispose() => _jobs.Dispose();

    [Fact]
    public async Task 待機中へのキャンセルはCancelledへ直行し保存とログが残る()
    {
        // ハンドラを起動していないので、受理を待つ相手がいない。
        await AddAsync(Queued("job-1"));

        CancelJobResult result = await CancelAsync("job-1");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Rejection);
        Assert.Equal(nameof(JobStatus.Cancelled), result.Job?.Status);

        // 終端に達したので、要求した時点の終了時刻が入る。
        Job saved = await SavedAsync("job-1");
        Assert.Equal(JobStatus.Cancelled, saved.Status);
        Assert.Equal(Requested, saved.FinishedAt);
        Assert.Equal(["update:job-1:Cancelled", "cancel:job-1"], _log.Entries);

        // Cancelled へ直行したこと（受理待ちでないこと）はこのログで読み取れる。
        RecordedLog entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("job-1", entry.State["JobId"]);
        Assert.Equal(JobStatus.Cancelled, entry.State["Status"]);
        Assert.Contains("受理", entry.Message);
    }

    [Fact]
    public async Task 実行中へのキャンセルは保存してから伝えられ受理待ちになる()
    {
        // ハンドラが動いているので、受理されるまでは終端に進めない。
        await AddAsync(Running("job-1"));

        CancelJobResult result = await CancelAsync("job-1");

        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(JobStatus.Cancelling), result.Job?.Status);

        Job saved = await SavedAsync("job-1");
        Assert.Equal(JobStatus.Cancelling, saved.Status);
        Assert.Null(saved.FinishedAt);

        // 並びが肝。伝達を保存より先にすると、こちらが Cancelling を書く前に
        // ハンドラが終端を書き込みうる。その後に届くこの更新が終端を上書きして、
        // 終わっているのに終わっていない Job ができる。
        Assert.Equal(["update:job-1:Cancelling", "cancel:job-1"], _log.Entries);

        // Job 行に Cancelling の時刻列は無い。要求が受理された時刻はこのログだけが持つ。
        RecordedLog entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("job-1", entry.State["JobId"]);
        Assert.Equal(JobStatus.Cancelling, entry.State["Status"]);
        Assert.Contains("受理", entry.Message);
    }

    /// <summary>
    /// TryRequestCancel の false は失敗ではない（まだ Queued、既に終わった、
    /// 別プロセスが実行している）。これを理由に状態遷移を巻き戻すと、
    /// 受理された事実が消えて画面と DB が食い違う。
    /// </summary>
    [Theory]
    [InlineData(true, nameof(JobStatus.Cancelling))]
    [InlineData(false, nameof(JobStatus.Cancelled))]
    public async Task 伝達の戻り値がfalseでも状態遷移は巻き戻らない(bool running, string expected)
    {
        _runningJobs.Result = false;
        await AddAsync(running ? Running("job-1") : Queued("job-1"));

        CancelJobResult result = await CancelAsync("job-1");

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Job?.Status);
        Assert.Equal(expected, (await SavedAsync("job-1")).Status.ToString());
    }

    [Fact]
    public async Task キャンセル中への再要求は成功として扱い保存も伝達もしない()
    {
        // 利用者から見ればボタンを 2 回押しただけ。要求の意図は既に満たされていて、
        // 状態は Cancelling のまま、トークンは最初の要求で発火済み。やり直すことが無い。
        await AddAsync(Cancelling("job-1"));

        CancelJobResult result = await CancelAsync("job-1");

        Assert.True(result.IsSuccess);
        Assert.Equal(JobTransitionRejection.AlreadyInEffect, result.Rejection);
        Assert.Equal(nameof(JobStatus.Cancelling), result.Job?.Status);
        Assert.Empty(_log.Entries);
    }

    [Theory]
    [InlineData(nameof(JobStatus.Completed))]
    [InlineData(nameof(JobStatus.Failed))]
    [InlineData(nameof(JobStatus.Cancelled))]
    public async Task 終端のJobは拒否され何も書かずに理由がログに残る(string status)
    {
        await AddAsync(Terminal("job-1", status));

        CancelJobResult result = await CancelAsync("job-1");

        // エンドポイントはこれを 409 に写す。現在の状態も返すので、
        // 呼び出し側は読み直さずに表示を決められる。
        Assert.False(result.IsSuccess);
        Assert.Equal(JobTransitionRejection.JobAlreadyFinished, result.Rejection);
        Assert.Equal(status, result.Job?.Status);

        // 実行の結末に手を触れない。保存も伝達も呼ばれていないこと。
        Assert.Empty(_log.Entries);

        // 終わった Job へのキャンセルは利用者の操作として普通に起きること。Warning にしない。
        RecordedLog entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("job-1", entry.State["JobId"]);
        Assert.Equal(JobTransitionRejection.JobAlreadyFinished, entry.State["Rejection"]);
    }

    /// <summary>
    /// 読み出しと保存の間に、実行エンジンがハンドラの完走を Completed として書き込む状況。
    /// ここで Cancelling を上書きすると、終わっている Job が永久に Cancelling のまま固まる。
    /// </summary>
    [Fact]
    public async Task 読み出しと保存の間に完了が書かれても上書きしない()
    {
        await AddAsync(Running("job-1"));

        _interference.BeforeNextUpdate = async () =>
        {
            Job completed = await SavedAsync("job-1");
            Assert.True(completed.Apply(JobTrigger.Complete, Finished).IsAllowed);
            Assert.True(await _jobs.UpdateAsync(completed, CancellationToken.None));
        };

        CancelJobResult result = await CancelAsync("job-1");

        // 読み直した先は終端なので、状態機械が拒否する。エンドポイントはこれを 409 に写す。
        Assert.False(result.IsSuccess);
        Assert.Equal(JobTransitionRejection.JobAlreadyFinished, result.Rejection);
        Assert.Equal(nameof(JobStatus.Completed), result.Job?.Status);

        // 保存されている状態は完了のまま。実行の結末が消えていない。
        Job saved = await SavedAsync("job-1");
        Assert.Equal(JobStatus.Completed, saved.Status);
        Assert.Equal(Finished, saved.FinishedAt);

        // 止める相手はもう居ない。伝えるのは保存できたときだけ。
        Assert.Empty(_runningJobs.RequestedIds);
    }

    [Fact]
    public async Task 読み出しと保存の間に実行が始まったら読み直して要求し直す()
    {
        await AddAsync(Queued("job-1"));

        _interference.BeforeNextUpdate = async () =>
        {
            Job started = await SavedAsync("job-1");
            Assert.True(started.Apply(JobTrigger.Start, Started).IsAllowed);
            Assert.True(await _jobs.UpdateAsync(started, CancellationToken.None));
        };

        CancelJobResult result = await CancelAsync("job-1");

        // 待機中のまま書けていたら Cancelled になっていた。実行が始まっている以上、
        // ハンドラの受理を待つ Cancelling が正しい。
        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(JobStatus.Cancelling), result.Job?.Status);
        Assert.Equal(JobStatus.Cancelling, (await SavedAsync("job-1")).Status);
        Assert.Equal([JobId.From("job-1")], _runningJobs.RequestedIds);
    }

    /// <summary>
    /// 存在しない・識別子の形にならない、はどちらも「対象なし」。エンドポイントは 404 に写す。
    /// 何も指し示していない以上、保存も伝達もログも残さない（ノイズになるだけ）。
    /// </summary>
    [Theory]
    [InlineData("job-2")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task 対象が無ければ何もせずに対象なしとして返る(string? id)
    {
        await AddAsync(Queued("job-1"));

        CancelJobResult result = await _handler.HandleAsync(id!, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Job);
        Assert.Null(result.Rejection);
        Assert.Empty(_log.Entries);
        Assert.Empty(_logger.Entries);
    }

    private Task<CancelJobResult> CancelAsync(string id) => _handler.HandleAsync(id, CancellationToken.None);

    private Task AddAsync(Job job) => _jobs.AddAsync(job, CancellationToken.None);

    private async Task<Job> SavedAsync(string id) =>
        await _jobs.FindAsync(JobId.From(id), CancellationToken.None)
        ?? throw new InvalidOperationException($"Job {id} が保存されていません。");

    private static Job Queued(string id) =>
        Job.Create(JobId.From(id), $"集計 {id}", "Demo", "{}", Created);

    private static Job Running(string id)
    {
        Job job = Queued(id);
        job.Apply(JobTrigger.Start, Started);
        return job;
    }

    private static Job Cancelling(string id)
    {
        Job job = Running(id);
        job.Apply(JobTrigger.RequestCancel, Finished);
        return job;
    }

    private static Job Terminal(string id, string status)
    {
        Job job = Running(id);

        switch (status)
        {
            case nameof(JobStatus.Completed):
                job.Apply(JobTrigger.Complete, Finished);
                break;
            case nameof(JobStatus.Failed):
                job.Apply(JobTrigger.Fail, Finished, "接続できませんでした。");
                break;
            case nameof(JobStatus.Cancelled):
                job.Apply(JobTrigger.RequestCancel, Finished);
                job.Apply(JobTrigger.ConfirmCancelled, Finished);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "終端状態ではありません。");
        }

        return job;
    }
}
