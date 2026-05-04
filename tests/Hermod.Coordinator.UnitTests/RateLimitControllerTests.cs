using System.Reflection;
using Hermod.Coordinator.Authorization;
using Hermod.Coordinator.Controllers;
using Hermod.Core.Configuration;
using Hermod.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hermod.Coordinator.UnitTests;

/// <summary>
/// Pins <see cref="RateLimitController"/>: routes correctly, requires the
/// <c>operator</c> policy at the class level (so viewers cannot mutate
/// runtime overrides), and the upsert / delete handlers persist into the
/// in-memory store.
/// </summary>
public class RateLimitControllerTests
{
    [Fact]
    public void Controller_RequiresOperatorPolicy()
    {
        var attr = typeof(RateLimitController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false);
        Assert.NotNull(attr);
        Assert.Equal(Policies.Operator, attr.Policy);
    }

    [Fact]
    public void Controller_RouteIsPinned()
    {
        var route = typeof(RateLimitController)
            .GetCustomAttribute<RouteAttribute>(inherit: false);
        Assert.NotNull(route);
        Assert.Equal("api/system/rate-limits", route.Template);
    }

    [Fact]
    public async Task Get_ReturnsStaticAndRuntimeOverrides()
    {
        var (controller, store) = Build();
        await store.SetAsync("lora/sensor-1", new TopicRateOverride { RatePerSecond = 0.5, Burst = 2, DedupWindowSeconds = 30 });

        var result = Assert.IsType<OkObjectResult>(controller.Get());
        var view = Assert.IsType<RateLimitView>(result.Value);

        Assert.True(view.Enabled);
        Assert.Single(view.RuntimeOverrides);
        Assert.Equal(0.5, view.RuntimeOverrides["lora/sensor-1"].RatePerSecond);
        Assert.Single(view.StaticOverrides);
        Assert.Equal("zigbee/spammy", view.StaticOverrides.Keys.First());
    }

    [Fact]
    public async Task Upsert_StoresOverride_AndReturnsIt()
    {
        var (controller, store) = Build();
        var body = new TopicRateOverrideView(RatePerSecond: 2.0, Burst: 5, DedupWindowSeconds: 10);

        var result = Assert.IsType<OkObjectResult>(await controller.Upsert("wifi/lamp-1", body, CancellationToken.None));
        var stored = Assert.IsType<TopicRateOverrideView>(result.Value);
        Assert.Equal(2.0, stored.RatePerSecond);

        var fromStore = store.TryGet("wifi/lamp-1");
        Assert.NotNull(fromStore);
        Assert.Equal(5, fromStore.Burst);
    }

    [Fact]
    public async Task Upsert_RejectsNegativeValues()
    {
        var (controller, _) = Build();
        var bad = new TopicRateOverrideView(RatePerSecond: -1, Burst: 5, DedupWindowSeconds: 0);
        Assert.IsType<BadRequestObjectResult>(await controller.Upsert("lora/x", bad, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_RemovesOverride_OrReturnsNotFound()
    {
        var (controller, store) = Build();
        await store.SetAsync("lora/x", new TopicRateOverride { RatePerSecond = 1, Burst = 1 });

        Assert.IsType<NoContentResult>(await controller.Delete("lora/x", CancellationToken.None));
        Assert.Null(store.TryGet("lora/x"));

        Assert.IsType<NotFoundResult>(await controller.Delete("lora/x", CancellationToken.None));
    }

    private static (RateLimitController controller, IRateLimitOverridesStore store) Build()
    {
        var settings = new HermodSettings
        {
            RateLimit = new RateLimitSettings
            {
                Enabled = true,
                DefaultRatePerSecond = 1.0,
                DefaultBurst = 10,
                DedupWindowSeconds = 5,
                MaxTrackedKeys = 4096,
                TopicOverrides = new Dictionary<string, TopicRateOverride>
                {
                    ["zigbee/spammy"] = new() { RatePerSecond = 0.1, Burst = 1 },
                },
            },
        };
        var monitor = new SystemControllerTestsHelpers.StaticMonitor<HermodSettings>(settings);
        var store = new RateLimitOverridesStore();
        return (new RateLimitController(store, monitor), store);
    }
}

internal static class SystemControllerTestsHelpers
{
    /// <summary>
    /// Minimal <see cref="IOptionsMonitor{T}"/> that returns a fixed value.
    /// Mirrors the helper used by <c>TopicIngressLimiterTests</c> but lives
    /// here so the Coordinator suite has no dependency on Hermod.UnitTests.
    /// </summary>
    public sealed class StaticMonitor<T> : IOptionsMonitor<T>
    {
        public StaticMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
