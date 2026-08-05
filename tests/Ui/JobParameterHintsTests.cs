namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// パラメータ欄の説明文の対応表。サーバは種類が存在することしか教えてくれないので、
/// 「何を入れるのか」を出せるかどうかはここだけで決まる。
/// </summary>
public sealed class JobParameterHintsTests
{
    /// <remarks>
    /// 大文字の行は「大小を区別しない」ことの検査。種類の解決（JobHandlerRegistry）が
    /// 区別しないので、説明だけ出ないことがあってはならない。
    /// </remarks>
    [Theory]
    [InlineData("subtasks", "サブタスクの個数と各々の秒数（例: 3 5）")]
    [InlineData("SUBTASKS", "サブタスクの個数と各々の秒数（例: 3 5）")]
    public void 知っている種類はその種類の説明を返す(string jobType, string expected)
    {
        Assert.Equal(expected, JobParameterHints.For(jobType));
    }

    [Theory]
    [InlineData("")]
    [InlineData("まだ知らない種類")]
    [InlineData(null)]
    public void 未選択や未知の種類はフォールバックを返す(string? jobType)
    {
        // ハンドラが増えて対応表を直し忘れても、何を入れる欄なのか分からない空欄にはしない。
        Assert.Equal(JobParameterHints.Fallback, JobParameterHints.For(jobType));
    }

    [Fact]
    public void フォールバックは空文字ではない()
    {
        Assert.False(string.IsNullOrWhiteSpace(JobParameterHints.Fallback));
    }
}
