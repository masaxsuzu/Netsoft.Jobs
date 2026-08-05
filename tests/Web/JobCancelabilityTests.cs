using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// ボタンの可否判定。判断の本体は Domain の状態機械にあるので、
/// ここで確かめるのは「文字列の状態が正しく写像されるか」と「未知の値の扱い」。
/// </summary>
public sealed class JobCancelabilityTests
{
    [Theory]
    [InlineData("Queued", true)]
    [InlineData("Running", true)]
    [InlineData("Cancelling", false)]
    [InlineData("Pausing", true)]
    [InlineData("Paused", true)]
    [InlineData("Completed", false)]
    [InlineData("Failed", false)]
    [InlineData("Cancelled", false)]
    public void 状態機械の判定がそのまま可否になる(string status, bool expected)
    {
        Assert.Equal(expected, JobCancelability.CanRequestCancel(status));
    }

    /// <summary>
    /// 読み戻せない状態は「何が起きるか分からない操作を許すより押させない」側に倒す仕様。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("queued ")]
    public void 未知の状態では押させない(string status)
    {
        Assert.False(JobCancelability.CanRequestCancel(status));
    }

    /// <summary>
    /// 一時停止・再開・編集の可否も同じ流儀（判断は Domain、ここは写像と未知の値の検査）。
    /// </summary>
    [Theory]
    [InlineData("Queued", false, false, true)]
    [InlineData("Running", true, false, true)]
    [InlineData("Pausing", false, true, true)]
    [InlineData("Paused", false, true, true)]
    [InlineData("Cancelling", false, false, false)]
    [InlineData("Completed", false, false, false)]
    [InlineData("Unknown", false, false, false)]
    public void 停止再開編集の可否が状態から写る(string status, bool pause, bool resume, bool edit)
    {
        Assert.Equal(pause, JobPausability.CanRequestPause(status));
        Assert.Equal(resume, JobPausability.CanRequestResume(status));
        Assert.Equal(edit, JobEditability.CanEdit(status));
    }
}
