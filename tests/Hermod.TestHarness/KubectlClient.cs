using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Hermod.TestHarness;

/// <summary>
/// Thin Process-based kubectl wrapper used by ResilienceTestRunner.
///
/// Reads kubectl from PATH; when the harness runs inside the cluster the
/// binary must be present in the image and the pod's ServiceAccount must
/// carry RBAC for delete pods, rollout restart, and create/delete
/// networkpolicies in the hermod namespace. The orchestration script
/// satisfies both prerequisites: the harness Dockerfile installs kubectl
/// and the RBAC manifest grants the required verbs.
///
/// Errors are captured as KubectlResult rather than thrown so the runner can
/// record a clean ERROR row instead of a runner-level crash.
/// </summary>
public sealed class KubectlClient
{
    private readonly ILogger _logger;
    private readonly string _ns;
    private readonly string _binary;

    public KubectlClient(ILogger logger, string @namespace = "hermod", string binary = "kubectl")
    {
        _logger = logger;
        _ns = @namespace;
        _binary = binary;
    }

    public Task<KubectlResult> RunAsync(string args, CancellationToken ct, TimeSpan? timeout = null)
        => RunRawAsync($"-n {_ns} {args}", ct, timeout);

    public Task<KubectlResult> RunClusterScopedAsync(string args, CancellationToken ct, TimeSpan? timeout = null)
        => RunRawAsync(args, ct, timeout);

    private async Task<KubectlResult> RunRawAsync(string args, CancellationToken ct, TimeSpan? timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _binary,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start {_binary}");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout is not null) cts.CancelAfter(timeout.Value);

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new KubectlResult(-1, "", $"timed out or cancelled: {_binary} {args}");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new KubectlResult(proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new KubectlResult(-1, "", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

public sealed record KubectlResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
    public string Summary => Ok ? Stdout.Trim() : $"exit={ExitCode}; stderr={Truncate(Stderr, 240)}";
    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";
}
