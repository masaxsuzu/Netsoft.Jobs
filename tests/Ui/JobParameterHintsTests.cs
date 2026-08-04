namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// パラメータ欄の説明文の対応表。サーバは種類が存在することしか教えてくれないので、
/// 「何を入れるのか」を出せるかどうかはここだけで決まる。
/// </summary>
public sealed class JobParameterHintsTests
{
    [Theory]
    [InlineData("demo", "待ち秒数（例: 10）")]
    [InlineData("archive", "固めるディレクトリのパス")]
    public void 知っている種類はその種類の説明を返す(string jobType, string expected)
    {
        Assert.Equal(expected, JobParameterHints.For(jobType));
    }

    [Theory]
    [InlineData("Demo")]
    [InlineData("ARCHIVE")]
    public void 種類の大文字小文字は区別しない(string jobType)
    {
        // 種類の解決（JobHandlerRegistry）が区別しないので、説明だけ出ないことがあってはならない。
        Assert.NotEqual(JobParameterHints.Fallback, JobParameterHints.For(jobType));
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
