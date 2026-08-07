using Netsoft.Jobs.Domain;

using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// テストが実行の途中で止めたり進めたりできる <see cref="IJobHandler"/>。
/// </summary>
/// <remarks>
/// <para>
/// 実行中の状態を確認するために「今ハンドラの中にいる」ことを確定させる必要がある。
/// <see cref="Task.Delay(TimeSpan)"/> で待つと、待ち時間が足りたかどうかにテストの成否が
/// 左右されて CI でランダムに落ちる。時間ではなく <see cref="TaskCompletionSource"/> の
/// 合図で同期する。
/// </para>
/// <para>
/// 使い方は「<see cref="Entered"/> を待つ → 確認や操作をする → <see cref="Release"/> で解放」。
/// 先に <see cref="Release"/> や <see cref="Throw"/> を呼んでおけば、止まらずに即座に終わる。
/// </para>
/// <para>
/// <b>中断は解放の直後に観測する。</b>store を渡すと、解放されたところで自分の行を読み、
/// Cancelling / Pausing なら対応する例外を投げる ── 実物のハンドラが待ちのたびにやることを、
/// 観測点 1 つに縮めた形。渡さなければ中断を見ないハンドラとして振る舞う。
/// 時間で観測しないのは、待ち時間の足りたかどうかにテストの成否を預けないため。
/// </para>
/// </remarks>
public sealed class ControllableJobHandler : IJobHandler
{
    // RunContinuationsAsynchronously を付けるのは、待っている側の続きが
    // ハンドラのスレッドを乗っ取って実行されるのを防ぐため。
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<string> _executions = [];
    private readonly IJobStore? _jobs;

    public ControllableJobHandler(string jobType, IJobStore? jobs = null)
    {
        JobType = jobType;
        _jobs = jobs;
    }

    /// <inheritdoc />
    public string JobType { get; }

    /// <summary>ハンドラに入ったら完了するタスク。2 回目以降の実行では既に完了している。</summary>
    public Task Entered => _entered.Task;

    /// <summary>実行のたびに受け取った parameters。実行された順に並ぶ。</summary>
    public IReadOnlyList<string> Executions => _executions;

    /// <summary>観測点で Cancelling を見つけたか。</summary>
    public bool CancellationObserved { get; private set; }

    /// <summary>ハンドラを解放して正常終了させる。</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>ハンドラを解放して例外で終了させる。</summary>
    public void Throw(Exception exception) => _released.TrySetException(exception);

    /// <inheritdoc />
    public async Task ExecuteAsync(JobId jobId, string parameters)
    {
        _executions.Add(parameters);
        _entered.TrySetResult();

        // 例外を仕込んであればここで飛ぶ。
        await _released.Task;

        if (_jobs is null)
        {
            return;
        }

        // 観測点。実物と同じ順序で、中断が書かれていれば自分から抜ける。
        Job? job = await _jobs.FindAsync(jobId, CancellationToken.None);
        if (job?.Status == JobStatus.Cancelling)
        {
            CancellationObserved = true;
            throw new JobCancelledException(jobId);
        }

        if (job?.Status == JobStatus.Pausing)
        {
            throw new JobPausedException(jobId);
        }
    }
}
