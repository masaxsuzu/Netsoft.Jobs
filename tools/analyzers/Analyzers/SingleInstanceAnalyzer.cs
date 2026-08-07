using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Netsoft.Jobs.Analyzers;

/// <summary>
/// プロセスに 1 つと決めた型を <c>new</c> していたら落とす。
/// </summary>
/// <remarks>
/// <para>
/// <b>2 つ目ができても、その場では何も起きない型がある。</b>合図の箱が 2 つあれば、
/// 鳴らす側と待つ側が別の箱を持って永久にすれ違う。実行エンジンのファクトリが 2 つあれば、
/// エンジンが 2 本立って同時実行数の前提が崩れる。変更通知の放送局が 2 つあれば、
/// 書き込みが誰にも届かない。どれも例外にならず、<b>ただ動かなくなる</b>。
/// </para>
/// <para>
/// 印は型の側に付ける（<c>[SingleInstance]</c>）。名前の一覧をこのアナライザーが持つ形にすると、
/// 型を足した人が tools/ を直しに来る必要があり、たいてい忘れる。属性なら、
/// 1 つに決めたという判断が型の定義に残る。
/// </para>
/// <para>
/// 属性は<b>型ではなく名前で照合する</b>。src の各アセンブリが自分の
/// <c>SingleInstanceAttribute</c> を持つので、これを共有の置き場へ出す必要が無い
/// （出すと Ui が Domain を参照する、といった要らない依存が生える。
/// CLAUDE.md「定義の形式的な重複はむしろ推奨」）。
/// </para>
/// <para>
/// 掛かるのは src だけ。tests は 1 つに決めた型でも遠慮なく組み立てる
/// ── エンジンを 2 本立てて競合を起こす試験がまさにそれで、あちらは前提を破るのが仕事。
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SingleInstanceAnalyzer : DiagnosticAnalyzer
{
    /// <summary>この検査の番号。</summary>
    public const string DiagnosticId = "NJ0002";

    /// <summary>印として探す属性の名前。</summary>
    public const string MarkerAttributeName = "SingleInstanceAttribute";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "1 つに決めた型を new しない",
        messageFormat: "{0} はプロセスに 1 つと決めた型です。new せず DI から受け取ってください",
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "2 つ目ができても例外にはならず、合図が届かなくなるだけの型がある。"
            + "壊れ方が静かなので、作れないようにしておく。");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // 明示の new と、型を書かない new() の両方。片方だけだと、書き方を変えるだけで抜けられる。
        context.RegisterSyntaxNodeAction(
            Report,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Report(SyntaxNodeAnalysisContext context)
    {
        if (context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol is not
            IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
        {
            return;
        }

        INamedTypeSymbol type = constructor.ContainingType;
        if (!type.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.Name == MarkerAttributeName))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), type.Name));
    }
}
