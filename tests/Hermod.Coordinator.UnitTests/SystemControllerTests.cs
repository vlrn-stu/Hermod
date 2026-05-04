using System.Reflection;
using System.Security.Claims;
using Hermod.Coordinator.Controllers;
using Hermod.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hermod.Coordinator.UnitTests;

/// <summary>
/// Pins <see cref="SystemController.GetFeatures"/>: anonymous only when
/// <c>Dev.Endpoints</c> is on (matrix harness + dev loops), JWT-required
/// otherwise via the same fallback policy every other API uses. Also
/// pins the response shape matrix tooling parses.
/// </summary>
public class SystemControllerTests
{
    [Fact]
    public void SystemController_ClassHasNoClassLevelAllowAnonymous()
    {
        // Class-level [AllowAnonymous] would bypass the global fallback
        // [Authorize] policy unconditionally. That was the pre-fix state
        // and was the regression this test guards against. Pin the current shape: the class
        // carries no auth attributes, so the global RequireAuthenticatedUser
        // fallback kicks in; the action-level [AllowAnonymous] is what
        // punches through in dev mode only.
        var classAllow = typeof(SystemController)
            .GetCustomAttribute<AllowAnonymousAttribute>(inherit: false);
        Assert.Null(classAllow);

        var classAuthorize = typeof(SystemController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false);
        Assert.Null(classAuthorize);
    }

    [Fact]
    public void GetFeatures_HasActionLevelAllowAnonymous()
    {
        // The action carries [AllowAnonymous] so dev callers can reach
        // /api/system/features pre-JWT; the handler itself checks
        // Dev.Endpoints + User.IsAuthenticated to enforce the prod gate.
        var method = typeof(SystemController).GetMethod(nameof(SystemController.GetFeatures));
        Assert.NotNull(method);
        var allow = method.GetCustomAttribute<AllowAnonymousAttribute>(inherit: false);
        Assert.NotNull(allow);
    }

    [Fact]
    public void SystemController_RouteIsPinned()
    {
        var route = typeof(SystemController)
            .GetCustomAttribute<RouteAttribute>(inherit: false);
        Assert.NotNull(route);
        Assert.Equal("api/system", route.Template);
    }

