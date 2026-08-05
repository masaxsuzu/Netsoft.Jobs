using Netsoft.Jobs.Domain;

using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Features.Tests.Execution;

public sealed class DemoJobHandlerTests
{
    private readonly DemoJobHandler _handler = new(TimeProvider.System);

    [Fact]
    public void 対応するJobTypeはdemo()
    {
        Assert.Equal("demo", _handler.JobType);
    }

    [Fact]
    public async Task 待ち時間が0なら即座に終わる()
    {
        await _handler.ExecuteAsync(JobId.From("job-1"), "0", CancellationToken.None);
    }

    [Fact]
    public void パラメータが空なら既定の待ち時間になる()
    {
        // 待ち時間そのものは観測できないので、既定値で待ちに入る（即座に終わらない）ことで確かめる。
        Task execution = _handler.ExecuteAsync(JobId.From("job-1"), string.Empty, CancellationToken.None);

        Assert.False(execution.IsCompleted);
        Assert.True(DemoJobHandler.DefaultDuration > TimeSpan.Zero);
    }

    [Theory]
    [InlineData("十秒")]
    [InlineData("10秒")]
    [InlineData("-1")]
    [InlineData("{}")]
    public async Task 待ち時間として読めないパラメータは例外になる(string parameters)
    {
        // 既定値で流すと、書き損じたことに利用者が気づけない。
        await Assert.ThrowsAsync<FormatException>(
            () => _handler.ExecuteAsync(JobId.From("job-1"), parameters, CancellationToken.None));
    }

    [Fact]
    public async Task キャンセルされると待機をやめる()
    {
        using CancellationTokenSource cancellation = new();

        // 十分に長い待ち時間を指定しても、キャンセルで即座に終わること。
        Task execution = _handler.ExecuteAsync(JobId.From("job-1"), "600", cancellation.Token);
        Assert.False(execution.IsCompleted);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }
}
