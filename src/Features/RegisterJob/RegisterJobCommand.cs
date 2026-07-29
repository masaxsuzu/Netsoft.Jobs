namespace Netsoft.Jobs.Features.RegisterJob;

/// <summary>
/// Job の登録要求。
/// </summary>
/// <remarks>
/// HTTP の本文と画面からの入力の両方がこの型に落ちる。
/// 画面が HTTP を経由せずハンドラを直接呼べるよう、Web の型を混ぜない。
/// </remarks>
/// <param name="Name">利用者が付ける名前。</param>
/// <param name="JobType">どの種類の Job か。</param>
/// <param name="Parameters">不透明なペイロード。中身は一切解釈しない。</param>
public sealed record RegisterJobCommand(string Name, string JobType, string Parameters);
