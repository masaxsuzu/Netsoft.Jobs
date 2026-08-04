using System.Diagnostics.Metrics;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// テストクラス 1 つに閉じた <see cref="IMeterFactory"/>。
/// </summary>
/// <remarks>
/// MetricCollector は「どの factory が作った Meter か」（<see cref="Meter.Scope"/>）で
/// 購読対象を絞る。テストごとに factory を分ければ、並行して走る他のテストの
/// 同名 Meter の計測が混ざらない。DI で AddMetrics を組むより速く、後始末も自前で閉じる。
/// </remarks>
public sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    /// <inheritdoc />
    public Meter Create(MeterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // MetricCollector(IMeterFactory, ...) は Meter.Scope == factory で購読対象を判定する。
        options.Scope = this;

        Meter meter = new(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (Meter meter in _meters)
        {
            meter.Dispose();
        }
    }
}
