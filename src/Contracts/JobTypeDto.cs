namespace Netsoft.Jobs.Contracts;

/// <summary>
/// 外に見せる「登録されている Job の種類」の表現。
/// </summary>
/// <remarks>
/// <para>
/// 種類名だけなら文字列の配列で足りるが、オブジェクトの配列にしてある。
/// 後で項目が増えたときに、配列の要素の型が変わる（文字列 → オブジェクト）という
/// 壊れ方をせず、項目を 1 つ足すだけで済むため。
/// </para>
/// <para>
/// 表示用の説明文はここに持たない。何をどう説明するかは見せる側（UI）の関心で、
/// サーバが持つと文言を変えるたびに API の応答が変わることになる。
/// </para>
/// </remarks>
public sealed record JobTypeDto(string JobType);
