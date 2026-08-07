using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Netsoft.Jobs.Analyzers.Tests;

/// <summary>
/// 「排他を使わない」の検査（NJ0001）が、何を落として何を通すか。
/// </summary>
/// <remarks>
/// <para>
/// <b>ビルドが通ることは、この検査が効いていることの証拠にならない。</b>アナライザーが
/// 何も報告しなくなった日も src/ は緑のまま通り、排他が黙って戻ってくる。
/// 効いていることを確かめられるのは、破っている原文を実際に食わせるここだけ。
/// </para>
/// <para>
/// 通す側（<c>Interlocked</c> / <c>Volatile</c>）も同じ重さで固定してある。
/// あれは相互排他ではなく読み書きの順序と不可分性の指定で、待たせるものが無い。
/// 禁止を広げすぎると、排他を消した先の書き方まで塞いでしまう。
/// </para>
/// </remarks>
public sealed class ExclusionAnalyzerTests
{
    [Fact]
    public async Task lock文は落とす()
    {
        string[] reported = await AnalyzeAsync(
            """
            class C
            {
                private readonly object _gate = new();
                void M() { lock (_gate) { } }
            }
            """);

        Assert.Equal([ExclusionAnalyzer.DiagnosticId], reported);
    }

    [Fact]
    public async Task 待ち合わせの型は落とす()
    {
        string[] reported = await AnalyzeAsync(
            """
            using System.Threading;
            class C
            {
                private readonly SemaphoreSlim _gate = new(1, 1);
                void M() => _gate.Wait();
            }
            """);

        Assert.Equal([ExclusionAnalyzer.DiagnosticId], reported);
    }

    /// <summary>
    /// <c>System.Threading.Lock</c> は型としても lock 文としても現れる。
    /// どちらの経路でも捕まえる（片方だけだと、型を書かずに済ませたり逆をしたりで抜けられる）。
    /// </summary>
    [Fact]
    public async Task 新しいLock型は宣言と使用の両方で落とす()
    {
        string[] reported = await AnalyzeAsync(
            """
            using System.Threading;
            class C
            {
                private readonly Lock _gate = new();
                void M() { lock (_gate) { } }
            }
            """);

        Assert.Equal(
            [ExclusionAnalyzer.DiagnosticId, ExclusionAnalyzer.DiagnosticId],
            reported);
    }

    [Fact]
    public async Task 不可分な差し替えと順序の指定は通す()
    {
        string[] reported = await AnalyzeAsync(
            """
            using System.Threading;
            class C
            {
                private string? _held;
                private long _ticks;
                void M()
                {
                    Interlocked.CompareExchange(ref _held, "next", null);
                    Volatile.Write(ref _ticks, 1);
                }
            }
            """);

        Assert.Empty(reported);
    }

    /// <summary>
    /// 素のコードは 1 件も報告しない。ここが緑でないと、上の 3 つが通っても
    /// 「何にでも吠える検査」と区別が付かない。
    /// </summary>
    [Fact]
    public async Task 排他が無ければ何も言わない()
    {
        string[] reported = await AnalyzeAsync(
            """
            class C
            {
                private int _count;
                void M() => _count++;
            }
            """);

        Assert.Empty(reported);
    }

    private static async Task<string[]> AnalyzeAsync(string source)
    {
        // 小さな断片しか食わせないので、参照は runtime の中核だけで足りる。
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        MetadataReference[] references =
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtime, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtime, "System.Threading.dll")),
        ];

        CSharpCompilation compilation = CSharpCompilation.Create(
            "Probe",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // 原文の誤りを検査の結果と取り違えないよう、先に組み立てが通ることを確かめる。
        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new ExclusionAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None);

        return
        [
            .. diagnostics
                .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
                .Select(diagnostic => diagnostic.Id)
        ];
    }
}
