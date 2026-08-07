namespace Netsoft.Jobs.Analyzers.Tests;

/// <summary>
/// 「1 つに決めた型を new しない」の検査（NJ0002）が、何を落として何を通すか。
/// </summary>
/// <remarks>
/// この検査が守るのは、2 つ目ができても<b>例外にならない</b>型。合図の箱が 2 つあれば
/// 鳴らす側と待つ側がすれ違い、実行エンジンのファクトリが 2 つあればエンジンが 2 本立つ。
/// どちらも黙って動かなくなるだけなので、ビルドが緑であることは何の証拠にもならない。
/// </remarks>
public sealed class SingleInstanceAnalyzerTests
{
    // 印は名前で照合する。src の各アセンブリが自分の宣言を持つので、
    // ここでも同じ名前の属性を断片の中に置く。
    private const string Marker =
        """
        [System.AttributeUsage(System.AttributeTargets.Class)]
        sealed class SingleInstanceAttribute : System.Attribute { }
        """;

    [Fact]
    public async Task 印の付いた型をnewすると落とす()
    {
        string[] reported = await AnalyzeAsync(
            $$"""
            {{Marker}}
            [SingleInstance]
            sealed class Feed { }
            class C
            {
                void M() { _ = new Feed(); }
            }
            """);

        Assert.Equal([SingleInstanceAnalyzer.DiagnosticId], reported);
    }

    /// <summary>
    /// 型を書かない <c>new()</c> でも落とす。片方だけだと、書き方を変えるだけで抜けられる。
    /// </summary>
    [Fact]
    public async Task 型を書かないnewでも落とす()
    {
        string[] reported = await AnalyzeAsync(
            $$"""
            {{Marker}}
            [SingleInstance]
            sealed class Feed { }
            class C
            {
                private readonly Feed _feed = new();
            }
            """);

        Assert.Equal([SingleInstanceAnalyzer.DiagnosticId], reported);
    }

    /// <summary>
    /// 印の付いた型でも、受け取って使うだけなら通す。禁じているのは作ることだけで、
    /// DI から受け取る形はまさにこれ。
    /// </summary>
    [Fact]
    public async Task 受け取って使うだけなら通す()
    {
        string[] reported = await AnalyzeAsync(
            $$"""
            {{Marker}}
            [SingleInstance]
            sealed class Feed { public void Publish() { } }
            class C
            {
                private readonly Feed _feed;
                public C(Feed feed) { _feed = feed; }
                void M() => _feed.Publish();
            }
            """);

        Assert.Empty(reported);
    }

    [Fact]
    public async Task 印の無い型はいくつ作ってもよい()
    {
        string[] reported = await AnalyzeAsync(
            $$"""
            {{Marker}}
            sealed class Row { }
            class C
            {
                void M() { _ = new Row(); _ = new Row(); }
            }
            """);

        Assert.Empty(reported);
    }

    private static Task<string[]> AnalyzeAsync(string source) =>
        AnalyzerProbe.RunAsync(new SingleInstanceAnalyzer(), source);
}
