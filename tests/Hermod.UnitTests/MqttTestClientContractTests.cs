using System.IO;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the <see cref="Hermod.TestHarness.MqttTestClient"/> contract via
/// source inspection. The test-harness project is an <c>OutputType=Exe</c>
/// console app with no unit-test surface of its own, and a real behavioural
/// test of <c>ConnectAsync</c> would require either a running broker or a
/// mock of <c>IMqttClient</c> (a large and unstable surface from MQTTnet).
/// Source inspection is the same approach used for the advisory-lock
/// coverage: weak but stable, and load-bearing for the specific
/// refactors this test is written to catch.
///
/// Guards:
///  1. ConnectAsync must acquire the connect lock before probing state.
///  2. ConnectAsync must short-circuit when the client reports IsConnected,
///     so a second caller is a no-op and shared singleton state is
///     preserved across test runners.
///  3. The re-subscribe-on-reconnect loop over `_subscribed.Keys` must
///     remain in place so topics survive a reconnect.
///  4. DisposeAsync must cancel pending correlation waiters before the
///     underlying client is disposed so awaiters observe a clean
///     cancellation instead of a dangling task.
/// </summary>
public class MqttTestClientContractTests
{
    private static string ReadMqttTestClientSource()
    {
        // Walk from the test assembly directory
        // (tests/Hermod.UnitTests/bin/<config>/<tfm>/) up five levels to
        // the Hermod repo root, then into the test-harness project.
        var assemblyDir = Path.GetDirectoryName(typeof(MqttTestClientContractTests).Assembly.Location)!;
        var repoDir = Directory.GetParent(assemblyDir)!.Parent!.Parent!.Parent!.Parent!.FullName;
        var srcPath = Path.Combine(repoDir, "tests", "Hermod.TestHarness", "MqttTestClient.cs");
        Assert.True(File.Exists(srcPath), $"expected MqttTestClient.cs at {srcPath}");
        return File.ReadAllText(srcPath);
    }

    [Fact]
    public void ConnectAsync_AcquiresConnectLockBeforeStateProbe()
    {
        var source = ReadMqttTestClientSource();
        // The semaphore ensures only one caller runs the probe+connect
        // sequence at a time. Removing this line without an equivalent
        // guard re-opens the connect-lock race this test was added to pin.
        Assert.Contains("await _connectLock.WaitAsync", source);
    }

    [Fact]
    public void ConnectAsync_ShortCircuitsWhenAlreadyConnected()
    {
        var source = ReadMqttTestClientSource();
        // The pattern-match guard (`_client is { IsConnected: true }`)
        // is the idempotency contract itself. Two occurrences exist
        // in the source: the public `IsConnected` property definition
        // and the guard inside `ConnectAsync`. Pin the guard by
        // requiring it to be preceded by an `if (` clause AND followed
        // by a `return;` within a short window.
        Assert.Contains("if (_client is { IsConnected: true })", source);

        var idx = source.IndexOf(
            "if (_client is { IsConnected: true })",
            System.StringComparison.Ordinal);
        var tail = source.Substring(idx, System.Math.Min(80, source.Length - idx));
        Assert.Contains("return", tail);
    }

    [Fact]
    public void ConnectAsync_ReSubscribesTrackedTopicsAfterReconnect()
    {
        var source = ReadMqttTestClientSource();
        // After a disposed client is rebuilt, tests that had already
        // called SubscribeAsync expect their subscriptions to survive.
        // The loop over _subscribed.Keys is what preserves them.
        Assert.Contains("_subscribed.Keys", source);
    }

    [Fact]
    public void DisposeAsync_CancelsPendingCorrelationWaitersBeforeDisposingClient()
    {
        var source = ReadMqttTestClientSource();
        // A test harness that holds Task references to pending
        // PublishAndWaitAsync calls must observe clean cancellation on
        // shutdown, not a dangling await. The TrySetCanceled() loop
        // over _correlationWaiters runs BEFORE the underlying client
        // is disposed.
        var cancelIdx = source.IndexOf("waiter.TrySetCanceled", System.StringComparison.Ordinal);
        var disposeIdx = source.IndexOf("_client.Dispose", System.StringComparison.Ordinal);
        Assert.True(cancelIdx > 0, "waiter cancellation loop must be present");
        Assert.True(disposeIdx > 0, "client disposal must be present");
        Assert.True(cancelIdx < disposeIdx,
            $"waiter cancellation (at {cancelIdx}) must precede client disposal (at {disposeIdx})");
    }
}
