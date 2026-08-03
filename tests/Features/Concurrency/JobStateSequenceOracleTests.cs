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
            [JobStatus.Queued, JobStatus.Running, JobStatus.Cancelling, JobStatus.Completed]));
    }

    [Fact]
    public void 観測が飛んでいても説明できる()
    {
        // 読み取りは連続していないので、途中の状態を見逃すのは正常。
        Assert.Null(JobStateSequenceOracle.FindViolation([JobStatus.Queued, JobStatus.Completed]));
    }

    [Fact]
    public void 同じ状態が続くのは説明できる()
    {
        Assert.Null(JobStateSequenceOracle.FindViolation(
            [JobStatus.Running, JobStatus.Running, JobStatus.Running]));
    }

    [Theory]
    [InlineData(JobStatus.Running, JobStatus.Queued)]
    [InlineData(JobStatus.Cancelling, JobStatus.Running)]
    [InlineData(JobStatus.Completed, JobStatus.Cancelling)]
    [InlineData(JobStatus.Cancelled, JobStatus.Completed)]
    [InlineData(JobStatus.Failed, JobStatus.Running)]
    public void 後退した列は説明できない(JobStatus from, JobStatus to)
    {
        Assert.NotNull(JobStateSequenceOracle.FindViolation([from, to]));
    }

    [Fact]
    public void Queuedから到達できない状態で始まる列は説明できない()
    {
        // Job は必ず Queued で作られる。最初の観測がそこから来られないなら、
        // 状態機械を通さずに書かれたということ。
        Assert.Null(JobStateSequenceOracle.FindViolation([JobStatus.Cancelled]));
        Assert.NotNull(JobStateSequenceOracle.FindViolation([JobStatus.Running, JobStatus.Queued]));
    }

    [Fact]
    public void 空の列と1件の列は説明できる()
    {
        Assert.Null(JobStateSequenceOracle.FindViolation([]));
        Assert.Null(JobStateSequenceOracle.FindViolation([JobStatus.Queued]));
    }

    [Theory]
    [InlineData(JobStatus.Queued, JobStatus.Running, true)]
    [InlineData(JobStatus.Queued, JobStatus.Cancelled, true)]
    [InlineData(JobStatus.Queued, JobStatus.Completed, false)]
    [InlineData(JobStatus.Running, JobStatus.Cancelled, false)]
    [InlineData(JobStatus.Cancelling, JobStatus.Cancelled, true)]
    public void 一手で行けるかは状態機械と一致する(JobStatus from, JobStatus to, bool expected)
    {
        Assert.Equal(expected, JobStateSequenceOracle.IsLegalStep(from, to));
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
