
namespace LiteNetLib.Utils;

/// <summary>
/// Engine-free subset of LiteNetLibManager's packed integer wire helpers.
///
/// The Unity client gets these extension methods from LiteNetLibManager's
/// NetDataReaderExtension / NetDataWriterExtension classes, but those full
/// classes depend on UnityEngine and the LiteNetLibManager serialization
/// registry.  The standalone GameServer references raw LiteNetLib only, so it
/// carries the exact packed integer codec needed by the existing wire protocol
/// without importing Unity into the server process.
///
/// Keep this encoding byte-for-byte compatible with the client helpers under:
/// Assets/Plugins/LiteNetLibManager/Scripts/Extensions/.
/// </summary>
internal static class LiteNetLibPackedIntegerExtensions
{
    public static ushort GetPackedUShort(this NetDataReader reader)
    {
        return (ushort)GetPackedULong(reader);
    }

    public static uint GetPackedUInt(this NetDataReader reader)
    {
        return (uint)GetPackedULong(reader);
    }

    public static long GetPackedLong(this NetDataReader reader)
    {
        return ((long)GetPackedInt(reader)) << 32 | (uint)GetPackedInt(reader);
    }

    public static int GetPackedInt(this NetDataReader reader)
    {
        uint value = GetPackedUInt(reader);
        return (int)((value >> 1) ^ (-(int)(value & 1)));
    }

    private static ulong GetPackedULong(NetDataReader reader)
    {
        byte a0 = reader.GetByte();
        if (a0 < 241)
            return a0;

        byte a1 = reader.GetByte();
        if (a0 >= 241 && a0 <= 248)
            return 240 + 256 * (a0 - 241UL) + a1;

        byte a2 = reader.GetByte();
        if (a0 == 249)
            return 2288 + 256UL * a1 + a2;

        byte a3 = reader.GetByte();
        if (a0 == 250)
            return a1 + ((ulong)a2 << 8) + ((ulong)a3 << 16);

        byte a4 = reader.GetByte();
        if (a0 == 251)
            return a1 + ((ulong)a2 << 8) + ((ulong)a3 << 16) + ((ulong)a4 << 24);

        byte a5 = reader.GetByte();
        if (a0 == 252)
            return a1 + ((ulong)a2 << 8) + ((ulong)a3 << 16) + ((ulong)a4 << 24) + ((ulong)a5 << 32);

        byte a6 = reader.GetByte();
        if (a0 == 253)
            return a1 + ((ulong)a2 << 8) + ((ulong)a3 << 16) + ((ulong)a4 << 24) + ((ulong)a5 << 32) + ((ulong)a6 << 40);

        byte a7 = reader.GetByte();
        if (a0 == 254)
            return a1 + ((ulong)a2 << 8) + ((ulong)a3 << 16) + ((ulong)a4 << 24) + ((ulong)a5 << 32) + ((ulong)a6 << 40) + ((ulong)a7 << 48);

        byte a8 = reader.GetByte();
        if (a0 == 255)
            return a1 + ((ulong)a2 << 8) + ((ulong)a3 << 16) + ((ulong)a4 << 24) + ((ulong)a5 << 32) + ((ulong)a6 << 40) + ((ulong)a7 << 48) + ((ulong)a8 << 56);

        throw new IndexOutOfRangeException("GetPackedULong() failure: " + a0);
    }

    public static void PutPackedUShort(this NetDataWriter writer, ushort value)
    {
        PutPackedULong(writer, value);
    }

    public static void PutPackedUInt(this NetDataWriter writer, uint value)
    {
        PutPackedULong(writer, value);
    }

    public static void PutPackedLong(this NetDataWriter writer, long value)
    {
        PutPackedInt(writer, (int)(value >> 32));
        PutPackedInt(writer, (int)(value & uint.MaxValue));
    }

    public static void PutPackedInt(this NetDataWriter writer, int value)
    {
        PutPackedUInt(writer, (uint)((value << 1) ^ (value >> 31)));
    }

    private static void PutPackedULong(NetDataWriter writer, ulong value)
    {
        if (value <= 240)
        {
            writer.Put((byte)value);
            return;
        }

        if (value <= 2287)
        {
            writer.Put((byte)((value - 240) / 256 + 241));
            writer.Put((byte)((value - 240) % 256));
            return;
        }

        if (value <= 67823)
        {
            writer.Put((byte)249);
            writer.Put((byte)((value - 2288) / 256));
            writer.Put((byte)((value - 2288) % 256));
            return;
        }

        if (value <= 16777215)
        {
            writer.Put((byte)250);
            writer.Put((byte)(value & 0xFF));
            writer.Put((byte)((value >> 8) & 0xFF));
            writer.Put((byte)((value >> 16) & 0xFF));
            return;
        }

        if (value <= 4294967295)
        {
            writer.Put((byte)251);
            writer.Put((byte)(value & 0xFF));
            writer.Put((byte)((value >> 8) & 0xFF));
            writer.Put((byte)((value >> 16) & 0xFF));
            writer.Put((byte)((value >> 24) & 0xFF));
            return;
        }

        if (value <= 1099511627775)
        {
            writer.Put((byte)252);
            writer.Put((byte)(value & 0xFF));
            writer.Put((byte)((value >> 8) & 0xFF));
            writer.Put((byte)((value >> 16) & 0xFF));
            writer.Put((byte)((value >> 24) & 0xFF));
            writer.Put((byte)((value >> 32) & 0xFF));
            return;
        }

        if (value <= 281474976710655)
        {
            writer.Put((byte)253);
            writer.Put((byte)(value & 0xFF));
            writer.Put((byte)((value >> 8) & 0xFF));
            writer.Put((byte)((value >> 16) & 0xFF));
            writer.Put((byte)((value >> 24) & 0xFF));
            writer.Put((byte)((value >> 32) & 0xFF));
            writer.Put((byte)((value >> 40) & 0xFF));
            return;
        }

        if (value <= 72057594037927935)
        {
            writer.Put((byte)254);
            writer.Put((byte)(value & 0xFF));
            writer.Put((byte)((value >> 8) & 0xFF));
            writer.Put((byte)((value >> 16) & 0xFF));
            writer.Put((byte)((value >> 24) & 0xFF));
            writer.Put((byte)((value >> 32) & 0xFF));
            writer.Put((byte)((value >> 40) & 0xFF));
            writer.Put((byte)((value >> 48) & 0xFF));
            return;
        }

        writer.Put((byte)255);
        writer.Put((byte)(value & 0xFF));
        writer.Put((byte)((value >> 8) & 0xFF));
        writer.Put((byte)((value >> 16) & 0xFF));
        writer.Put((byte)((value >> 24) & 0xFF));
        writer.Put((byte)((value >> 32) & 0xFF));
        writer.Put((byte)((value >> 40) & 0xFF));
        writer.Put((byte)((value >> 48) & 0xFF));
        writer.Put((byte)((value >> 56) & 0xFF));
    }
}
