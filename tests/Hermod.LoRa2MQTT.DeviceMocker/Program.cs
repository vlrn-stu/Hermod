using Hermod.LoRa2MQTT.DeviceMocker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "MOCKER_");
if (args.Length > 0)
{
    builder.Configuration.AddCommandLine(args);
}

builder.Services.Configure<MockerOptions>(builder.Configuration.GetSection(MockerOptions.SectionName));
builder.Services.Configure<MockerOptions>(builder.Configuration);

builder.Services.AddSingleton<SerialLoRaSender>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<MockerOptions>>().Value;
    return new SerialLoRaSender(
        sp.GetRequiredService<ILogger<SerialLoRaSender>>(),
        opts.SerialPort,
        opts.BaudRate,
        opts.AppendLf);
});

builder.Services.AddSingleton<TrafficGenerator>(sp => new TrafficGenerator(
    sp.GetRequiredService<ILogger<TrafficGenerator>>(),
    sp.GetRequiredService<SerialLoRaSender>(),
    sp.GetRequiredService<IOptions<MockerOptions>>().Value));

builder.Services.AddHostedService<MockerWorker>();

await builder.Build().RunAsync();

namespace Hermod.LoRa2MQTT.DeviceMocker
{
    internal sealed class MockerWorker(
        ILogger<MockerWorker> logger,
        SerialLoRaSender sender,
        TrafficGenerator generator,
        IHostApplicationLifetime lifetime) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                sender.Open();
                await generator.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "mocker worker crashed");
            }
            finally
            {
                sender.Dispose();
                lifetime.StopApplication();
            }
        }
    }
}
