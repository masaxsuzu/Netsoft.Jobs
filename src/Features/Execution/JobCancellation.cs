using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 実行中の 1 件分のキャンセルの受け口。破棄すると
/// <see cref="RunningJobRegistry"/> からの登録が外れる。
/// </summary>
/// <remarks>
/// <para>
/// <b>破棄の時点を、持ち主の数で決める。</b>この受け口は 2 人が同時に触りうる
/// ── 実行を終えて退場するエンジンと、キャンセルを伝えにくる HTTP のスレッド。
/// 素直に「退場する側が破棄する」と決めると、掴んだ側が
/// <see cref="CancellationTokenSource.Cancel()"/> を呼ぶまでの間に破棄が割り込んで
/// <see cref="ObjectDisposedException"/> になりうるので、掴みと破棄を排他で隔てるしかない。
/// </para>
/// <para>
/// <b>最後に手を離した者が破棄する</b>形にすると、その窓が消える。数はエンジンの登録が
/// 持つ 1 から始まり、伝えにくる側は伝えている間だけ 1 つ借りる。0 にした者だけが破棄するので、
/// 誰かが触っている最中に消えることも、誰も破棄しないまま残ることもない。
/// </para>
/// <para>
/// <b>破棄しない形も検討したが採らなかった。</b>この受け口が抱える資源は待ち受けの登録だけで
/// （時計も待機ハンドルも持たない ── <see cref="CancellationToken.WaitHandle"/> を
/// 触っていないので確保もされない）、GC に任せても実害は無い。ただし「実害が無いから
/// 放置してよい」は、資源が増えた日に黙って崩れる約束になる。数で決める形なら、
/// 排他を使わずに破棄を保証できるので、放置する理由が無くなった。
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

    // 触っている者の数。1 はエンジンの登録が持っていて Dispose で返す。
    // 0 にした者が破棄する。0 を見た者はもう借りられない（＝破棄済み）。
    private int _users = 1;

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
    /// 伝えている間は 1 つ借りるので、その最中に破棄されることはない。勝った直後に退場が
    /// 起きても <c>Cancel()</c> は走り切り、破棄は手を離した後にこちらが行う。
    /// </para>
    /// </remarks>
    internal bool TryCancel()
    {
        if (!TryUse())
        {
            // 借りられない＝破棄済み＝この受け口はもう走っていない。
            return false;
        }

        try
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
        finally
        {
            Release();
        }
    }

    /// <summary>登録を外す。以後この受け口にキャンセルは届かない。</summary>
    /// <remarks>
    /// 2 度目は何もしない。手を離すのを 2 回数えると、まだ誰かが触っている最中に
    /// 破棄が起きる。
    /// </remarks>
    public void Dispose()
    {
        // 条件を付けずに退場させる。Cancelled から Retired への移りは
        // 「伝えたが、もう走っていない」で、次の要求に false を返すのが正しい。
        if (Interlocked.Exchange(ref _state, Retired) == Retired)
        {
            return;
        }

        _owner.Untrack(this);
        Release();
    }

    /// <summary>
    /// 触る権利を 1 つ借りる。破棄済み（0）なら借りられない。
    /// </summary>
    /// <remarks>
    /// 増やすだけでは足りない。0 を見てから増やすまでの間に破棄が挟まると、
    /// 破棄済みの物を借りたことになる。0 でないことを確かめた値ごと差し替える。
    /// </remarks>
    private bool TryUse()
    {
        while (true)
        {
            int users = Volatile.Read(ref _users);
            if (users == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _users, users + 1, users) == users)
            {
                return true;
            }
        }
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _users) == 0)
        {
            _cancellation.Dispose();
        }
    }
}
