using System.Globalization;

namespace Netsoft.Jobs.Infrastructure;

/// <summary>
/// SQLite に日時を書き出す・読み戻すときの表現を一箇所に固定する。
/// </summary>
/// <remarks>
/// SQLite に日時型は無く、入れたものがそのまま TEXT / INTEGER / REAL として残るだけ。
/// 表現を各クエリで決めていると、桁数やオフセットの揺れで文字列比較としての
/// ソートと範囲比較が静かに壊れる。そのため次の 2 点をここで固定する。
///
/// 1. 必ず UTC へ揃えてから書く。オフセット付きのまま書くと "09:00+09:00" と
///    "00:00+00:00" が同じ瞬間なのに文字列としては別物になり、大小関係が逆転する。
/// 2. 常に固定長のラウンドトリップ書式 ("O") を使う。桁数が揃っていれば
///    辞書順が時系列順と一致するので、ORDER BY をそのまま使える。
///
/// 元のオフセットは保存しない。<see cref="DateTimeOffset"/> の等価性は瞬間で決まるため、
/// 往復しても値は一致する。オフセットは「どこで記録されたか」の情報でしかなく、
/// Job の意味には関わらないので捨てる。
/// </remarks>
internal static class SqliteTimestamp
{
    /// <summary>UTC の ISO 8601 文字列へ変換する。</summary>
    public static string ToText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>UTC の ISO 8601 文字列から復元する。</summary>
    public static DateTimeOffset FromText(string text) =>
        DateTimeOffset.ParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
