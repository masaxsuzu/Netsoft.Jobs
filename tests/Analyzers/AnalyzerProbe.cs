using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Netsoft.Jobs.Analyzers.Tests;

/// <summary>
/// 断片を組み立ててアナライザーに食わせ、報告された番号を並べる。
/// </summary>
/// <remarks>
/// 落ちるはずの原文と落ちないはずの原文を、同じ道で通す。検査ごとに組み立てを写すと、
/// 参照の揃え方が食い違って「原文の誤り」を「検査の結果」と取り違える。
/// </remarks>
internal static class AnalyzerProbe
{
    public static async Task<string[]> RunAsync(DiagnosticAnalyzer analyzer, string source)
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
            .WithAnalyzers([analyzer])
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None);

        return
        [
            .. diagnostics
                .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
                .Select(diagnostic => diagnostic.Id)
        ];
    }
}
