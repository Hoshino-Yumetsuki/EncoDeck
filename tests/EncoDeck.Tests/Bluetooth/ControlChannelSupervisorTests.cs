// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.Commands;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Brands;
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
        public ChannelOpenStatus? FailWith { get; set; }
        public FakeOppoDevice? LastDevice { get; private set; }

        public Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            if (FailWith is { } status) return Task.FromResult(ChannelOpenResult.Failed(status, "fake failure"));
            LastDevice = new FakeOppoDevice();
            return Task.FromResult(new ChannelOpenResult(ChannelOpenStatus.Opened, LastDevice.Channel, serviceUuids[0], null));
        }
    }

    private static (ControlChannelSupervisor Supervisor, DeviceStateStore Store) Create(FakeLink link, FakeConnector connector, IControllableProfile? profile = null)
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = X4 });
        var supervisor = new ControlChannelSupervisor(X4, profile ?? new OppoDeviceProfile(OppoModels.EncoX4), link, connector, store, FastPolicy, linkSettleDelay: TimeSpan.FromMilliseconds(20));
        return (supervisor, store);
    }

    private sealed class StartupProfile(Func<Task<bool>> initialize, bool throwOnCreate = false) : IControllableProfile
    {
        private readonly OppoDeviceProfile _inner = new(OppoModels.EncoX4);
        public string Brand => _inner.Brand;
        public bool IsGeneric => false;
        public string ModelId => _inner.ModelId;
        public string DisplayName => _inner.DisplayName;
        public string ImageKey => _inner.ImageKey;
        public IDeviceCapabilities Capabilities => _inner.Capabilities;
        public IReadOnlyList<NoiseControlOption> NoiseControlOptions => _inner.NoiseControlOptions;
        public IReadOnlyList<EqualizerPreset> EqualizerPresets => _inner.EqualizerPresets;
        public IReadOnlyList<Guid> ControlServiceUuids => _inner.ControlServiceUuids;
        public int MatchScore(DeviceIdentity identity) => _inner.MatchScore(identity);
        public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) => throwOnCreate
            ? throw new IOException("controller creation failed") : new StartupController(channel, context, initialize);
    }

    private sealed class StartupController(IDuplexByteChannel channel, ControllerContext context, Func<Task<bool>> initialize)
        : BrandControllerBase(channel, context)
    {
        protected override Task<bool> StartSessionAsync(CancellationToken cancellationToken) => initialize();
        protected override void OnReceived(ReadOnlySpan<byte> data) { }
        public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(CommandResult.Ok);
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
    public async Task MissingLeEndpoint_StopsConnectingWithoutRetrying_AndManualRetryCanRecover()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector { FailWith = ChannelOpenStatus.ControlEndpointNotFound };
        var (supervisor, store) = Create(link, connector);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlUnavailable);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.True(link.IsConnected);
        Assert.Null(supervisor.Controller);
        Assert.Equal(1, connector.Attempts);
        Assert.Equal("fake failure", store.Current.LastError?.Message);
        Assert.Equal("Error_ControlEndpointNotFound", store.Current.LastError?.ResourceKey);

        connector.FailWith = null;
        supervisor.RequestReconnect();
        await WaitFor(store, DeviceConnectionState.ControlReady);
        Assert.Null(store.Current.LastError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControllerStartupThrows_ReleasesChannelAndEndsConnecting(bool throwOnCreate)
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var profile = new StartupProfile(() => throw new IOException("handshake failed"), throwOnCreate);
        var (supervisor, store) = Create(link, connector, profile);
        await using var _ = supervisor;
        supervisor.Start();
        await WaitFor(store, DeviceConnectionState.ControlUnavailable);
        Assert.Null(supervisor.Controller);
        Assert.NotNull(store.Current.LastError);
        Assert.Equal(FastPolicy.MaxAttempts + 1, connector.Attempts);
        Assert.True(connector.LastDevice!.Channel.IsDisposed);
    }

    [Fact]
    public async Task LinkDropsDuringHandshake_DoesNotBecomeControlReady()
    {
        var link = new FakeLink(true);
        var connector = new FakeConnector();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = new StartupProfile(() => { started.TrySetResult(); return finish.Task; });
        var (supervisor, store) = Create(link, connector, profile);
        await using var _ = supervisor;
        var transitions = new List<DeviceConnectionState>();
        store.StateChanged += (_, state) => { lock (transitions) transitions.Add(state.Current.Connection); };
        supervisor.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        link.Set(false);
        finish.SetResult(true);
        await WaitFor(store, DeviceConnectionState.Disconnected);
        Assert.Null(supervisor.Controller);
        Assert.True(connector.LastDevice!.Channel.IsDisposed);
        lock (transitions) Assert.DoesNotContain(DeviceConnectionState.ControlReady, transitions);
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
