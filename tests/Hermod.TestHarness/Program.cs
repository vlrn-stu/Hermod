using Hermod.TestHarness;
using Hermod.TestHarness.Runners;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<MqttTestClient>();
builder.Services.AddSingleton<MockPublisher>();
builder.Services.AddSingleton<LoRaTestRunner>();
builder.Services.AddSingleton<FunctionalTestRunner>();
builder.Services.AddSingleton<SecurityTestRunner>();
builder.Services.AddSingleton<PerformanceTestRunner>();
builder.Services.AddSingleton<HttpE2ETestRunner>();
builder.Services.AddSingleton<AuthAttackTestRunner>();
builder.Services.AddSingleton<ResilienceTestRunner>();
builder.Services.AddSingleton<MeasurementCollector>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var collector = host.Services.GetRequiredService<MeasurementCollector>();

var mode = args.FirstOrDefault() ?? "--functional";

logger.LogInformation("Hermod Test Harness starting in {Mode} mode", mode);

// Methodology rule: credentials from env, fail fast with a diagnostic if
// missing. The old fallback password is banned. --performance requires
// auth because it seeds + tears down the harness-perf-fwd rule via the API.
var requiresAuth = mode is "--e2e" or "--auth-attack" or "--security" or "--performance" or "--all";
if (requiresAuth && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HERMOD_ADMIN_PASSWORD")))
{
    collector.Record(new TestResult
    {
        Category = "Harness",
        Claim = "N/A",
        Name = "Environment_Preflight",
        Status = "ERROR",
        Details = "HERMOD_ADMIN_PASSWORD is not set. Set it before running " +
                  "any runner that exercises authenticated endpoints."
    });
    await collector.SaveResultsAsync();
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var exitCode = 0;

try
{
    switch (mode)
    {
        case "--functional":
            await host.Services.GetRequiredService<FunctionalTestRunner>().RunAsync(cts.Token);
            break;

        case "--security":
            await host.Services.GetRequiredService<SecurityTestRunner>().RunAsync(cts.Token);
            break;

        case "--performance":
            await host.Services.GetRequiredService<PerformanceTestRunner>().RunAsync(cts.Token);
            break;

        case "--e2e":
            await host.Services.GetRequiredService<HttpE2ETestRunner>().RunAsync(cts.Token);
            break;

        case "--auth-attack":
            await host.Services.GetRequiredService<AuthAttackTestRunner>().RunAsync(cts.Token);
            break;

        case "--lora":
            await host.Services.GetRequiredService<LoRaTestRunner>().RunAsync(cts.Token);
            break;

        case "--resilience":
            await host.Services.GetRequiredService<ResilienceTestRunner>().RunAsync(cts.Token);
            break;

        case "--all":
            // Ordered execution with graceful per-runner isolation: one
            // runner throwing must not skip the remaining runners' result
            // flush. Exceptions are captured in the collector.
            await RunSafely("HttpE2E", () => host.Services.GetRequiredService<HttpE2ETestRunner>().RunAsync(cts.Token));
            await RunSafely("AuthAttack", () => host.Services.GetRequiredService<AuthAttackTestRunner>().RunAsync(cts.Token));
            await RunSafely("Functional", () => host.Services.GetRequiredService<FunctionalTestRunner>().RunAsync(cts.Token));
            await RunSafely("Security", () => host.Services.GetRequiredService<SecurityTestRunner>().RunAsync(cts.Token));
            await RunSafely("Performance", () => host.Services.GetRequiredService<PerformanceTestRunner>().RunAsync(cts.Token));
            await RunSafely("Resilience", () => host.Services.GetRequiredService<ResilienceTestRunner>().RunAsync(cts.Token));
            break;

        default:
            logger.LogError("Unknown mode: {Mode}. Use --functional, --security, --performance, " +
                            "--e2e, --auth-attack, --lora, --resilience, or --all", mode);
            exitCode = 1;
            break;
    }
}
catch (OperationCanceledException)
{
    logger.LogWarning("Test run cancelled");
    exitCode = 130;
}
catch (Exception ex)
{
    logger.LogError(ex, "Test run failed with unexpected exception");
    collector.Record(new TestResult
    {
        Category = "Harness",
        Claim = "N/A",
        Name = "Unhandled_Exception",
        Status = "ERROR",
        Details = $"{ex.GetType().Name}: {ex.Message}"
    });
    exitCode = 1;
}
finally
{
    // ALWAYS save results. The previous version only saved on two specific
    // exit paths and lost partial results on any other exception.
    try
    {
        await collector.SaveResultsAsync();
        logger.LogInformation("Test run complete. {Count} results saved.", collector.Count);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to save results");
    }
}

return exitCode;

async Task RunSafely(string runnerName, Func<Task> runner)
{
    try
    {
        await runner();
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        collector.Record(new TestResult
        {
            Category = runnerName,
            Claim = "N/A",
            Name = $"{runnerName}_RunnerCrashed",
            Status = "ERROR",
            Details = $"{ex.GetType().Name}: {ex.Message}"
        });
    }
}
