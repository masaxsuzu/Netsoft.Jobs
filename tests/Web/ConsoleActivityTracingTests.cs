using System.Diagnostics;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Web;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// スパンを標準出力へ書き出す購読者の検証。
/// </summary>
/// <remarks>
/// 購読者を作るときは自前の <see cref="ActivitySource"/> を使う。計装が持つものを
/// 借りると、別のテストが同じソースに付けた購読者と混ざる
/// （JobExecutionInstrumentation の注記）。名前さえ同じなら購読は成立する。
/// </remarks>
public sealed class ConsoleActivityTracingTests
{
    [Fact]
    public void 無効なら購読者を作らない()
    {
        StringWriter writer = new();

        Assert.Null(ConsoleActivityTracing.Enable(enabled: false, writer));
    }

    [Fact]
    public void 無効なら計装はスパンを作らない()
    {
        StringWriter writer = new();
        using ActivitySource source = new(JobExecutionInstrumentation.Name);

        using (ConsoleActivityTracing.Enable(enabled: false, writer))
        {
            // 購読者が居ないと StartActivity は null を返す。「出力が空」ではなく
            // 「スパンが存在しない」ことが、この機能が要る理由そのもの。
            Assert.Null(source.StartActivity("job.execute"));
        }

        Assert.Empty(writer.ToString());
    }

    [Fact]
    public void 有効なら止まったスパンを1行として書き出す()
    {
        StringWriter writer = new();
        using ActivitySource source = new(JobExecutionInstrumentation.Name);

        using (ConsoleActivityTracing.Enable(enabled: true, writer))
        {
            using (Activity? activity = source.StartActivity("job.execute"))
            {
                Assert.NotNull(activity);
                activity.SetTag("job.id", "019fd716");
            }
        }

        string[] lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        string line = Assert.Single(lines);

        Assert.StartsWith(ConsoleActivityTracing.Prefix, line, StringComparison.Ordinal);
        Assert.Contains("job.execute", line, StringComparison.Ordinal);

        // 属性が落ちると「どの Job のスパンか」が分からず、出す意味が無くなる。
        // これを守っているのは Format の側で、Sample の値ではない
        // （PropagationData でも属性は残る。ConsoleActivityTracing の注記）。
        Assert.Contains("job.id=019fd716", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 属性が無いスパンも書き出す()
    {
        StringWriter writer = new();
        using ActivitySource source = new(JobExecutionInstrumentation.Name);

        using (ConsoleActivityTracing.Enable(enabled: true, writer))
        {
            source.StartActivity("job.recover")?.Dispose();
        }

        Assert.Contains("job.recover", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void 別の名前のソースは購読しない()
    {
        StringWriter writer = new();
        using ActivitySource other = new("Someone.Else");

        using (ConsoleActivityTracing.Enable(enabled: true, writer))
        {
            other.StartActivity("noise")?.Dispose();
        }

        Assert.Empty(writer.ToString());
    }
}
