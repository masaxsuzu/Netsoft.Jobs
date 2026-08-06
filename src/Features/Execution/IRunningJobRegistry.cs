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
/// 実行中の Job とその <see cref="CancellationTokenSource"/> を保持する既定の実装。
/// </summary>
/// <remarks>
/// 同時実行数が 1 なので、保持するのは常に高々 1 件。辞書にしていないのはそのため。
/// 並列化するときはここを辞書に変える（<see cref="JobExecutionEngine"/> の注記を参照）。
/// 書き込むのはエンジンのループ、読むのは HTTP や画面のスレッドなので、
/// ここだけは排他が要る。
/// </remarks>
public sealed class RunningJobRegistry : IRunningJobRegistry
{
    private readonly Lock _gate = new();

    private JobId _id;
    private CancellationTokenSource? _cancellation;

    /// <summary>
    /// 実行中の Job として登録する。戻り値を破棄すると登録が外れる。
    /// </summary>
    /// <remarks>
    /// エンジン以外が登録することは無いので internal にしてある。
    /// </remarks>
    internal IDisposable Track(JobId id, CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);

        lock (_gate)
        {
            if (_cancellation is not null)
            {
                // 同時実行数 1 の前提が破れている。黙って上書きすると
                // 先に動いている Job へキャンセルが届かなくなるので、ここで気づけるようにする。
                throw new InvalidOperationException(
                    $"Job {_id} が実行中です。実行エンジンはプロセス内で 1 つだけ動かしてください。");
            }

            _id = id;
            _cancellation = cancellation;
        }

        return new Registration(this, id);
    }

    /// <inheritdoc />
    public bool TryRequestCancel(JobId id)
    {
        // Cancel をロックの中で呼ぶ。外に出すと、掴んだ直後にエンジンが実行を終えて
        // Dispose した場合に ObjectDisposedException になりうる。
        // ハンドラの継続はこのロックを取らないので、これで詰まることはない。
        lock (_gate)
        {
            if (_cancellation is null || _id != id)
            {
                return false;
            }

            _cancellation.Cancel();
            return true;
        }
    }

    private void Untrack(JobId id)
    {
        lock (_gate)
        {
            if (_id != id)
            {
                return;
            }

            _id = default;
            _cancellation = null;
        }
    }

    private sealed class Registration(RunningJobRegistry owner, JobId id) : IDisposable
    {
        public void Dispose() => owner.Untrack(id);
    }
}
