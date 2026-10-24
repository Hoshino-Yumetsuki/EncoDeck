// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace EncoDeck.Bluetooth.LeAudio;

public enum LeAudioPairingStage
{
    RemovingPairings,
    WaitingForCleanup,
    WaitingForPairingMode,
    PairingPrimary,
    PairingSecondary,
}

public enum LeAudioPairingOutcome
{
    /// <summary>Both earbuds are bonded over LE; Windows derived the Classic bond (control) from the primary.</summary>
    Paired,
    /// <summary>Only the primary earbud is bonded: audio plays on one side.</summary>
    PrimaryOnly,
    /// <summary>This PC's pairings were removed; the user pairs the earbuds again in Windows Settings.</summary>
    Removed,
    PairingModeNotSeen,
    PairingFailed,
}

public sealed record LeAudioPairingResult(LeAudioPairingOutcome Outcome, string? Detail = null);

/// <summary>Bonds that make up the earbuds' LE Audio setup on this PC.</summary>
public sealed record LeAudioBondState(bool PrimaryBonded, bool PrimaryConnected, bool SecondaryBonded, bool ClassicBonded);

/// <summary>
/// Switches OPPO earbuds between Windows' Classic (A2DP) and LE Audio (LC3) connections.
/// The primary earbud answers at the same public address over Classic and LE, but is not
/// LE-discoverable, so Windows' "Add device" always pairs it over Classic. Pairing its LE
/// identity directly makes Windows derive the Classic bond (CTKD) into the same device
/// container, without Classic audio services. The secondary earbud does not advertise at all;
/// its address is in the primary's scan response (manufacturer 0xBF0D). Verified on the Enco X4.
/// </summary>
public static class LeAudioPairing
{
    /// <summary>Manufacturer data id whose payload <c>[00 00][address LE ×6]..</c> names the secondary earbud.</summary>
    public const ushort SecondaryAddressCompanyId = 0xBF0D;

    private static readonly TimeSpan PairingModeTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ScanResponseWait = TimeSpan.FromSeconds(4);
    private const int PairAttempts = 5;

    /// <summary>
    /// Reads the secondary earbud's address from the primary's 0xBF0D manufacturer data. Accepted
    /// only when it differs from the primary and shares its OUI, so a foreign advert cannot redirect pairing.
    /// </summary>
    public static bool TryParseSecondaryAddress(ulong primary, ReadOnlySpan<byte> data, out ulong secondary)
    {
        secondary = 0;
        if (data.Length < 8) return false;
        ulong address = 0;
        for (int i = 7; i >= 2; i--) address = (address << 8) | data[i];
        if (address == primary || address >> 24 != primary >> 24) return false;
        secondary = address;
        return true;
    }

    public static async Task<LeAudioBondState> GetStateAsync(ulong primary, CancellationToken cancellationToken = default)
    {
        using var le = await OpenLeAsync(primary, cancellationToken).ConfigureAwait(false);
        using var classic = await OpenClassicAsync(primary, cancellationToken).ConfigureAwait(false);
        bool primaryBonded = await LeAudioLink.IsBondedAsync(primary, cancellationToken).ConfigureAwait(false);
        var secondary = await FindSecondaryBondsAsync(primary, le?.Name ?? classic?.Name, cancellationToken).ConfigureAwait(false);
        try
        {
            return new LeAudioBondState(
                primaryBonded,
                le?.ConnectionStatus == BluetoothConnectionStatus.Connected,
                secondary.Count > 0,
                classic?.DeviceInformation.Pairing.IsPaired == true);
        }
        finally
        {
            foreach (var other in secondary) other.Dispose();
        }
    }

    /// <summary>
    /// Waits for the user to open pairing mode, then removes this PC's pairings of the earbuds and
    /// bonds the primary and the secondary over LE. Writes nothing to the earbuds. A timeout or
    /// cancellation before pairing mode leaves the existing pairings untouched.
    /// </summary>
    public static async Task<LeAudioPairingResult> SwitchToLeAudioAsync(ulong primary, IProgress<LeAudioPairingStage>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(LeAudioPairingStage.WaitingForPairingMode);
        var advert = await WaitForPairingModeAsync(primary, cancellationToken).ConfigureAwait(false);
        if (advert is null) return new(LeAudioPairingOutcome.PairingModeNotSeen);

        // Windows may publish the CTKD Classic entry before the LE bond is visible. Block control
        // opens throughout this interval so discovery cannot start RFCOMM while LE pairing runs.
        using var pairingChange = LeAudioLink.BeginPairingChange(primary);
        progress?.Report(LeAudioPairingStage.RemovingPairings);
        await RemoveAllAsync(primary, cancellationToken).ConfigureAwait(false);
        // Pairing LE before Windows has torn down the old Classic device makes CTKD bring back its
        // A2DP/HFP services: the primary then stays on Classic audio and only the other earbud plays.
        progress?.Report(LeAudioPairingStage.WaitingForCleanup);
        await WaitForNodesRemovedAsync(primary, "BTHENUM", cancellationToken).ConfigureAwait(false);

        progress?.Report(LeAudioPairingStage.PairingPrimary);
        var status = await PairLeAsync(primary, cancellationToken).ConfigureAwait(false);
        if (status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired))
            return new(LeAudioPairingOutcome.PairingFailed, status.ToString());

