// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Oppo;

/// <summary>
/// Command ids of the OPPO/HeyMelody control protocol. Responses carry <c>id | 0x8000</c>.
/// Evidence for each id (community source, APK analysis, Enco X4 capture) is tracked in
/// docs/PROTOCOL_FINDINGS.md — an id being listed here does not mean it is verified on a model.
/// </summary>
public static class OppoCommand
{
    // Queries (0x01xx)
    public const ushort Handshake = 0x0100;
    public const ushort QueryMtu = 0x0101;
    public const ushort QueryVid = 0x0102;
    public const ushort QueryUpgradeCapability = 0x0107;
    public const ushort QuerySpatialType = 0x012A;
    public const ushort QueryGameSound = 0x012B;
    public const ushort QueryPromptVolume = 0x0130;
    public const ushort QueryBassEngine = 0x0124;
    public const ushort QueryPriorityDevice = 0x0132;
    public const ushort QueryHeadMotionType = 0x0134;
    /// <summary>LE/BR switch ("game low latency with LE Audio"): <c>[st][bits]</c>, bit0 on, bit1 owner, bit2 reset.</summary>
    public const ushort QueryLeBrSwitch = 0x0136;
    public const ushort QueryProductId = 0x0103;
    public const ushort QueryFirmware = 0x0105;
    public const ushort QueryBattery = 0x0106;
    public const ushort QueryTouchControls = 0x0108;
    public const ushort QueryWearState = 0x0109;
    public const ushort QueryColor = 0x010B;
    public const ushort QueryConnectedDevices = 0x0112;
    public const ushort QueryCodec = 0x0114;
    public const ushort QueryNoiseControl = 0x010C;
    public const ushort QueryFeatureStatus = 0x010D;
    public const ushort QueryEqualizer = 0x010F;
    public const ushort QueryEqualizerList = 0x0122;

    // Event subscription / notifications (0x02xx)
    public const ushort QueryEventCodes = 0x0200;
    public const ushort EventCodesAltResponse = 0x8203;
    public const ushort DeviceEvent = 0x0204;
    public const ushort SubscribeEvents = 0x0205;

    // Settings (0x04xx)
    public const ushort FindEarbuds = 0x0400;
    public const ushort SetKeyFunction = 0x0401;
    /// <summary>Device → host "free music" request; answered <c>[00]</c>.</summary>
    public const ushort FreeMusicRequest = 0x0402;
    public const ushort SetFeature = 0x0403;
    public const ushort FitTest = 0x0405;
    public const ushort SetBassEngine = 0x041B;
    public const ushort SetHostCapabilities = 0x0419;
    public const ushort SetGameSound = 0x0423;
    public const ushort SetPromptVolume = 0x0427;
    public const ushort OperateMultiConnect = 0x0429;
    public const ushort SetHeadMotionType = 0x0431;
    /// <summary>Sets the LE/BR switch, <c>[01]</c> on / <c>[00]</c> off. Not needed to pair or use LE Audio on Windows.</summary>
    public const ushort SetLeBrSwitch = 0x0433;
    public const ushort SetNoiseControl = 0x0404;
    public const ushort SetEqualizer = 0x0406;
    public const ushort SetCustomEqualizer = 0x0418;
    public const ushort SetSpatialAudio = 0x0422;

    // Device-originated (0x05xx)
    /// <summary>Earbuds ask the host for the current time; answered with <c>0x8500 [00][unix seconds u32 LE]</c>.</summary>
    public const ushort TimeRequest = 0x0500;
    public const ushort FeatureChangedNotification = 0x0503;
    public const ushort EqualizerChangedNotification = 0x0504;
    public const ushort CustomEqualizerChangedNotification = 0x0506;
    public const ushort BassEngineChangedNotification = 0x0508;
    /// <summary>Game-EQ foreground query; answered <c>00 01 00</c> (no game list on Windows).</summary>
    public const ushort GameEqRequest = 0x050C;
    /// <summary>Earbuds ask for the host's app capabilities; answered <c>00 00 00</c>.</summary>
    public const ushort AppCapabilitiesRequest = 0x051C;
    /// <summary>Earbuds ask the host to apply its own spatial mode; answered <c>01</c> (not supported).</summary>
    public const ushort PhoneSpatialRequest = 0x051D;
    public const ushort SpatialAudioNotification = 0x0510;

    public static ushort ResponseOf(ushort request) => (ushort)(request | OppoFrame.ResponseFlag);

