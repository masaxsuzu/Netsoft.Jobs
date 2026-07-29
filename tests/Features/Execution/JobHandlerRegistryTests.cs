using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

public sealed class JobHandlerRegistryTests
{
    [Fact]
    public void 登録した種類のハンドラを引ける()
    {
        ControllableJobHandler handler = new("demo");
        JobHandlerRegistry registry = new([handler]);

        Assert.Same(handler, registry.Find("demo"));
    }

    [Theory]
    [InlineData("Demo")]
    [InlineData("DEMO")]
    public void 種類の大文字小文字は区別しない(string jobType)
    {
        // JobType は利用者の入力そのもの。大小の違いで解決できたりできなかったりしない。
        ControllableJobHandler handler = new("demo");
        JobHandlerRegistry registry = new([handler]);

        Assert.Same(handler, registry.Find(jobType));
    }

    [Theory]
    [InlineData("未登録")]
    [InlineData("")]
    [InlineData(" ")]
    public void 対応するハンドラが無ければnullを返す(string jobType)
    {
        JobHandlerRegistry registry = new([new ControllableJobHandler("demo")]);

        Assert.Null(registry.Find(jobType));
    }

    [Fact]
    public void 同じ種類のハンドラが重複していたら例外になる()
    {
        // どちらが動くかを黙って決めると、Job が意図しない処理で実行されてしまう。
        Assert.Throws<ArgumentException>(() =>
            new JobHandlerRegistry([new ControllableJobHandler("demo"), new ControllableJobHandler("DEMO")]));
    }
}
