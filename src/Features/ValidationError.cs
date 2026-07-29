namespace Netsoft.Jobs.Features;

/// <summary>
/// 入力が受け付けられなかった理由。どの項目が原因かを持つ。
/// </summary>
/// <remarks>
/// 項目名を持たせるのは、UI が入力欄の横にメッセージを出せるようにするため。
/// メッセージだけを返すと、画面側が文言を解析して項目を推測することになる。
/// <paramref name="Field"/> は HTTP の本文と同じ camelCase にそろえる（例 <c>jobType</c>）。
/// 画面と API で別の名前を使うと、同じエラーを二通りに扱うことになる。
/// </remarks>
/// <param name="Field">原因となった入力項目の名前。</param>
/// <param name="Message">利用者に見せる説明。</param>
public sealed record ValidationError(string Field, string Message);
