using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

/// <summary>
/// サブタスクの進行・永続化・協調的キャンセルを、針を進める時計で決定的に確かめる。
/// </summary>
/// <remarks>
/// 実時間は待たない。1 秒の待ちは <see cref="ManualTimeProvider"/> のタイマーで表現され、
/// タイマーが張られたこと（<see cref="ManualTimeProvider.WaitForTimersAsync"/>）が
/// 「直前の遷移の書き込みまで済んでいる」ことの証拠になる。
/// ハンドラは書いてから待ちに入るので、時間を測らずに状態を検分できる。
/// </remarks>
public sealed class SubTaskJobHandlerTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly JobId Job1 = JobId.From("job-1");

    private readonly TemporarySubTaskStore _store = new();
    private readonly TemporaryJobStore _jobs = new();
    private readonly ManualTimeProvider _time = new();
    private readonly SubTaskJobHandler _handler;

    public SubTaskJobHandlerTests() => _handler = new SubTaskJobHandler(_store, _jobs, _time);

    public void Dispose()
    {
        _jobs.Dispose();
        _store.Dispose();
    }

    [Fact]
    public async Task サブタスクは連番順に進み遷移のたびに永続化される()
    {
        Task execution = _handler.ExecuteAsync(Job1, "2 2", CancellationToken.None);

        // 最初の待ちが張られた時点で、全行の作成と 1 つ目の Running が書き込み済み。
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);
        Assert.Equal([SubTaskStatus.Running, SubTaskStatus.Pending], await StatusesAsync());

        // 1 秒目を越えても、まだ 1 つ目の実行中（m=2 なので 2 秒目が要る）。
        _time.Advance(SubTaskJobHandler.Step);
        await _time.WaitForTimersAsync(2).WaitAsync(WaitLimit);
        Assert.Equal([SubTaskStatus.Running, SubTaskStatus.Pending], await StatusesAsync());

        // 2 秒目で 1 つ目が完了し、2 つ目が動き出す。
        _time.Advance(SubTaskJobHandler.Step);
        await _time.WaitForTimersAsync(3).WaitAsync(WaitLimit);
        Assert.Equal([SubTaskStatus.Completed, SubTaskStatus.Running], await StatusesAsync());

        _time.Advance(SubTaskJobHandler.Step);
        await _time.WaitForTimersAsync(4).WaitAsync(WaitLimit);
        _time.Advance(SubTaskJobHandler.Step);

        await execution.WaitAsync(WaitLimit);
        Assert.Equal([SubTaskStatus.Completed, SubTaskStatus.Completed], await StatusesAsync());
    }

    [Fact]
    public async Task キャンセルすると実行中も未着手もCancelledに畳まれて残る()
    {
        using CancellationTokenSource cancellation = new();
        Task execution = _handler.ExecuteAsync(Job1, "3 5", cancellation.Token);

        // 1 つ目が走り出したところで要求する。
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);
        await cancellation.CancelAsync();

        // Job の結末（Cancelled）は実行エンジンの領分なので、ここでは OCE が出ることまで。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(WaitLimit));

        // 実行中だった 1 つ目も、未着手だった 2 つ目 3 つ目も、畳まれた事実が永続化されている。
        // トークンは発火済みなので、この書き込みが中断されていないことの確認でもある。
        Assert.Equal(
            [SubTaskStatus.Cancelled, SubTaskStatus.Cancelled, SubTaskStatus.Cancelled],
            await StatusesAsync());
    }

    [Fact]
    public async Task 完了済みのサブタスクはキャンセルでも上書きされない()
    {
        using CancellationTokenSource cancellation = new();
        Task execution = _handler.ExecuteAsync(Job1, "2 1", cancellation.Token);

        // 1 つ目を完走させ、2 つ目の待ちに入ったところで要求する。
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);
        _time.Advance(SubTaskJobHandler.Step);
        await _time.WaitForTimersAsync(2).WaitAsync(WaitLimit);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(WaitLimit));

        // 完了の事実は消えない。畳まれるのは終端に達していないものだけ。
        Assert.Equal([SubTaskStatus.Completed, SubTaskStatus.Cancelled], await StatusesAsync());
    }

    /// <summary>
    /// 書き手はこのハンドラだけという契約が破られたら、黙って続けずに Job を失敗させる。
    /// 続けると進捗の記録が嘘になる。
    /// </summary>
    [Fact]
    public async Task 他所がサブタスクを書き換えていたら失敗として表に出る()
    {
        Task execution = _handler.ExecuteAsync(Job1, "1 1", CancellationToken.None);
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);

        // 横から 1 つ目を畳んでしまう（実行中 → Cancelled は正規の遷移なので書ける）。
        SubTask hijacked = (await _store.ListByJobAsync(Job1, CancellationToken.None))[0];
        SubTaskTransition cancelled = hijacked.Apply(SubTaskTrigger.Cancel);
        Assert.True(await _store.UpdateAsync(hijacked, cancelled.Previous, CancellationToken.None));

        // ハンドラが完了を書こうとすると、期待（Running）と実際（Cancelled）が食い違う。
        _time.Advance(SubTaskJobHandler.Step);

        await Assert.ThrowsAsync<InvalidOperationException>(() => execution.WaitAsync(WaitLimit));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3")]
    [InlineData("3 5 7")]
    [InlineData("a 5")]
    [InlineData("3 b")]
    [InlineData("0 5")]
    [InlineData("3 0")]
    [InlineData("-1 5")]
    public async Task 個数と秒数として読めない指定は失敗し行も作られない(string parameters)
    {
        await Assert.ThrowsAsync<FormatException>(
            () => _handler.ExecuteAsync(Job1, parameters, CancellationToken.None));

        Assert.Empty(await _store.ListByJobAsync(Job1, CancellationToken.None));
    }

    /// <summary>
    /// 走り出す前に届いたキャンセルは、行を 1 つも作らずに抜ける。
    /// </summary>
    /// <remarks>
    /// キャンセルの受け口は Running を書き戻すより前に用意されるので、要求が claim と
    /// ほぼ同時に届くとハンドラは「もう要らない」と分かった状態で始まる。待機に渡した
    /// トークンだけに頼ると、その前に N 行を作って 1 つ目を開始済みにしてしまい、
    /// <b>走る前に消された Job に走った形跡が残る</b>。
    /// </remarks>
    [Fact]
    public async Task 走り出す前のキャンセルは行を作らずに抜ける()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _handler.ExecuteAsync(Job1, "3 5", cancellation.Token));

        Assert.Empty(await _store.ListByJobAsync(Job1, CancellationToken.None));
    }

    private async Task<IReadOnlyList<SubTaskStatus>> StatusesAsync() =>
        [.. (await _store.ListByJobAsync(Job1, CancellationToken.None)).Select(subTask => subTask.Status)];
}
