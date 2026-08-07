using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 実行中の 1 件分のキャンセルの受け口。破棄すると
/// <see cref="RunningJobRegistry"/> からの登録が外れる。
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="CancellationTokenSource"/> を破棄しない。</b>これがこの型の要である。
/// 破棄する者が居ると、掴んだ側が <see cref="CancellationTokenSource.Cancel()"/> を
/// 呼ぶまでの間に破棄が割り込んで <see cref="ObjectDisposedException"/> になりうるので、
/// 掴みと破棄を排他で隔てるしかなくなる。破棄しなければその窓は<b>存在しない</b>。
/// </para>
/// <para>
/// 払うものはほぼ無い。この受け口が抱える資源は待ち受けの登録だけで
/// （時計も待機ハンドルも持たない ── <see cref="CancellationToken.WaitHandle"/> を
/// 触っていないので確保もされない）、実行が終われば登録も残らず、あとは GC が回収する。
/// <b>破棄で解放されるものが無い側と、破棄のために排他が要る側を比べた結果</b>である。
/// </para>
/// <para>
/// 状態を 3 つ持つのは、「届いた」と「もう走っていない」を呼び出し側へ正しく返すため。
/// <see cref="TryCancel"/> と <see cref="Dispose"/> は同じ状態を奪い合い、
/// 勝った側だけが進むので、退場した受け口へキャンセルが届いたと答えることがない。
/// </para>
/// </remarks>
public sealed class JobCancellation : IDisposable
{
    private const int Live = 0;
    private const int Cancelled = 1;
    private const int Retired = 2;

    private readonly RunningJobRegistry _owner;
    private readonly CancellationTokenSource _cancellation = new();

    private int _state;

    internal JobCancellation(RunningJobRegistry owner, JobId id)
    {
        _owner = owner;
        Id = id;
    }

    /// <summary>この受け口が受け持っている Job。</summary>
    internal JobId Id { get; }

    /// <summary>ハンドラへ渡すトークン。</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>利用者のキャンセルで発火したか。</summary>
    /// <remarks>
    /// エンジンはこれで「自分が渡したトークンで終わったか」を見分ける。
    /// 別のトークンでの中断は利用者の意図ではないので失敗として扱う。
    /// </remarks>
    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    /// <summary>
    /// キャンセルを伝える。伝わったなら（既に伝わっていた場合を含め）true。
    /// </summary>
    /// <remarks>
    /// 退場済みなら false。退場と競っても、状態の差し替えに勝った側だけが
    /// <see cref="CancellationTokenSource.Cancel()"/> へ進むので、答えが二重にならない。
    /// <para>
    /// 勝った直後に退場が起きても <c>Cancel()</c> は安全に走り切る。破棄する者が居ないので、
    /// この受け口は退場しても生きたまま残る。
    /// </para>
    /// </remarks>
    internal bool TryCancel()
    {
        int previous = Interlocked.CompareExchange(ref _state, Cancelled, Live);
        if (previous == Live)
        {
            _cancellation.Cancel();
            return true;
        }

        // 既に伝えてある。2 度目の要求も「届いている」が正しい答えになる。
        return previous == Cancelled;
    }

    /// <summary>登録を外す。以後この受け口にキャンセルは届かない。</summary>
    public void Dispose()
    {
        // 条件を付けずに退場させる。Cancelled から Retired への移りは
        // 「伝えたが、もう走っていない」で、次の要求に false を返すのが正しい。
        Volatile.Write(ref _state, Retired);

        _owner.Untrack(this);
    }
}
