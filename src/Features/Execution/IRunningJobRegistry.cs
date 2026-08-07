using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 実行中の Job にキャンセルを伝える口。
/// </summary>
/// <remarks>
/// 状態を Cancelling にするだけではハンドラは止まらない。ハンドラが持っている
/// <see cref="CancellationToken"/> を発火させる経路が要る。キャンセル機能（コマンド側）は
/// 状態を進めた後にこの口を呼ぶ。
/// インターフェースにしてあるのは、コマンド側のテストが実行エンジンを立てずに
/// 「エンジンへ伝えたか」を確認できるようにするため。
/// </remarks>
public interface IRunningJobRegistry
{
    /// <summary>
    /// 実行中の Job にキャンセルを伝える。
    /// </summary>
    /// <returns>
    /// このプロセスでその Job が実行中で、キャンセルを伝えられたなら true。
    /// 実行中でなければ false。false は失敗ではない（まだ待ち行列、既に終わった、
    /// 別プロセスが実行している、のいずれか）ので、呼び出し側はこれを理由に
    /// 状態遷移を巻き戻さないこと。
    /// <para>
    /// 「これから走るがまだ登録されていない」は<b>この 3 つに含まれない</b>。
    /// エンジンが InProgress を書き戻す前に登録を済ませるので、状態が InProgress に
    /// 見えている間は必ず登録済みで、その状況自体が起きない
    /// （<see cref="JobExecutionEngine.RunOnceAsync"/> の注記を参照）。
    /// もし登録を書き戻しより後ろへ動かすと、false の意味に
    /// 「やることが有るのに今は不可能」が混ざり、この戻り値では区別できなくなる。
    /// </para>
    /// </returns>
    bool TryRequestCancel(JobId id);
}

/// <summary>
/// 実行中の Job の <see cref="JobCancellation"/> を保持する既定の実装。
/// </summary>
/// <remarks>
/// <para>
/// 同時実行数が 1 なので、保持するのは常に高々 1 件。辞書にしていないのはそのため。
/// 並列化するときはここを辞書に変える（<see cref="JobExecutionEngine"/> の注記を参照）。
/// </para>
/// <para>
/// <b>ここは「エンジンのループ」と「HTTP・画面のスレッド」が同じ物を触る唯一の場所</b>だが、
/// 排他は使わない。載せ替えは参照 1 本の不可分な差し替えで、届いたかどうかは
/// <see cref="JobCancellation"/> 側の状態を奪い合って決まる。
/// </para>
/// <para>
/// かつては <c>lock</c> で守っていた。理由は「掴んだ直後にエンジンが実行を終えて
/// <see cref="CancellationTokenSource"/> を破棄すると <see cref="ObjectDisposedException"/> に
/// なりうる」で、これは正しい。<b>破棄する者を「最後に手を離した者」に変えると、その窓は
/// 存在しなくなる</b>（<see cref="JobCancellation"/> の注記）。
/// 窓を閉じるのではなく、窓の理由を無くしてある。
/// </para>
/// <para>
/// 外した受け口への参照はここに残らない。<see cref="Untrack"/> が
/// <c>_current</c> を null に戻すので、エンジンの <c>using</c> を抜けた時点で
/// どこからも辿れなくなる（<see cref="TryRequestCancel"/> が掴む参照は呼び出しの間だけ）。
/// </para>
/// </remarks>
public sealed class RunningJobRegistry : IRunningJobRegistry
{
    private JobCancellation? _current;

    /// <summary>
    /// 実行中の Job として登録する。戻り値を破棄すると登録が外れる。
    /// </summary>
    /// <remarks>
    /// エンジン以外が登録することは無いので internal にしてある。
    /// 受け口（<see cref="CancellationTokenSource"/>）を外から受け取らずここで作るのは、
    /// 破棄する者を 1 人も作らないため（<see cref="JobCancellation"/> の注記）。
    /// </remarks>
    internal JobCancellation Track(JobId id)
    {
        JobCancellation cancellation = new(this, id);

        if (Interlocked.CompareExchange(ref _current, cancellation, null) is { } running)
        {
            // 載せられなかったものは呼び出し側へ渡らない（＝誰も破棄しない）ので、
            // ここで捨ててから投げる。using へ入る前に投げる唯一の経路がここ。
            cancellation.Dispose();

            // 同時実行数 1 の前提が破れている。黙って上書きすると
            // 先に動いている Job へキャンセルが届かなくなるので、ここで気づけるようにする。
            throw new InvalidOperationException(
                $"Job {running.Id} が実行中です。実行エンジンはプロセス内で 1 つだけ動かしてください。");
        }

        return cancellation;
    }

    /// <inheritdoc />
    public bool TryRequestCancel(JobId id)
    {
        while (true)
        {
            JobCancellation? current = Volatile.Read(ref _current);
            if (current is null || current.Id != id)
            {
                return false;
            }

            if (current.TryCancel())
            {
                return true;
            }

            // 掴んだ登録は退場済みだった。まだ同じ物が載っているなら、この Job は走っていない
            // ── 載せ替えを待つ理由が無いので false を返す。
            if (ReferenceEquals(Volatile.Read(ref _current), current))
            {
                return false;
            }

            // 載せ替わっている。同じ Job が走り直しているなら掴み直す。周回できるのは
            // 走り直しが起きた回数までで、走り直しには利用者の停止と再開が要る（空転しない）。
        }
    }

    /// <remarks>
    /// 条件付きで外す。自分が載っているときだけ null に戻すので、
    /// 既に次の実行が載っていたらそれを消してしまうことがない。
    /// </remarks>
    internal void Untrack(JobCancellation cancellation) =>
        Interlocked.CompareExchange(ref _current, null, cancellation);
}
