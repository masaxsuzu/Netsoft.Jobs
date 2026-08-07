namespace Netsoft.Jobs.Ui;

/// <summary>
/// プロセスに 1 つだけ在ればよい、と決めた型に付ける印。
/// </summary>
/// <remarks>
/// <para>
/// 付いている型を <c>new</c> すると <c>NJ0002</c> がビルドを落とす（tools/analyzers）。
/// 2 つ目ができても例外にならず、合図が届かなくなるだけ、という壊れ方をする型が対象。
/// </para>
/// <para>
/// アセンブリごとに同じものを持つ。共有の置き場へ出すと、印を使いたいだけの理由で
/// アセンブリ間に依存が生える（CLAUDE.md「定義の形式的な重複はむしろ推奨」）。
/// アナライザーは型ではなく名前で照合するので、これで揃う。
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SingleInstanceAttribute : Attribute;
