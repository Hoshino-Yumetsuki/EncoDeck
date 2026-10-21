// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Oppo;
using EncoDeck.Tests.Fakes;

namespace EncoDeck.Tests.Protocol;

public class OppoSessionTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task Request_IsAnsweredByMatchingResponse()
    {
        var device = new FakeOppoDevice();
        await using var session = new OppoSession(device.Channel);
        session.Start();
        var result = await session.RequestAsync(new OppoRequest(OppoCommand.QueryProductId, []), Short, TestContext.Current.CancellationToken);
        Assert.Equal(RequestStatus.Success, result.Status);
        Assert.True(OppoParsers.TryParseProductId(result.Response!.Value, out uint pid));
        Assert.Equal(0x06EC10u, pid);
    }

    [Fact]
    public async Task Request_TimesOutWhenDeviceIsSilent()
    {
        var channel = new FakeByteChannel();
        await using var session = new OppoSession(channel);
        session.Start();
        var result = await session.RequestAsync(new OppoRequest(OppoCommand.QueryBattery, []), TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.Equal(RequestStatus.Timeout, result.Status);
        Assert.True(session.IsOpen);
    }

    [Fact]
    public async Task Request_CanBeCancelled()
    {
        var channel = new FakeByteChannel();
        await using var session = new OppoSession(channel);
        session.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await session.RequestAsync(new OppoRequest(OppoCommand.QueryBattery, []), TimeSpan.FromSeconds(10), cts.Token);
        Assert.Equal(RequestStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task UnrelatedFrames_AreSurfacedAsUnsolicited_WhileRequestWaits()
    {
        var channel = new FakeByteChannel();
        channel.Responder = request => request.Command == OppoCommand.QueryBattery
            ? [new OppoFrame(OppoCommand.DeviceEvent, 9, [0x01, 0x01, 0x01, 0x50]), new OppoFrame(0x8106, request.Sequence, [0x00, 0x01, 0x01, 0x50])]
            : null;
        await using var session = new OppoSession(channel);
        var unsolicited = new List<OppoFrame>();
        session.UnsolicitedFrame += (_, f) => { lock (unsolicited) unsolicited.Add(f); };
        session.Start();

        var result = await session.RequestAsync(new OppoRequest(OppoCommand.QueryBattery, [], SequenceMode.FixedSet), Short, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        lock (unsolicited) Assert.Single(unsolicited, f => f.Command == OppoCommand.DeviceEvent);
    }

    [Fact]
    public async Task Requests_AreSerialized_AndEachGetsItsOwnResponse()
    {
        var device = new FakeOppoDevice();
        await using var session = new OppoSession(device.Channel);
        session.Start();
        var tasks = Enumerable.Range(0, 20).Select(i => session.RequestAsync(
            i % 2 == 0 ? new OppoRequest(OppoCommand.QueryProductId, []) : new OppoRequest(OppoCommand.QueryEqualizer, []),
            Short,
            TestContext.Current.CancellationToken)).ToList();
        var results = await Task.WhenAll(tasks);
        for (int i = 0; i < results.Length; i++)
        {
            Assert.True(results[i].IsSuccess);
            Assert.Equal(i % 2 == 0 ? 0x8103 : 0x810F, results[i].Response!.Value.Command);
        }
    }

    [Fact]
    public async Task AlternativeResponseCommand_IsAccepted()
    {
        var channel = new FakeByteChannel();
        channel.Responder = r => [new OppoFrame(OppoCommand.EventCodesAltResponse, r.Sequence, [0x00, 0x01, 0x01])];
        await using var session = new OppoSession(channel);
        session.Start();
        var request = new OppoRequest(OppoCommand.QueryEventCodes, [], ResponseCommands: [0x8200, OppoCommand.EventCodesAltResponse]);
        var result = await session.RequestAsync(request, Short, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RemoteClose_FailsPendingRequest_AndRaisesClosedOnce()
    {
        var channel = new FakeByteChannel();
        await using var session = new OppoSession(channel);
        int closedCount = 0;
        SessionCloseReason? reason = null;
        session.Closed += (_, e) => { Interlocked.Increment(ref closedCount); reason = e.Reason; };
        session.Start();

        var pending = session.RequestAsync(new OppoRequest(OppoCommand.QueryBattery, []), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        channel.RemoteClose();
        var result = await pending;

        Assert.Equal(RequestStatus.Closed, result.Status);
        Assert.False(session.IsOpen);
        Assert.Equal(1, closedCount);
        Assert.Equal(SessionCloseReason.RemoteClosed, reason);
        Assert.True(channel.IsDisposed);
    }

    [Fact]
    public async Task WriteFailure_ReturnsTransportError_AndClosesSession()
    {
        var channel = new FakeByteChannel { FailWritesWith = new IOException("radio off") };
        await using var session = new OppoSession(channel);
        session.Start();
        var result = await session.RequestAsync(new OppoRequest(OppoCommand.QueryBattery, []), Short, TestContext.Current.CancellationToken);
        Assert.Equal(RequestStatus.TransportError, result.Status);
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotBreakReadLoop()
    {
        var device = new FakeOppoDevice();
        await using var session = new OppoSession(device.Channel);
        session.UnsolicitedFrame += (_, _) => throw new InvalidOperationException("bad handler");
        session.Start();
        device.PushBatteryEvent(0x01, 0x50);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var result = await session.RequestAsync(new OppoRequest(OppoCommand.QueryProductId, []), Short, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RequestAfterDispose_ReturnsClosed()
    {
        var device = new FakeOppoDevice();
        var session = new OppoSession(device.Channel);
        session.Start();
        await session.DisposeAsync();
        var result = await session.RequestAsync(new OppoRequest(OppoCommand.QueryProductId, []), Short, TestContext.Current.CancellationToken);
        Assert.Equal(RequestStatus.Closed, result.Status);
    }
}
