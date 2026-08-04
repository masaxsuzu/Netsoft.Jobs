namespace Netsoft.Jobs.Features.RegisterJob;

/// <summary>
/// Job の登録要求。
/// </summary>
/// <remarks>
/// HTTP の本文と画面からの入力の両方がこの型に落ちる。
/// ハンドラの入力であって、線の上を流れる契約ではない。Web の型を混ぜないのは、
/// 呼び出し元（今は HTTP エンドポイント）の都合をハンドラの入力に持ち込まないため。
/// </remarks>
/// <param name="Name">利用者が付ける名前。</param>
/// <param name="JobType">どの種類の Job か。</param>
/// <param name="Parameters">不透明なペイロード。中身は一切解釈しない。</param>
public sealed record RegisterJobCommand(string Name, string JobType, string Parameters);
