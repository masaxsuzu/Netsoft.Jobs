using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.RegisterJob;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.RegisterJob;

public sealed class RegisterJobHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly RecordingLogger<RegisterJobHandler> _logger = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task 登録すると待機中のJobが保存される()
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", "{}"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Job saved = Assert.Single(await ListAsync());
        Assert.Equal(JobStatus.Queued, saved.Status);
        Assert.Equal(nameof(JobStatus.Queued), result.Value.Status);
    }

    [Fact]
    public async Task 保存されたJobは入力した名前と種類とパラメータを持つ()
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", "これは JSON ではない"),
            CancellationToken.None);

        Job saved = Assert.Single(await ListAsync());
        Assert.Equal("毎晩の集計", saved.Name);
        Assert.Equal("Demo", saved.JobType);
        Assert.Equal("これは JSON ではない", saved.Parameters);

        Assert.Equal(saved.Name, result.Value.Name);
        Assert.Equal(saved.JobType, result.Value.JobType);
        Assert.Equal(saved.Parameters, result.Value.Parameters);
    }

    [Fact]
    public async Task 作成日時は注入した時計の時刻になる()
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", "{}"),
            CancellationToken.None);

        Job saved = Assert.Single(await ListAsync());
        Assert.Equal(Now, saved.CreatedAt);
        Assert.Equal(Now, result.Value.CreatedAt);
        Assert.Null(saved.StartedAt);
        Assert.Null(saved.FinishedAt);
    }

    [Fact]
    public async Task 識別子は注入した採番器の値になる()
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", "{}"),
            CancellationToken.None);

        Job saved = Assert.Single(await ListAsync());
        Assert.Equal(JobId.From("job-1"), saved.Id);
        Assert.Equal("job-1", result.Value.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task 名前が空なら失敗して保存されない(string name)
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand(name, "Demo", "{}"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Empty(await ListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task 種類が空なら失敗して保存されない(string jobType)
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", jobType, "{}"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task パラメータが空文字でも登録できる()
    {
        // 引数を取らない Job があるので、空文字は不正ではない。
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", string.Empty),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Job saved = Assert.Single(await ListAsync());
        Assert.Equal(string.Empty, saved.Parameters);
    }

    [Fact]
    public async Task パラメータが未指定なら失敗して保存されない()
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", null!),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("parameters", Assert.Single(result.Errors).Field);
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task 失敗したときはどの項目が原因か分かる()
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand(" ", " ", "{}"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        // 画面が全項目のエラーを一度に出せるよう、最初の 1 件で打ち切らない。
        Assert.Equal(["name", "jobType"], result.Errors.Select(error => error.Field));
        Assert.All(result.Errors, error => Assert.False(string.IsNullOrWhiteSpace(error.Message)));
    }

    [Fact]
    public async Task 失敗した結果から値を読もうとすると例外になる()
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand(" ", "Demo", "{}"),
            CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public async Task 登録に成功するとJobId付きのログが残る()
    {
        RegisterJobHandler handler = CreateHandler("job-1");

        await handler.HandleAsync(
            new RegisterJobCommand("毎晩の集計", "Demo", "{}"),
            CancellationToken.None);

        // 名前付きの値で残ることまで確かめる。構造化コレクタは JobId というパラメータ名で絞る。
        RecordedLog entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("job-1", entry.State["JobId"]);
        Assert.Equal("Demo", entry.State["JobType"]);
        Assert.Contains("job-1", entry.Message);
    }

    [Fact]
    public async Task 検証エラーではログを残さない()
    {
        // 400 として応答に出るもので、JobId も採番されていない。ログに書いてもノイズになる。
        RegisterJobHandler handler = CreateHandler("job-1");

        Result<JobDto> result = await handler.HandleAsync(
            new RegisterJobCommand(" ", " ", "{}"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task 連続して登録しても識別子が重複しない()
    {
        // 既定の採番器（UUID v7）をそのまま使う。偽物では重複しないことの確認にならない。
        RegisterJobHandler handler = new(_store, new GuidV7JobIdFactory(), _timeProvider, _logger);

        for (int i = 0; i < 100; i++)
        {
            Result<JobDto> result = await handler.HandleAsync(
                new RegisterJobCommand($"集計 {i}", "Demo", "{}"),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
        }

        Assert.Equal(100, (await ListAsync()).Select(job => job.Id).Distinct().Count());
    }

    private RegisterJobHandler CreateHandler(params string[] ids) =>
        new(_store, new StubJobIdFactory(ids), _timeProvider, _logger);

    private Task<IReadOnlyList<Job>> ListAsync() => _store.ListAsync(CancellationToken.None);
}
