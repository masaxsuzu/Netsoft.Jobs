using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// 状態から画面の文言への写像。
/// </summary>
/// <remarks>
/// API の <c>Status</c> は enum の名前のままなので、画面に出る語が変わるのはここだけ。
/// 写し忘れると利用者には enum の名前がそのまま出る。
/// </remarks>
public sealed class JobStatusLabelTests
{
    [Theory]
    [InlineData(JobStatus.Registered, "登録済み")]
    [InlineData(JobStatus.InProgress, "Running")]
    [InlineData(JobStatus.Pausing, "保留要求中")]
    [InlineData(JobStatus.Paused, "保留中")]
    [InlineData(JobStatus.Resuming, "再開要求中")]
    [InlineData(JobStatus.Resumed, "再開待ち")]
    [InlineData(JobStatus.Cancelling, "中止要求中")]
    [InlineData(JobStatus.Cancelled, "中止済み")]
    [InlineData(JobStatus.Completed, "完了")]
    [InlineData(JobStatus.Failed, "失敗")]
    public void 状態ごとに文言が決まっている(JobStatus status, string expected)
    {
        Assert.Equal(expected, JobStatusLabel.From(status));
        Assert.Equal(expected, JobStatusLabel.From(JobStatusText.ToText(status)));
    }

    /// <summary>
    /// 状態を足したら文言も足す。足し忘れをここで捕まえる。
    /// </summary>
    /// <remarks>
    /// 上の表に行を足さずに <see cref="JobStatus"/> だけ増やすと、この検査が落ちる。
    /// 「enum の名前がそのまま出る」形で黙って動き続けるのを防ぐため。
    /// </remarks>
    [Fact]
    public void 文言の無い状態は無い()
    {
        foreach (JobStatus status in Enum.GetValues<JobStatus>())
        {
            Assert.NotEqual(JobStatusText.ToText(status), JobStatusLabel.From(status));
        }
    }

    /// <summary>読み戻せない値は畳まずそのまま出す。何が起きているか分からなくなるため。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("registered ")]
    public void 未知の状態はそのまま出す(string status)
    {
        Assert.Equal(status, JobStatusLabel.From(status));
    }
}
