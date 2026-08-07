using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

/// <summary>
/// サブタスクの進行・永続化・中断の観測を、針を進める時計で決定的に確かめる。
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
    private static readonly DateTimeOffset Created = new(2026, 8, 6, 9, 0, 0, TimeSpan.Zero);

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
        Task execution = _handler.ExecuteAsync(Job1, "2 2");

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
        await AddRunningJobAsync("3 5");
        Task execution = _handler.ExecuteAsync(Job1, "3 5");

        // 1 つ目が走り出したところで行に Cancelling と書く。突く相手は居ない。
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);
        await RequestCancelAsync();

        // 待ちを 1 つ越えさせる。観測点はここ（1 サブタスクにつき m 回）。
        _time.Advance(SubTaskJobHandler.Step);

        // Job の結末（Cancelled）は実行エンジンの領分なので、ここでは例外が出ることまで。
        await Assert.ThrowsAsync<JobCancelledException>(() => execution.WaitAsync(WaitLimit));

        // 実行中だった 1 つ目も、未着手だった 2 つ目 3 つ目も、畳まれた事実が永続化されている。
        Assert.Equal(
            [SubTaskStatus.Cancelled, SubTaskStatus.Cancelled, SubTaskStatus.Cancelled],
            await StatusesAsync());
    }

    [Fact]
    public async Task 完了済みのサブタスクはキャンセルでも上書きされない()
    {
        await AddRunningJobAsync("2 1");
        Task execution = _handler.ExecuteAsync(Job1, "2 1");

        // 1 つ目を完走させ、2 つ目の待ちに入ったところで要求する。
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);
        _time.Advance(SubTaskJobHandler.Step);
        await _time.WaitForTimersAsync(2).WaitAsync(WaitLimit);
        await RequestCancelAsync();
        _time.Advance(SubTaskJobHandler.Step);

        await Assert.ThrowsAsync<JobCancelledException>(() => execution.WaitAsync(WaitLimit));

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
        Task execution = _handler.ExecuteAsync(Job1, "1 1");
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
            () => _handler.ExecuteAsync(Job1, parameters));

        Assert.Empty(await _store.ListByJobAsync(Job1, CancellationToken.None));
    }

    /// <summary>
    /// 走り出す前に届いたキャンセルは、行を 1 つも作らずに抜ける。
    /// </summary>
    /// <remarks>
    /// 要求が claim とほぼ同時に届くと、ハンドラは「もう要らない」と書かれた状態で始まる。
    /// 待ちの側の観測点だけに頼ると、その前に N 行を作って 1 つ目を開始済みにしてしまい、
    /// <b>走る前に消された Job に走った形跡が残る</b>。
    /// </remarks>
    [Fact]
    public async Task 走り出す前のキャンセルは行を作らずに抜ける()
    {
        await AddRunningJobAsync("3 5");
        await RequestCancelAsync();

        await Assert.ThrowsAsync<JobCancelledException>(
            () => _handler.ExecuteAsync(Job1, "3 5"));

        Assert.Empty(await _store.ListByJobAsync(Job1, CancellationToken.None));
    }

    /// <summary>
    /// 最後のサブタスクを走らせている最中に一時停止を要求されても、走り切ったなら完走。
    /// </summary>
    /// <remarks>
    /// 境界は「行の突き合わせ → 残りがあるか → 中断の観測」の順で見る。順序が逆だと、
    /// 全部終えた後の境界で Pausing を観測して <see cref="JobPausedException"/> を投げ、
    /// <b>完走した Job が Paused として記録される</b>（状態機械の Pausing + Complete →
    /// Completed という判断とも食い違う）。
    /// </remarks>
    [Fact]
    public async Task 走り切った後に一時停止が要求されていても完走として終わる()
    {
        await AddRunningJobAsync("1 1");

        Task execution = _handler.ExecuteAsync(Job1, "1 1");
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);

        await RequestPauseAsync();
        _time.Advance(SubTaskJobHandler.Step);

        await execution.WaitAsync(WaitLimit);
        Assert.Equal([SubTaskStatus.Completed], await StatusesAsync());
    }

    /// <summary>
    /// 最後の完了を書いた直後にキャンセルが届いても、走り切ったなら完走。
    /// </summary>
    /// <remarks>
    /// キャンセル側の同じ話（<see cref="走り切った後に一時停止が要求されていても完走として終わる"/>）。
    /// 窓は「最後の完了の書き込み」と「次の周回」の間しかないので、書き込みを合図にして起こす。
    /// </remarks>
    [Fact]
    public async Task 走り切った後にキャンセルが届いても完走として終わる()
    {
        InterferingSubTaskStore interfering = new(_store);
        SubTaskJobHandler handler = new(interfering, _jobs, _time);

        await AddRunningJobAsync("1 1");

        Task execution = handler.ExecuteAsync(Job1, "1 1");
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);

        // 最後の完了を書いた直後＝次の境界の直前に要求する。
        interfering.AfterNextUpdate = () => RequestCancelAsync().GetAwaiter().GetResult();
        _time.Advance(SubTaskJobHandler.Step);

        await execution.WaitAsync(WaitLimit);
        Assert.Equal([SubTaskStatus.Completed], await StatusesAsync());
    }

    /// <summary>
    /// 走り出す前のキャンセルでも、行が既にあるなら畳んでから抜ける。
    /// </summary>
    /// <remarks>
    /// 行が無いときだけ入口で抜けてよい（<see cref="走り出す前のキャンセルは行を作らずに抜ける"/>）。
    /// 再開した Job には前回の行が残っているので、同じ場所で抜けると
    /// <b>Job は Cancelled なのに未着手の行が Pending のまま残る</b>。
    /// 畳めるようになった後の観測点まで進んでから抜ける。
    /// </remarks>
    [Fact]
    public async Task 再開した直後のキャンセルは残っている行を畳む()
    {
        await AddRunningJobAsync("2 1");
        await RequestCancelAsync();

        // 前回の実行が 1 つ目まで終えて止まった姿を作る。
        SubTask[] rows = [SubTask.Create(Job1, 0), SubTask.Create(Job1, 1)];
        await _store.AddRangeAsync(rows, CancellationToken.None);
        await ApplyAsync(rows[0], SubTaskTrigger.Start);
        await ApplyAsync(rows[0], SubTaskTrigger.Complete);

        await Assert.ThrowsAsync<JobCancelledException>(
            () => _handler.ExecuteAsync(Job1, "2 1"));

        Assert.Equal([SubTaskStatus.Completed, SubTaskStatus.Cancelled], await StatusesAsync());
    }

    private async Task AddRunningJobAsync(string parameters)
    {
        Job job = Job.Create(Job1, "集計", SubTaskJobHandler.SubTaskJobType, parameters, Created);
        job.Apply(JobTrigger.Start, Created.AddMinutes(1));

        await _jobs.AddAsync(job, CancellationToken.None);
    }

    private async Task RequestCancelAsync()
    {
        Job job = await _jobs.FindAsync(Job1, CancellationToken.None)
            ?? throw new InvalidOperationException("Job が保存されていません。");

        job.Apply(JobTrigger.RequestCancel, Created.AddMinutes(2));
        Assert.True(await _jobs.UpdateAsync(job, CancellationToken.None));
    }

    private async Task RequestPauseAsync()
    {
        Job job = await _jobs.FindAsync(Job1, CancellationToken.None)
            ?? throw new InvalidOperationException("Job が保存されていません。");

        job.Apply(JobTrigger.RequestPause, Created.AddMinutes(2));
        Assert.True(await _jobs.UpdateAsync(job, CancellationToken.None));
    }

    private async Task ApplyAsync(SubTask subTask, SubTaskTrigger trigger)
    {
        SubTaskTransition transition = subTask.Apply(trigger);
        Assert.True(await _store.UpdateAsync(subTask, transition.Previous, CancellationToken.None));
    }

    private async Task<IReadOnlyList<SubTaskStatus>> StatusesAsync() =>
        [.. (await _store.ListByJobAsync(Job1, CancellationToken.None)).Select(subTask => subTask.Status)];
}