        if (advert.Secondary is not { } secondary) return new(LeAudioPairingOutcome.PrimaryOnly, "secondary address not advertised");
        progress?.Report(LeAudioPairingStage.PairingSecondary);
        status = await PairLeAsync(secondary, cancellationToken).ConfigureAwait(false);
        return status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired
            ? new(LeAudioPairingOutcome.Paired)
            : new(LeAudioPairingOutcome.PrimaryOnly, status.ToString());
    }

    /// <summary>
    /// Removes this PC's pairings of the earbuds (both LE bonds and the Classic bond Windows derived
    /// from them, which carries no audio). The user then pairs them again in Windows Settings, which
    /// sets up Classic audio (A2DP/HFP) with its own connection handling.
    /// </summary>
    public static async Task<LeAudioPairingResult> RemoveLeAudioAsync(ulong primary, IProgress<LeAudioPairingStage>? progress, CancellationToken cancellationToken)
    {
        using var pairingChange = LeAudioLink.BeginPairingChange(primary);
        progress?.Report(LeAudioPairingStage.RemovingPairings);
        await RemoveAllAsync(primary, cancellationToken).ConfigureAwait(false);
        progress?.Report(LeAudioPairingStage.WaitingForCleanup);
        await WaitForNodesRemovedAsync(primary, "BTHLE", cancellationToken).ConfigureAwait(false);
        return new(LeAudioPairingOutcome.Removed);
    }

    /// <summary>
    /// Waits until Windows removed the device nodes of <paramref name="primary"/> whose instance id starts
    /// with <paramref name="enumerator"/> (BTHENUM = Classic, BTHLE = LE), then a little longer.
    /// Verified on the Enco X4: pairing 1–2 s after unpairing is too early, 30 s later is fine.
    /// </summary>
    private static async Task WaitForNodesRemovedAsync(ulong primary, string enumerator, CancellationToken cancellationToken)
    {
        string address = primary.ToString("X12", System.Globalization.CultureInfo.InvariantCulture);
        var deadline = DateTime.UtcNow + NodeRemovalTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var nodes = await Windows.Devices.Enumeration.Pnp.PnpObject.FindAllAsync(
                Windows.Devices.Enumeration.Pnp.PnpObjectType.Device, ["System.Devices.DeviceInstanceId"],
                $"System.Devices.DeviceInstanceId:~~\"{address}\"").AsTask(cancellationToken).ConfigureAwait(false);
            bool left = nodes.Any(n => n.Properties.TryGetValue("System.Devices.DeviceInstanceId", out var id)
                && id is string text && text.StartsWith(enumerator, StringComparison.OrdinalIgnoreCase));
            if (!left) break;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        await Task.Delay(NodeRemovalSettle, cancellationToken).ConfigureAwait(false);
    }

    private static readonly TimeSpan NodeRemovalTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan NodeRemovalSettle = TimeSpan.FromSeconds(5);

    private static async Task RemoveAllAsync(ulong primary, CancellationToken cancellationToken)
    {
        using var le = await OpenLeAsync(primary, cancellationToken).ConfigureAwait(false);
        using var classic = await OpenClassicAsync(primary, cancellationToken).ConfigureAwait(false);
        foreach (var secondary in await FindSecondaryBondsAsync(primary, le?.Name ?? classic?.Name, cancellationToken).ConfigureAwait(false))
        {
            using (secondary) await UnpairAsync(secondary.DeviceInformation.Pairing, cancellationToken).ConfigureAwait(false);
        }
        if (le is not null) await UnpairAsync(le.DeviceInformation.Pairing, cancellationToken).ConfigureAwait(false);
        if (classic is not null) await UnpairAsync(classic.DeviceInformation.Pairing, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bonded LE devices that are the other earbud: the same LE name and the same first four
    /// address bytes as the primary (the secondary's address only differs in its last two bytes).
    /// </summary>
    internal static async Task<List<BluetoothLEDevice>> FindSecondaryBondsAsync(ulong primary, string? name, CancellationToken cancellationToken)
    {
        var found = new List<BluetoothLEDevice>();
        if (string.IsNullOrEmpty(name)) return found;
        var bonded = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask(cancellationToken).ConfigureAwait(false);
        // Open only same-named bonds; some bonded entries are not openable as BluetoothLEDevice at all.
        foreach (var info in bonded.Where(i => i.Name == name))
        {
            BluetoothLEDevice? device;
            try
            {
                device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or COMException)
            {
                continue;
            }
            if (device is null) continue;
            if (device.BluetoothAddress != primary && device.BluetoothAddress >> 16 == primary >> 16 && device.Name == name) found.Add(device);
            else device.Dispose();
        }
        return found;
    }

    private sealed record PairingAdvert(ulong? Secondary);

    /// <summary>Waits for the primary's connectable advert (pairing mode) and reads the secondary address from its scan response.</summary>
    private static async Task<PairingAdvert?> WaitForPairingModeAsync(ulong primary, CancellationToken cancellationToken)
    {
        var connectable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondaryFound = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (args.BluetoothAddress != primary) return;
            if (args.IsConnectable) connectable.TrySetResult();
            foreach (var data in args.Advertisement.GetManufacturerDataByCompanyId(SecondaryAddressCompanyId))
            {
                var bytes = new byte[data.Data.Length];
                DataReader.FromBuffer(data.Data).ReadBytes(bytes);
                if (TryParseSecondaryAddress(primary, bytes, out ulong secondary)) secondaryFound.TrySetResult(secondary);
            }
        }
        watcher.Received += OnReceived;
        try
        {
            watcher.Start();
            try
            {
                await connectable.Task.WaitAsync(PairingModeTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
            try
            {
                return new PairingAdvert(await secondaryFound.Task.WaitAsync(ScanResponseWait, cancellationToken).ConfigureAwait(false));
            }
            catch (TimeoutException)
            {
                return new PairingAdvert(null);
            }
        }
        finally
        {
            watcher.Received -= OnReceived;
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
        }
    }

    /// <summary>The first attempt right after the advert turns connectable may fail, so retry a few times.</summary>
    private static async Task<DevicePairingResultStatus> PairLeAsync(ulong address, CancellationToken cancellationToken)
    {
        var status = DevicePairingResultStatus.Failed;
        for (int attempt = 0; attempt < PairAttempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
            using var device = await OpenLeAsync(address, cancellationToken).ConfigureAwait(false);
            if (device is null) continue;
            if (device.DeviceInformation.Pairing.IsPaired) return DevicePairingResultStatus.AlreadyPaired;
            status = await PairAsync(device.DeviceInformation.Pairing, DevicePairingProtectionLevel.Encryption, cancellationToken).ConfigureAwait(false);
            if (status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired) return status;
        }
        return status;
    }

    /// <summary>Earbuds have no display or keypad: accept Just Works / numeric comparison without a Windows prompt.</summary>
    private static async Task<DevicePairingResultStatus> PairAsync(DeviceInformationPairing pairing, DevicePairingProtectionLevel level, CancellationToken cancellationToken)
    {
        void OnRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args) => args.Accept();
        pairing.Custom.PairingRequested += OnRequested;
        try
        {
            var result = await pairing.Custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.DisplayPin, level)
                .AsTask(cancellationToken).ConfigureAwait(false);
            return result.Status;
        }
        finally
        {
            pairing.Custom.PairingRequested -= OnRequested;
        }
    }

    private static async Task UnpairAsync(DeviceInformationPairing pairing, CancellationToken cancellationToken)
    {
        if (!pairing.IsPaired) return;
        var result = await pairing.UnpairAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (result.Status is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
            throw new InvalidOperationException($"Windows could not remove the earbuds' pairing: {result.Status}");
    }

    private static async Task<BluetoothLEDevice?> OpenLeAsync(ulong address, CancellationToken cancellationToken)
    {
        try
        {
            return await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Public).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            return null;
        }
    }

    private static async Task<BluetoothDevice?> OpenClassicAsync(ulong address, CancellationToken cancellationToken)
    {
        try
        {
            return await BluetoothDevice.FromBluetoothAddressAsync(address).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            return null;
        }
    }
}
