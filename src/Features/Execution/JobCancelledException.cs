using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// ハンドラがキャンセル要求を観測して抜けたことを表す。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JobPausedException"/> の対。ハンドラがこれを投げると、エンジンが捕まえて
/// <see cref="JobTrigger.ConfirmCancelled"/> を書く。結末を書くのはエンジンだけ、という
/// 分担は一時停止と同じ。
/// </para>
/// <para>
/// かつてここは <see cref="OperationCanceledException"/> だった。<see cref="CancellationToken"/> で
/// 伝えていたので、中断はトークンの発火として届き、例外の型もそれに従っていた。
/// 伝え方を「ハンドラが自分の行を読む」に変えたので、**誰かに中断させられた**ではなく
/// <b>自分で要求を見つけて抜けた</b>が実際に起きていることになり、型もそちらへ合わせてある。
/// </para>
/// <para>
/// 型を分けたのは、<see cref="OperationCanceledException"/> だと出どころが絞れないため。
/// あれは非同期の口ならどこからでも飛んでくるので、「利用者が中止を押した」と
/// 「別の理由で中断された」を区別できない。区別が付かないと、エンジンは中断を
/// Cancelled と記録してよいかを決められない。
/// </para>
/// </remarks>
public sealed class JobCancelledException : Exception
{
    /// <summary>観測した Job の識別子で生成する。</summary>
    public JobCancelledException(JobId jobId)
        : base($"Job {jobId.Value} はキャンセル要求を観測しました。") => JobId = jobId;

    /// <summary>観測した Job の識別子。</summary>
    public JobId JobId { get; }
}
