using Hermod.Core.Telemetry;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the Prometheus exposition surface of <see cref="HermodMetrics"/>.
/// The /metrics scrape is the only knob Grafana sees, so regressions in
/// counter names or missing lines silently break alerts.
/// </summary>
public class HermodMetricsTests
{
    [Fact]
    public void IncMqttReconnects_RendersInPrometheusOutput()
    {
        var metrics = new HermodMetrics();

        metrics.IncMqttReconnects();
        metrics.IncMqttReconnects();
        metrics.IncMqttReconnects();

        var render = metrics.Render();

        Assert.Contains("# TYPE hermod_mqtt_reconnects_total counter", render, StringComparison.Ordinal);
        Assert.Contains("hermod_mqtt_reconnects_total 3\n", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_UntouchedCounter_IsZero()
    {
        var metrics = new HermodMetrics();

        var render = metrics.Render();

        Assert.Contains("hermod_mqtt_reconnects_total 0\n", render, StringComparison.Ordinal);
    }
}
