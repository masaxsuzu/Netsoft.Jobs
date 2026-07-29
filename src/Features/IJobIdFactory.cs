using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features;

/// <summary>
/// <see cref="JobId"/> を採番する。
/// </summary>
/// <remarks>
/// Domain は「採番は自分の責務ではない」としているので、採番の口はこちら側に置く。
/// インターフェースにしてあるのは、テストで採番を固定して結果を検証できるようにするため。
/// </remarks>
public interface IJobIdFactory
{
    /// <summary>新しい識別子を払い出す。</summary>
    JobId Create();
}

/// <summary>
/// UUID v7 で採番する既定の実装。
/// </summary>
/// <remarks>
/// v7 は先頭に時刻を含むので、生成順と辞書順が一致する。
/// 主キーのインデックスが末尾に追記され続けて断片化しにくく、
/// 作成日時が同じ Job を Id で第 2 ソートしたときの並びも安定する。v4 にはどちらの性質も無い。
/// 文字列化に "d"（小文字の 16 進）を使うのは、大文字だと ASCII 上で数字・英大文字・英小文字が
/// 混ざり、文字列比較の順序が値の順序とずれるため。
/// </remarks>
public sealed class GuidV7JobIdFactory : IJobIdFactory
{
    /// <inheritdoc />
    public JobId Create() => JobId.From(Guid.CreateVersion7().ToString("d"));
}
