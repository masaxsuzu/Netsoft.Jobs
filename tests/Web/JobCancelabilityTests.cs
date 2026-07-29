using Netsoft.Jobs.Features;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// キャンセルボタンの可否判定。判断の本体は Domain の状態機械にあるので、
/// ここで確かめるのは「DTO の文字列状態が正しく写像されるか」と「未知の値の扱い」。
/// </summary>
public sealed class JobCancelabilityTests
{
    [Theory]
    [InlineData("Queued", true)]
    [InlineData("Running", true)]
    [InlineData("Cancelling", false)]
    [InlineData("Completed", false)]
    [InlineData("Failed", false)]
    [InlineData("Cancelled", false)]
    public void 状態機械の判定がそのまま可否になる(string status, bool expected)
    {
        Assert.Equal(expected, JobCancelability.CanRequestCancel(CreateDto(status)));
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
        Assert.False(JobCancelability.CanRequestCancel(CreateDto(status)));
    }

    private static JobDto CreateDto(string status) =>
        new(
            Id: "job-1",
            Name: "テスト",
            JobType: "demo",
            Parameters: "",
            Status: status,
            CreatedAt: DateTimeOffset.UtcNow,
            StartedAt: null,
            FinishedAt: null,
            FailureMessage: null);
}
