using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests;

public sealed class GuidV7JobIdFactoryTests
{
    [Fact]
    public void 採番した識別子は生成順に辞書順で並ぶ()
    {
        // v7 を選んだ理由そのもの。ここが崩れると Id を第 2 ソートキーにした並びが安定しない。
        GuidV7JobIdFactory factory = new();

        List<string> ids = [];
        for (int i = 0; i < 50; i++)
        {
            ids.Add(factory.Create().Value);

            // 同一ミリ秒内でも単調増加する実装だが、時刻部分が進む場合も含めて確かめたい。
            Thread.Sleep(1);
        }

        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
    }

    [Fact]
    public void 採番した識別子は空にならず重複もしない()
    {
        GuidV7JobIdFactory factory = new();

        List<JobId> ids = [.. Enumerable.Range(0, 100).Select(_ => factory.Create())];

        Assert.All(ids, id => Assert.False(id.IsEmpty));
        Assert.Equal(100, ids.Distinct().Count());
    }
}
