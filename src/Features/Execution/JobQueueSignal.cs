using System.Threading.Channels;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 「実行できる Job が増えたかもしれない」という合図。書き込み側が <see cref="Set"/> で鳴らし、
/// 実行エンジンが <see cref="WaitAsync"/> で待つ。アイドルポーリングの置き換え。
/// </summary>
/// <remarks>
/// <para>
/// 実体は容量 1 の Channel。合図は「仕事があるかもしれない」以上の情報を持たないので、
/// 2 つ溜めても 1 つと同じ意味にしかならず、あふれた分は捨ててよい
/// （SSE の接続ごとに同じ箱を置く JobEventsEndpoint と同じ判断）。
/// </para>
/// <para>
/// ネットワーク越えの push（SSE）と違い、プロセス内の Channel は best-effort ではない。
/// <see cref="Set"/> が <see cref="WaitAsync"/> の開始より先でもトークンが箱に残るため、
/// 後から始めた待ちは即座に返る。「確認してから待ちに入るまでの間に書き込まれた」を
/// 取りこぼす窓が無い。エンジンが安全網のポーリング無しで合図だけに頼れるのはこの性質による。
/// </para>
/// </remarks>
public sealed class JobQueueSignal
{
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    /// <summary>
    /// 合図を鳴らす。既に鳴っていれば何もしない（意味が変わらないため）。
    /// </summary>
    /// <remarks>
    /// TryWrite は満杯でも待たずに false を返すだけ。発火元は store の書き込み経路なので、
    /// エンジンの消費を書き込みが待つ形にしてはいけない。
    /// </remarks>
    public void Set() => _signals.Writer.TryWrite(0);

    /// <summary>
    /// 合図が鳴るまで待ち、1 つ消費する。既に鳴っていれば即座に返る。
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        // 値は見ない。合図は「変更があった」以上の情報を運ばない契約。
        _ = await _signals.Reader.ReadAsync(cancellationToken);
    }
}
