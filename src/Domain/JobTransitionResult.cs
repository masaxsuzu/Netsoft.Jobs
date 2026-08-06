using System.Diagnostics.CodeAnalysis;

namespace Netsoft.Jobs.Domain;

/// <summary>
/// 遷移判定の結果。許可されたか、遷移後の状態、拒否された理由を持つ。
/// </summary>
/// <remarks>
/// 拒否は例外にしない。「その状態ではその操作ができない」は利用者の操作から
/// 日常的に起きる分岐であって、プログラムの誤りではないため。
/// </remarks>
public readonly record struct JobTransitionResult
{
    private JobTransitionResult(JobStatus previous, JobStatus status, JobTransitionRejection? rejection)
    {
        Previous = previous;
        Status = status;
        Rejection = rejection;
    }

    /// <summary>
    /// 判定を行った時点の状態。<see cref="Job.Apply"/> が Job を破壊的に変えた後でも、
    /// 「どこから来たか」をこの結果から読める。
    /// </summary>
    /// <remarks>
    /// かつては <see cref="IJobStore.UpdateAsync"/> の期待状態として渡していたが、
    /// 条件付き更新の守りが状態から版（<see cref="Job.Version"/>）へ移ったので、
    /// その用途は無くなった。いま読んでいるのは実行エンジンだけで、結末の書き込みが
    /// 「キャンセル要求中に完走した」のか単なる完了かを見分けてログを分けるのに使う。
    /// </remarks>
    public JobStatus Previous { get; }

    /// <summary>
    /// 許可された場合は遷移後の状態。拒否された場合は現在の状態のまま。
    /// </summary>
    public JobStatus Status { get; }

    /// <summary>拒否された場合の理由。許可された場合は null。</summary>
    public JobTransitionRejection? Rejection { get; }

    /// <summary>遷移が許可されたか。</summary>
    /// <remarks>
    /// <para>
    /// 理由の有無から導く。別に持つと「許可されたのに理由がある」ような、
    /// 生成側が間違えないと作れないはずの組み合わせを表現できてしまう。
    /// </para>
    /// <para>
    /// 属性で「false なら <see cref="Rejection"/> は非 null」を伝える。無いと呼び出し側が
    /// CS8629（null かもしれない値型）を避けるために <c>?? throw</c> を書くことになり、
    /// <see cref="Rejected"/> が非 null しか受け取らない以上<b>決して発火しない分岐</b>が
    /// 拒否を読むすべての場所に増える。値型にも効くことは実測して確かめた。
    /// </para>
    /// </remarks>
    [MemberNotNullWhen(false, nameof(Rejection))]
    public bool IsAllowed => Rejection is null;

    /// <summary>許可された結果を作る。</summary>
    public static JobTransitionResult Allowed(JobStatus current, JobStatus next) => new(current, next, null);

    /// <summary>拒否された結果を作る。状態は変わらないので現在の状態をそのまま返す。</summary>
    public static JobTransitionResult Rejected(JobStatus current, JobTransitionRejection rejection) =>
        new(current, current, rejection);
}