    [Fact]
    public void GetFeatures_DevEndpointsOn_AllowsAnonymous()
    {
        var sut = BuildController(devEndpoints: true, authenticated: false);

        var result = sut.GetFeatures();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void GetFeatures_DevEndpointsOff_AnonymousCaller_Returns401()
    {
        // Prod posture: with Dev.Endpoints off, an unauthenticated
        // caller must get 401 even though the action is [AllowAnonymous]
        // for the routing stage.
        var sut = BuildController(devEndpoints: false, authenticated: false);

        var result = sut.GetFeatures();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public void GetFeatures_DevEndpointsOff_AuthenticatedCaller_Returns200()
    {
        // Prod posture with a valid JWT (simulated via an authenticated
        // ClaimsPrincipal): the handler lets the request through.
        var sut = BuildController(devEndpoints: false, authenticated: true);

        var result = sut.GetFeatures();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void GetFeatures_ReturnsMirroredSettings()
    {
        var settings = new HermodSettings
        {
            Features = new FeaturesSettings
            {
                DeviceStateTracking = false,
                MessagePersistence = true,
                RuleAuditLog = false,
                StatsRollup = true,
                RuleCache = false,
                MetricsEndpoint = true,
                UuidTrace = true,
            },
            Storage = new StorageSettings
            {
                Mode = StorageMode.Postgres,
                WriteBatchSize = 500,
                WriteFlushIntervalMs = 250,
                WriteQueueCapacity = 10_000,
                MaxPoolSize = 42,
                MinPoolSize = 7,
                CommandTimeoutSeconds = 15,
                KeepAliveSeconds = 11,
                MaxAutoPrepare = 13,
                SkipDeviceExistenceCheck = true,
                FastDeviceUpserts = false,
            },
            Engine = new EngineSettings
            {
                Parallelism = 4,
                BatchSize = 32,
                QueueCapacity = 50_000,
                LogBatching = true,
                LogBatchSize = 128,
                LogBatchIntervalMs = 500,
                RuleCacheRefreshSeconds = 30,
            },
            Mqtt = new MqttSettings { ReconnectBufferSize = 2048, ParallelClients = 4 },
            Seed = new SeedSettings { Devices = false, Rules = true },
            Dev = new DevSettings { Endpoints = true },
            Telemetry = new TelemetrySettings { TimestampsCsvPath = "/tmp/ts.csv", BufferCapacity = 123 },
        };
        var sut = BuildController(settings, authenticated: false);

        var result = sut.GetFeatures();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SystemFeaturesResponse>(ok.Value);

        Assert.False(body.Features.DeviceStateTracking);
        Assert.True(body.Features.MessagePersistence);
        Assert.True(body.Features.StatsRollup);
        Assert.True(body.Features.UuidTrace);

        Assert.Equal("Postgres", body.Storage.Mode);
        Assert.Equal(500, body.Storage.WriteBatchSize);
        Assert.Equal(42, body.Storage.MaxPoolSize);
        Assert.Equal(7, body.Storage.MinPoolSize);
        Assert.Equal(15, body.Storage.CommandTimeoutSeconds);
        Assert.Equal(11, body.Storage.KeepAliveSeconds);
        Assert.Equal(13, body.Storage.MaxAutoPrepare);
        Assert.True(body.Storage.SkipDeviceExistenceCheck);
        Assert.False(body.Storage.FastDeviceUpserts);

        Assert.Equal(4, body.Engine.Parallelism);
        Assert.Equal(30, body.Engine.RuleCacheRefreshSeconds);

        Assert.Equal(2048, body.Mqtt.ReconnectBufferSize);
        Assert.Equal(4, body.Mqtt.ParallelClients);

        Assert.False(body.Seed.Devices);
        Assert.True(body.Seed.Rules);
        Assert.True(body.Dev.Endpoints);

        Assert.Equal("/tmp/ts.csv", body.Telemetry.TimestampsCsvPath);
        Assert.Equal(123, body.Telemetry.BufferCapacity);
    }

    [Fact]
    public void GetFeatures_RuntimeUptimeIsNonNegativeAndMonotonicToNow()
    {
        var sut = BuildController(devEndpoints: true, authenticated: false);

        var result = sut.GetFeatures();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SystemFeaturesResponse>(ok.Value);

        Assert.True(body.Runtime.UptimeSeconds >= 0);
        Assert.True(body.Runtime.Now >= body.Runtime.StartedAt);
    }

    [Fact]
    public void GetFeatures_BuildView_FallsBackToUnknownWhenEnvVarsUnset()
    {
        var originalSha = Environment.GetEnvironmentVariable("HERMOD_GIT_SHA");
        var originalDigest = Environment.GetEnvironmentVariable("HERMOD_IMAGE_DIGEST");
        Environment.SetEnvironmentVariable("HERMOD_GIT_SHA", null);
        Environment.SetEnvironmentVariable("HERMOD_IMAGE_DIGEST", null);
        try
        {
            var sut = BuildController(devEndpoints: true, authenticated: false);
            var ok = Assert.IsType<OkObjectResult>(sut.GetFeatures());
            var body = Assert.IsType<SystemFeaturesResponse>(ok.Value);

            Assert.Equal("unknown", body.Build.GitSha);
            Assert.Equal("unknown", body.Build.ImageDigest);
            Assert.NotNull(body.Build.AssemblyVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HERMOD_GIT_SHA", originalSha);
            Environment.SetEnvironmentVariable("HERMOD_IMAGE_DIGEST", originalDigest);
        }
    }

    [Fact]
    public void GetFeatures_BuildView_UsesEnvVarsWhenSet()
    {
        var originalSha = Environment.GetEnvironmentVariable("HERMOD_GIT_SHA");
        var originalDigest = Environment.GetEnvironmentVariable("HERMOD_IMAGE_DIGEST");
        Environment.SetEnvironmentVariable("HERMOD_GIT_SHA", "deadbeef");
        Environment.SetEnvironmentVariable("HERMOD_IMAGE_DIGEST", "sha256:abc123");
        try
        {
            var sut = BuildController(devEndpoints: true, authenticated: false);
            var ok = Assert.IsType<OkObjectResult>(sut.GetFeatures());
            var body = Assert.IsType<SystemFeaturesResponse>(ok.Value);

            Assert.Equal("deadbeef", body.Build.GitSha);
            Assert.Equal("sha256:abc123", body.Build.ImageDigest);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HERMOD_GIT_SHA", originalSha);
            Environment.SetEnvironmentVariable("HERMOD_IMAGE_DIGEST", originalDigest);
        }
    }

    private static SystemController BuildController(bool devEndpoints, bool authenticated) =>
        BuildController(new HermodSettings { Dev = new DevSettings { Endpoints = devEndpoints } }, authenticated);

    private static SystemController BuildController(HermodSettings settings, bool authenticated)
    {
        var ctrl = new SystemController(Options.Create(settings));
        var identity = authenticated
            ? new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "test-user") }, "TestScheme")
            : new ClaimsIdentity();
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return ctrl;
    }
}
