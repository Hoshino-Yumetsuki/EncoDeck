// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Brands.Akg;
using EncoDeck.Protocol.Brands.Sony;

namespace EncoDeck.Protocol.Brands;

/// <summary>
/// A BLE GATT service that carries a brand's ordinary byte stream: commands are written to one
/// characteristic and replies arrive as notifications on another. The named characteristic is used
/// when the service has it; otherwise (or when the UUID is null) the first writable / first notifying
/// characteristic of the service.
/// </summary>
/// <param name="NameMatchedLeFallback">
/// When the classic address has no such service, the endpoint may be found on one separate LE
/// advertiser named like the paired device (or "&lt;name&gt;-LE"); never used when several match.
/// </param>
public sealed record GattStreamEndpoint(Guid Service, Guid? Write, Guid? Notify, string Name, bool NameMatchedLeFallback = false);

/// <summary>Byte-stream GATT services opened by the shared notify/write channel.</summary>
public static class GattStreamEndpoints
{
    public static IReadOnlyList<GattStreamEndpoint> All { get; } =
    [
        // HPC "to accessory" / "from accessory"; mos9527/SonyHeadphonesClient PR #43 takes the first
        // writable / notifying characteristic, which stays the fallback.
        new(SonyAkgProfiles.BleServiceUuid, SonyAkgProfiles.BleWriteUuid, SonyAkgProfiles.BleNotifyUuid, "Sony MDR GATT"),
        new(AkgBleUuids.BesService, AkgBleUuids.BesWrite, AkgBleUuids.BesNotify, "AKG BES GATT", NameMatchedLeFallback: true),
        new(AkgBleUuids.RaceService, AkgBleUuids.RaceWrite, AkgBleUuids.RaceNotify, "Airoha RACE GATT", NameMatchedLeFallback: true),
    ];

    public static GattStreamEndpoint? Find(Guid service) => All.FirstOrDefault(e => e.Service == service);
}
