using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.RegisterJob;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.RegisterJob;

/// <summary>
/// 登録 1 回の結末を、シナリオ単位で固定する。
/// </summary>
/// <remarks>
/// 保存された Job・応答・ログは同じ 1 回の登録の別々の面なので、
/// 1 シナリオ 1 テストに束ねてある。HTTP への写し（200/400）は tests/Web の
/// JobApiTests、採番の一意性は GuidV7JobIdFactoryTests が持つ。
/// </remarks>
public sealed class RegisterJobHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly RecordingJobTraceContextStore _traceContexts = new();
    private readonly RecordingLogger<RegisterJobHandler> _logger = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task 登録すると入力どおりの待機中のJobが保存されログが残る()
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", "これは JSON ではない"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Job saved = Assert.Single(await ListAsync());
        Assert.Equal(JobId.From("job-1"), saved.Id);
        Assert.Equal(JobStatus.Registered, saved.Status);
        Assert.Equal("毎晩の集計", saved.Name);
        Assert.Equal("Demo", saved.JobType);

        // Parameters はハンドラだけが解釈する不透明な文字列。ここでは中身を検査しない。
        Assert.Equal("これは JSON ではない", saved.Parameters);

        // 時刻は注入した時計、識別子は注入した採番器のもの。まだ動いていないので開始も終了も無い。
        Assert.Equal(Now, saved.CreatedAt);
        Assert.Null(saved.StartedAt);
        Assert.Null(saved.FinishedAt);

        // 応答は保存されたものの写し。読み直させない。
        Assert.Equal("job-1", result.Value.Id);
        Assert.Equal(nameof(JobStatus.Registered), result.Value.Status);
        Assert.Equal(saved.Name, result.Value.Name);
        Assert.Equal(saved.JobType, result.Value.JobType);
        Assert.Equal(saved.Parameters, result.Value.Parameters);
        Assert.Equal(Now, result.Value.CreatedAt);

        // 名前付きの値で残ることまで確かめる。構造化コレクタは JobId というパラメータ名で絞る。
        RecordedLog entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("job-1", entry.State["JobId"]);
        Assert.Equal("Demo", entry.State["JobType"]);
    }

    [Fact]
    public async Task パラメータが空文字でも登録できる()
    {
        // 引数を取らない Job があるので、空文字は不正ではない。null だけを弾く。
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", string.Empty),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Job saved = Assert.Single(await ListAsync());
        Assert.Equal(string.Empty, saved.Parameters);
    }

    /// <summary>
    /// 検証エラーは保存もログも残さない。400 として応答に出るもので、
    /// JobId も採番されていないから、ログに書いてもノイズになる。
    /// </summary>
    [Theory]
    [InlineData("", "Demo", "{}", "name")]
    [InlineData(" ", "Demo", "{}", "name")]
    [InlineData("\t", "Demo", "{}", "name")]
    [InlineData("毎晩の集計", "", "{}", "jobType")]
    [InlineData("毎晩の集計", " ", "{}", "jobType")]
    [InlineData("毎晩の集計", "\t", "{}", "jobType")]
    [InlineData("毎晩の集計", "Demo", null, "parameters")]
    [InlineData(" ", " ", "{}", "name", "jobType")]
    public async Task 入力が欠けていると項目ごとの理由で失敗し何も残らない(
        string name, string jobType, string? parameters, params string[] expectedFields)
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand(name, jobType, parameters!),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        // 画面が全項目のエラーを一度に出せるよう、最初の 1 件で打ち切らない。
        Assert.Equal(expectedFields, result.Errors.Select(error => error.Field));
        Assert.All(result.Errors, error => Assert.False(string.IsNullOrWhiteSpace(error.Message)));

        Assert.Empty(await ListAsync());
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task 登録時のActivityのTraceparentが保存される()
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        using Activity activity = StartRecordedActivity();

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", "{}"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(activity.Id, _traceContexts.Saved[JobId.From("job-1")]);
    }

    /// <summary>
    /// サンプリングされていない Activity の traceparent は flags=00 で、リンク先のスパンが
    /// どこにもエクスポートされていない。保存しても後から辿れないので、書かない。
    /// </summary>
    [Fact]
    public async Task サンプリングされていないActivityではTraceContextは保存されない()
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        using Activity activity = new Activity("登録リクエスト").Start();

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", "{}"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(activity.Recorded);
        Assert.Empty(_traceContexts.Saved);
    }

    [Fact]
    public async Task Activityが無ければTraceContextは保存されない()
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", "{}"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(_traceContexts.Saved);
    }

    [Fact]
    public async Task TraceContextの保存が失敗しても登録は成功する()
    {
        // 観測は本体の妨げにならない。保存の失敗は Warning のログに落ちるだけで、
        // Job そのものは登録されている。
        _traceContexts.SaveFailure = new IOException("観測の置き場が壊れています。");
        RegisterJobHandler handler = CreateHandler("job-1");

        using Activity activity = StartRecordedActivity();

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", "{}"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(await ListAsync());

        RecordedLog warning = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal("job-1", warning.State["JobId"]);
    }

    private RegisterJobHandler CreateHandler(params string[] ids) =>
        new(_store, new StubJobIdFactory(ids), _timeProvider, _traceContexts, _logger);

    private Task<IReadOnlyList<Job>> ListAsync() => _store.ListAsync(CancellationToken.None);

    /// <summary>
    /// ASP.NET Core がリクエストごとに作る、サンプリング済みの Activity を演じる。
    /// </summary>
    /// <remarks>
    /// Recorded を立てるのは、ハンドラがそれを保存の条件にしているから。
    /// 素の <see cref="Activity"/> は flags=00（未サンプリング）で始まるので、
    /// 立てないと「保存されない」側のテストになってしまう。
    /// </remarks>
    private static Activity StartRecordedActivity()
    {
        Activity activity = new Activity("登録リクエスト").Start();
        activity.ActivityTraceFlags = ActivityTraceFlags.Recorded;

        return activity;
    }
}
