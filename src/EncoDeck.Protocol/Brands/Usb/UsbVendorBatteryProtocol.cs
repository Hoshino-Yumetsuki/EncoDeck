// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Brands.Usb;

/// <summary>Read-only queries for individually documented receiver protocols. Never select by brand alone.</summary>
public static class UsbVendorBatteryProtocol
{
    public static string? Model(ushort vendor, ushort product) => (vendor, product) switch
    {
        (0x3837, 0x6045) => "mchose-usb-x9",
        (0x1532, 0x0528) => "razer-usb-blackshark-v2-pro-2020",
        (0x1532, 0x0555) => "razer-usb-blackshark-v2-pro-2023",
        _ => null,
    };

    public static bool IsCollection(ushort vendor, ushort product, ushort page, ushort usage) =>
        Model(vendor, product) is not null && usage == 1 &&
        page == (vendor == 0x3837 ? 0xFF90 : 0xFF00);

    public static byte[] Query(ushort vendor, ushort product)
    {
        if (Model(vendor, product) is null) throw new ArgumentException("Undocumented receiver");
        var frame = new byte[64];
        if (vendor == 0x3837) { frame[0] = 0x55; frame[1] = 0x65; frame[2] = 1; }
        else if (product == 0x0528) new byte[] { 0xFF, 0x0A, 0, 0xFD, 4, 0x12, 0xF1, 2, 5 }.CopyTo(frame, 0);
        else
        {
            frame[0] = 2; frame[1] = 0x80; frame[2] = 8; frame[5] = 0x50; frame[6] = 0x41;
            frame[7] = 8; frame[9] = 3; frame[10] = 0x21;
        }
        return frame;
    }

    public static byte[] Remote(bool enabled)
    {
        var frame = Query(0x1532, 0x0555);
        frame[2] = 7; frame[7] = 0x0E; frame[9] = 2; frame[10] = 0xE1; frame[11] = enabled ? (byte)1 : (byte)0;
        return frame;
    }

    public static int? Parse(ushort vendor, ushort product, ReadOnlySpan<byte> frame)
    {
        // WinRT includes the report ID. Never try an offset heuristic or clamp an invalid percentage.
        if (Model(vendor, product) is null || frame.Length != 64) return null;
        if (vendor == 0x3837)
            return frame[0] == 0x55 && frame[1] == 0x65 && frame[3] == 2 && frame[2] <= 100 ? frame[2] : null;
        if (product == 0x0528)
        {
            ReadOnlySpan<byte> fresh = [0xFF, 0x0F, 5, 0xFE, 0x12, 4, 0x1F, 8, 5];
            // Off/out-of-range replies retain old payload under a different header.
            // The reported level is unreliable during active charging (state 09).
            return frame.StartsWith(fresh) && (frame[12] != 0 || frame[13] != 0) && frame[11] != 9 && frame[14] <= 100 ? frame[14] : null;
        }
        return frame[0] == 2 && frame[12] == 0x21 && frame[13] == 1 && frame[14] == 1 && frame[15] <= 100 ? frame[15] : null;
    }
}
