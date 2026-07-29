namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 常に同じ時刻を返す <see cref="TimeProvider"/>。時刻を進めたいときは <see cref="UtcNow"/> を書き換える。
/// </summary>
/// <remarks>
/// BCL の <see cref="TimeProvider"/> を継承するだけで足りるので、独自の時計インターフェースは作らない。
/// 追加のテスト用パッケージも要らない。
/// </remarks>
public sealed class FixedTimeProvider : TimeProvider
{
    public FixedTimeProvider(DateTimeOffset utcNow) => UtcNow = utcNow;

    /// <summary>この時計が返す時刻。</summary>
    public DateTimeOffset UtcNow { get; set; }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => UtcNow;
}