    public static string Describe(ushort command) => (command & 0x7FFF) switch
    {
        Handshake => "Handshake",
        QueryMtu => "QueryMtu",
        QueryVid => "QueryVid",
        QueryUpgradeCapability => "QueryUpgradeCapability",
        QuerySpatialType => "QuerySpatialType",
        QueryGameSound => "QueryGameSound",
        QueryPromptVolume => "QueryPromptVolume",
        QueryBassEngine => "QueryBassEngine",
        QueryPriorityDevice => "QueryPriorityDevice",
        QueryHeadMotionType => "QueryHeadMotionType",
        QueryLeBrSwitch => "QueryLeBrSwitch",
        QueryColor => "QueryColor",
        SetKeyFunction => "SetKeyFunction",
        FreeMusicRequest => "FreeMusicRequest",
        FitTest => "FitTest",
        SetBassEngine => "SetBassEngine",
        SetHostCapabilities => "SetHostCapabilities",
        SetGameSound => "SetGameSound",
        SetPromptVolume => "SetPromptVolume",
        OperateMultiConnect => "OperateMultiConnect",
        SetHeadMotionType => "SetHeadMotionType",
        SetLeBrSwitch => "SetLeBrSwitch",
        CustomEqualizerChangedNotification => "CustomEqualizerChanged",
        BassEngineChangedNotification => "BassEngineChanged",
        GameEqRequest => "GameEqRequest",
        PhoneSpatialRequest => "PhoneSpatialRequest",
        QueryProductId => "QueryProductId",
        QueryFirmware => "QueryFirmware",
        QueryBattery => "QueryBattery",
        QueryTouchControls => "QueryTouchControls",
        QueryWearState => "QueryWearState",
        QueryConnectedDevices => "QueryConnectedDevices",
        QueryCodec => "QueryCodec",
        FindEarbuds => "FindEarbuds",
        EqualizerChangedNotification => "EqualizerChanged",
        FeatureChangedNotification => "FeatureChanged",
        TimeRequest => "TimeRequest",
        AppCapabilitiesRequest => "AppCapabilitiesRequest",
        QueryNoiseControl => "QueryNoiseControl",
        QueryFeatureStatus => "QueryFeatureStatus",
        QueryEqualizer => "QueryEqualizer",
        QueryEqualizerList => "QueryEqualizerList",
        QueryEventCodes => "QueryEventCodes",
        0x0203 => "EventCodes(alt)",
        DeviceEvent => "DeviceEvent",
        SubscribeEvents => "SubscribeEvents",
        SetFeature => "SetFeature",
        SetNoiseControl => "SetNoiseControl",
        SetEqualizer => "SetEqualizer",
        SetCustomEqualizer => "SetCustomEqualizer",
        SetSpatialAudio => "SetSpatialAudio",
        SpatialAudioNotification => "SpatialAudioNotification",
        _ => "Unknown",
    } + ((command & OppoFrame.ResponseFlag) != 0 ? ".Response" : string.Empty);
}

/// <summary>Event codes carried in the first payload byte of <see cref="OppoCommand.DeviceEvent"/>.</summary>
public static class OppoEventCode
{
    public const byte Battery = 0x01;
    /// <summary>Per-component wear / case position (observed on Enco X4).</summary>
    public const byte WearState = 0x02;
    public const byte NoiseControl = 0x03;
    /// <summary>Fit test result <c>[04][dev][st][dev][st]</c>.</summary>
    public const byte FitTest = 0x04;
    /// <summary>Connected-device history; the host must answer <c>0x8204 [00][F2]</c>.</summary>
    public const byte ConnectionHistory = 0xF2;
    /// <summary>Telemetry JSON; the host must answer <c>0x8204 [00][F4]{"cmd":…}</c>.</summary>
    public const byte Telemetry = 0xF4;
    /// <summary>List of source devices known to the earbuds (contains addresses and names — sensitive).</summary>
    public const byte ConnectedDevices = 0x06;
}

/// <summary>
/// Sequence numbers. Auto-allocated numbers cycle 0x01..0xEF so they never collide with the fixed
/// values some commands use (0x00, 0xF0), which keeps response correlation unambiguous.
/// </summary>
public sealed class OppoSequenceAllocator
{
    public const byte FixedQuery = 0x00;
    public const byte FixedSet = 0xF0;
    private const byte First = 0x01;
    private const byte Last = 0xEF;

    private int _next = First;

    public byte Next()
    {
        while (true)
        {
            int current = Volatile.Read(ref _next);
            int following = current >= Last ? First : current + 1;
            if (Interlocked.CompareExchange(ref _next, following, current) == current) return (byte)current;
        }
    }
}
