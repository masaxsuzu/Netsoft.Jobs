using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// オラクル自身のテスト。
/// </summary>
/// <remarks>
/// 検査の道具が嘘をつくと、壊れていないことの証明にも壊れていることの証明にもならない。
/// 「壊れた列を壊れていると言う」ことを、道具を使う前にここで固定しておく。
/// </remarks>
public sealed class JobStateSequenceOracleTests
{
    [Fact]
    public void 実際に起こりうる列は説明できる()
    {
        Assert.Null(JobStateSequenceOracle.FindViolation(
            [JobStatus.Registered, JobStatus.Running, JobStatus.Cancelling, JobStatus.Completed]));
    }

    [Fact]
    public void 観測が飛んでいても説明できる()
    {
        // 読み取りは連続していないので、途中の状態を見逃すのは正常。
        Assert.Null(JobStateSequenceOracle.FindViolation([JobStatus.Registered, JobStatus.Completed]));
    }

    [Fact]
    public void 同じ状態が続くのは説明できる()
    {
        Assert.Null(JobStateSequenceOracle.FindViolation(
            [JobStatus.Running, JobStatus.Running, JobStatus.Running]));
    }

    /// <remarks>
    /// Resume が入る前は Running から待ち行列へ戻るのも後退だった。いまは
    /// Pausing → Paused → Resume を経て正当に戻れるので、オラクルが捕まえられる後退は
    /// Cancelling と終端から出る列だけに狭まっている。検出力の低下は閉路の正直な代償で、
    /// 閉路そのものが合法であることは下の「一時停止と再開の列は説明できる」が固定する。
    /// </remarks>
    [Theory]
    [InlineData(JobStatus.Cancelling, JobStatus.Running)]
    [InlineData(JobStatus.Cancelling, JobStatus.Resumed)]
    [InlineData(JobStatus.Cancelling, JobStatus.Paused)]
    [InlineData(JobStatus.Completed, JobStatus.Cancelling)]
    [InlineData(JobStatus.Cancelled, JobStatus.Completed)]
    [InlineData(JobStatus.Failed, JobStatus.Running)]
    public void 後退した列は説明できない(JobStatus from, JobStatus to)
    {
        Assert.NotNull(JobStateSequenceOracle.FindViolation([from, to]));
    }

    [Fact]
    public void 一時停止と再開の列は説明できる()
    {
        // 待ち行列へ戻る唯一の道。観測が飛んでいても（Running → Resumed だけでも）説明できる。
        Assert.Null(JobStateSequenceOracle.FindViolation(
            [JobStatus.Running, JobStatus.Pausing, JobStatus.Paused, JobStatus.Resumed, JobStatus.Running]));
        Assert.Null(JobStateSequenceOracle.FindViolation([JobStatus.Running, JobStatus.Resumed]));
    }

    [Fact]
    public void どの状態も登録直後から到達できる()
    {
        // Job は必ず Registered で作られる。全状態が到達可能になったので、
        // 「最初の観測が Registered から来られない」という違反は現在の機械では起きない
        //（オラクルの検査自体は、将来孤立した状態が増えたときのために残っている）。
        foreach (JobStatus status in Enum.GetValues<JobStatus>())
        {
            Assert.Null(JobStateSequenceOracle.FindViolation([status]));
        }
    }

    [Fact]
    public void 空の列と1件の列は説明できる()
    {
        Assert.Null(JobStateSequenceOracle.FindViolation([]));
        Assert.Null(JobStateSequenceOracle.FindViolation([JobStatus.Registered]));
    }

    [Fact]
    public void 終端からはどこへも行けない()
    {
        foreach (JobStatus terminal in Enum.GetValues<JobStatus>().Where(status => status.IsTerminal()))
        {
            foreach (JobStatus other in Enum.GetValues<JobStatus>().Where(status => status != terminal))
            {
                Assert.False(JobStateSequenceOracle.IsReachable(terminal, other));
            }
        }
    }
}
