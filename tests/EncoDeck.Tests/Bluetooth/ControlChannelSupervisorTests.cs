// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Control;
using EncoDeck.Core.Connection;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using EncoDeck.Tests.Protocol;

namespace EncoDeck.Tests.Bluetooth;

public class ControlChannelSupervisorTests
{
    private static readonly DeviceIdentity X4 = new("dev-x4", "OPPO Enco X4", new BluetoothAddress(0x112233445566));
    private static readonly ReconnectPolicy FastPolicy = new(TimeSpan.FromMilliseconds(10), 2, TimeSpan.FromMilliseconds(40), 3);

    private sealed class FakeLink(bool connected) : ILinkStateSource
    {
        public bool IsConnected { get; private set; } = connected;

        public event EventHandler<bool>? ConnectionChanged;

        public void Set(bool connected)
        {
            IsConnected = connected;
            ConnectionChanged?.Invoke(this, connected);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeConnector : IChannelConnector
    {
        public int Attempts;
        public Action? OnOpen { get; set; }
        public Task? BeforeOpen { get; set; }
        public ChannelOpenStatus? FailWith { get; set; }
        public FakeOppoDevice? LastDevice { get; private set; }

        public async Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            if (BeforeOpen is { } pending) await pending.WaitAsync(cancellationToken);
            OnOpen?.Invoke();
            if (FailWith is { } status) return ChannelOpenResult.Failed(status, "fake failure");
            LastDevice = new FakeOppoDevice();
            return new ChannelOpenResult(ChannelOpenStatus.Opened, LastDevice.Channel, serviceUuids[0], null);
        }
    }

    private static (ControlChannelSupervisor Supervisor, DeviceStateStore Store) Create(FakeLink link, FakeConnector connector, TimeSpan? connectionTimeout = null)
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = X4 });
        var supervisor = new ControlChannelSupervisor(X4, new OppoDeviceProfile(OppoModels.EncoX4), link, connector, store, FastPolicy, linkSettleDelay: TimeSpan.FromMilliseconds(20), connectionTimeout: connectionTimeout);
        return (supervisor, store);
    }

    private static Task WaitFor(DeviceStateStore store, DeviceConnectionState state, int timeoutMs = 3000) =>
        OppoDeviceControllerTests.WaitUntil(() => store.Current.Connection == state, timeoutMs);

    [Fact]
    public async Task LinkAlreadyUp_ConnectsToReady()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);
        Assert.NotNull(supervisor.Controller);
        Assert.Equal(1, connector.Attempts);
        Assert.Equal(100, store.Current.Battery.Left!.Value.Level);
    }

    [Fact]
    public async Task LinkDown_WaitsWithoutConnecting()
    {
        var link = new FakeLink(false);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.PairedNotConnected);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, connector.Attempts);
    }

    [Fact]
    public async Task ManualConnect_AttemptsWhileDisconnected_AndWaitsForHandshake()
    {
        var link = new FakeLink(false);
        var connector = new FakeConnector { OnOpen = () => link.Set(true) };
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.PairedNotConnected);
        var result = await supervisor.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal(DeviceConnectionState.ControlReady, store.Current.Connection);
        Assert.Equal(1, connector.Attempts);
        Assert.NotNull(supervisor.Controller);
    }

    [Fact]
    public async Task ManualConnect_WhenUnreachable_FailsWithoutBackgroundRetryLoop()
    {
        var link = new FakeLink(false);
        var connector = new FakeConnector { FailWith = ChannelOpenStatus.DeviceUnreachable };
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.PairedNotConnected);
        var result = await supervisor.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        await WaitFor(store, DeviceConnectionState.PairedNotConnected);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, connector.Attempts);
        await supervisor.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, connector.Attempts);
    }

    [Fact]
    public async Task ManualConnect_WhenAlreadyReady_DoesNotOpenAnotherChannel()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);
        Assert.True((await supervisor.ConnectAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(1, connector.Attempts);
    }

    [Fact]
    public async Task ManualConnect_ConcurrentClicksShareOneAttempt()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = new FakeLink(false);
        var connector = new FakeConnector { BeforeOpen = gate.Task, OnOpen = () => link.Set(true) };
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.PairedNotConnected);
        var first = supervisor.ConnectAsync(TestContext.Current.CancellationToken);
        await OppoDeviceControllerTests.WaitUntil(() => connector.Attempts == 1);
        var second = supervisor.ConnectAsync(TestContext.Current.CancellationToken);
        gate.SetResult();
        Assert.True((await first).IsSuccess);
        Assert.True((await second).IsSuccess);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, connector.Attempts);
    }

    [Fact]
    public async Task ManualConnect_UnresponsiveDeviceTimesOut()
    {
        var link = new FakeLink(false);
        var connector = new FakeConnector { BeforeOpen = new TaskCompletionSource().Task };
        var (supervisor, store) = Create(link, connector, TimeSpan.FromMilliseconds(50));
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.PairedNotConnected);
        var result = await supervisor.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EncoDeck.Core.Commands.CommandStatus.Timeout, result.Status);
        await WaitFor(store, DeviceConnectionState.PairedNotConnected);
        Assert.Equal(1, connector.Attempts);
    }

    [Fact]
    public async Task LinkCycle_DisconnectsThenReconnects_AndRaisesLinkConnected()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        int linkEvents = 0;
        supervisor.LinkConnected += (_, _) => Interlocked.Increment(ref linkEvents);
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);

        link.Set(false);
        await WaitFor(store, DeviceConnectionState.Disconnected);
        Assert.Null(supervisor.Controller);

        link.Set(true);
        await WaitFor(store, DeviceConnectionState.ControlReady);
        Assert.Equal(2, connector.Attempts);
        Assert.Equal(1, linkEvents);
    }

    [Fact]
    public async Task ChannelDropWhileLinkUp_ReconnectsWithBackoff()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);

        connector.LastDevice!.Channel.RemoteClose();
        await OppoDeviceControllerTests.WaitUntil(() => connector.Attempts == 2 && store.Current.Connection == DeviceConnectionState.ControlReady);
    }

    [Fact]
    public async Task RepeatedFailures_AreBounded_ThenUnavailable_UntilManualRetry()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector { FailWith = ChannelOpenStatus.ConnectFailed };
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();

        await WaitFor(store, DeviceConnectionState.ControlUnavailable);
        int attemptsWhenExhausted = connector.Attempts;
        Assert.Equal(FastPolicy.MaxAttempts + 1, attemptsWhenExhausted);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(attemptsWhenExhausted, connector.Attempts); // no runaway retries
        Assert.NotNull(store.Current.LastError);

        connector.FailWith = null;
        supervisor.RequestReconnect();
        await WaitFor(store, DeviceConnectionState.ControlReady);
    }

    [Fact]
    public async Task MissingService_IsPermanent_WithoutRetries()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector { FailWith = ChannelOpenStatus.ServiceNotFound };
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlUnavailable);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(1, connector.Attempts);
    }

    [Fact]
    public async Task Dispose_ReleasesChannel()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var (supervisor, store) = Create(link, connector);
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlReady);
        await supervisor.DisposeAsync();
        Assert.True(connector.LastDevice!.Channel.IsDisposed);
        Assert.Null(supervisor.Controller);
    }

    [Fact]
    public void ReconnectPolicy_IsExponentialAndBounded()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(1), 2, TimeSpan.FromSeconds(5), 4);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(4));
        Assert.Null(policy.GetDelay(5));
        Assert.Null(policy.GetDelay(0));
    }
}
