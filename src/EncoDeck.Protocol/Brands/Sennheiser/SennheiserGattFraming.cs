// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace EncoDeck.Protocol.Brands.Sennheiser;

/// <summary>BLE carries vendor/command/payload PDUs without the RFCOMM envelope.</summary>
public static class SennheiserGattFraming
{
    public static byte[] UnwrapCommand(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 8 || frame[0] != 0xFF || frame[1] != 3 || frame[2] != 0 || frame.Length != 8 + frame[3]
            || frame[4] != 4 || frame[5] != 0x95) throw new InvalidDataException("Invalid GAIA v3 BLE command envelope.");
        return frame[4..].ToArray();
    }
    public static byte[] WrapNotification(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length is < 4 or > 1028 || pdu[0] != 4 || pdu[1] != 0x95) throw new InvalidDataException("Invalid Sennheiser BLE control PDU.");
        int length = pdu.Length - 4;
        int header = length > 254 ? 5 : 4;
        byte[] frame = new byte[header + pdu.Length];
        frame[0] = 0xFF; frame[1] = header == 4 ? (byte)3 : (byte)4; frame[2] = header == 4 ? (byte)0 : (byte)2;
        if (header == 4) frame[3] = (byte)length;
        else BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), (ushort)length);
        pdu.CopyTo(frame.AsSpan(header)); return frame;
    }
}
