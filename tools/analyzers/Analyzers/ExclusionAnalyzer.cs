using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Netsoft.Jobs.Analyzers;

/// <summary>
/// src/ で相互排他（<c>lock</c> と待ち合わせの道具）を使っていたら落とす。
/// </summary>
/// <remarks>
/// <para>
/// 規則そのものは CLAUDE.md「やらないこと」にある。ここはその機械的な検査で、
/// 規則を読まなくても破れないようにするために置いている。
/// </para>
/// <para>
/// <b>BannedApiAnalyzers では足りない。</b>あちらは記号（型やメソッド）の参照しか見ないので、
/// <c>lock (_gate)</c> のように <c>object</c> を掴むだけの排他が素通りする
/// （<c>Monitor.Enter</c> はコンパイラが生成するもので、原文には現れない）。
/// <c>lock</c> 文そのものを見るには構文を見るしかない。
/// </para>
/// <para>
/// <c>Interlocked</c> と <c>Volatile</c> は禁じない。あれは相互排他ではなく読み書きの
/// 順序と不可分性の指定で、待たせるものが無い。禁じたいのは「他人を待たせて守る」形の方。
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ExclusionAnalyzer : DiagnosticAnalyzer
{
    /// <summary>この検査の番号。抑制するときはこれを使う。</summary>
    public const string DiagnosticId = "NJ0001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "排他で守らない",
        messageFormat: "{0} は使えません。待たせて守るのではなく、順序に依らない形にしてください（CLAUDE.md「やらないこと」）",
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "排他は「重なったら待たせる」ことで正しさを作るので、正しさの根拠が実行の時間軸に乗る。"
            + "版や不可分な差し替えで表せば、何本重なっても、どの順で終わっても結果が変わらない。");

    // 型で見る分。lock 文は構文で見るので、ここには Monitor だけが例外的に入る
    //（Monitor.Enter / Exit を手で書く形を塞ぐため）。
    private static readonly ImmutableHashSet<string> BannedTypes = ImmutableHashSet.Create(
        "System.Threading.Barrier",
        "System.Threading.Lock",
        "System.Threading.ManualResetEventSlim",
        "System.Threading.Monitor",
        "System.Threading.Mutex",
        "System.Threading.ReaderWriterLock",
        "System.Threading.ReaderWriterLockSlim",
        "System.Threading.Semaphore",
        "System.Threading.SemaphoreSlim",
        "System.Threading.SpinLock");

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

        context.RegisterSyntaxNodeAction(ReportLockStatement, SyntaxKind.LockStatement);
        context.RegisterSyntaxNodeAction(
            ReportBannedType, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
    }

    private static void ReportLockStatement(SyntaxNodeAnalysisContext context) =>
        context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetFirstToken().GetLocation(), "lock 文"));

    /// <remarks>
    /// 名前 1 つずつを見るので <c>System.Threading.SemaphoreSlim</c> のような修飾名でも
    /// 型に解決されるのは最後の 1 つだけになり、報告は 1 回で済む。
    /// </remarks>
    private static void ReportBannedType(SyntaxNodeAnalysisContext context)
    {
        if (context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol is not
            INamedTypeSymbol type)
        {
            return;
        }

        string name = type.OriginalDefinition.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
                SymbolDisplayGlobalNamespaceStyle.Omitted));

        if (BannedTypes.Contains(name))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), name));
        }
    }
}
