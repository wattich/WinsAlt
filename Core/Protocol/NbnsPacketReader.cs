using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using WinsAlt.Core.Domain;

namespace WinsAlt.Core.Protocol;

/// <summary>
/// Forward-only, bounds-checked cursor over a received datagram. A <c>ref struct</c> over
/// <see cref="ReadOnlySpan{T}"/>: it lives on the stack and reads straight out of the pooled
/// receive buffer.
/// </summary>
public ref struct NbnsPacketReader
{
    private readonly ReadOnlySpan<byte> _packet;
    private int _pos;

    public NbnsPacketReader(ReadOnlySpan<byte> packet, int position = 0)
    {
        _packet = packet;
        _pos = position;
    }

    public readonly int Position => _pos;
    public readonly int Remaining => _packet.Length - _pos;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadUInt16(out ushort value)
    {
        if (Remaining < 2) { value = 0; return false; }
        value = BinaryPrimitives.ReadUInt16BigEndian(_packet[_pos..]);
        _pos += 2;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadUInt32(out uint value)
    {
        if (Remaining < 4) { value = 0; return false; }
        value = BinaryPrimitives.ReadUInt32BigEndian(_packet[_pos..]);
        _pos += 4;
        return true;
    }

    public bool TryReadName(out NameKey name, out bool hasScope)
    {
        if (!NbnsName.TryDecode(_packet, _pos, out name, out int consumed, out hasScope)) return false;
        _pos += consumed;
        return true;
    }
}

/// <summary>
/// The parts of an NBNS packet a name server acts on, flattened into one value type:
/// the header, the (single) question or first record name, and the first NB resource record.
/// </summary>
public struct NbnsMessage
{
    public NbnsHeader Header;
    /// <summary>Question name; for a response with no question section, the first record's name.</summary>
    public NameKey Name;
    /// <summary>The name carried NetBIOS scope labels (unsupported - such packets are ignored).</summary>
    public bool HasScope;
    public ushort QuestionType;

    /// <summary>True when an NB record with at least one (NB_FLAGS, address) entry was present.</summary>
    public bool HasNbRecord;
    public uint Ttl;
    public ushort NbFlags;
    /// <summary>First NB_ADDRESS, host-order numeric (192.168.1.10 = 0xC0A8010A).</summary>
    public uint Address;
    /// <summary>Where the NB record's RDATA - a list of 6-byte (NB_FLAGS, address) entries - sits in the packet.</summary>
    public int RdataOffset;
    public int RdataLength;

    /// <summary>True when <paramref name="address"/> is one of the addresses listed in the NB record.</summary>
    public readonly bool ListsAddress(ReadOnlySpan<byte> packet, uint address)
    {
        if (!HasNbRecord || RdataOffset + RdataLength > packet.Length) return false;
        var rdata = packet.Slice(RdataOffset, RdataLength);
        for (int i = 0; i + 6 <= rdata.Length; i += 6)
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(rdata[(i + 2)..]) == address) return true;
        return false;
    }

    /// <summary>Copies every address listed in the NB record into <paramref name="destination"/>. Returns the count.</summary>
    public readonly int CopyAddresses(ReadOnlySpan<byte> packet, Span<uint> destination)
    {
        if (!HasNbRecord || RdataOffset + RdataLength > packet.Length) return 0;
        var rdata = packet.Slice(RdataOffset, RdataLength);
        int count = 0;
        for (int i = 0; i + 6 <= rdata.Length && count < destination.Length; i += 6)
            destination[count++] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(rdata[(i + 2)..]);
        return count;
    }

    public static bool TryParse(ReadOnlySpan<byte> packet, out NbnsMessage message)
    {
        message = default;
        if (!NbnsHeader.TryRead(packet, out var header)) return false;
        message.Header = header;

        var reader = new NbnsPacketReader(packet, NbnsHeader.Size);

        if (header.QdCount > 1) return false;
        if (header.QdCount == 1)
        {
            if (!reader.TryReadName(out message.Name, out message.HasScope)) return false;
            if (!reader.TryReadUInt16(out message.QuestionType) || !reader.TryReadUInt16(out _)) return false;
        }

        int records = header.AnCount + header.NsCount + header.ArCount;
        if (records == 0)
        {
            if (header.QdCount == 1) return true;

            // A negative response as RFC 1002 §4.2.14 draws it (and Microsoft WINS sends it):
            // all four counts are zero, yet the name of the failed query still follows the header.
            return header.IsResponse && reader.TryReadName(out message.Name, out message.HasScope);
        }

        // Only the first record matters: a registration/release carries exactly one NB record in
        // the additional section, and a query response carries its answer first.
        if (!reader.TryReadName(out var recordName, out bool recordScope)) return false;
        if (!reader.TryReadUInt16(out ushort type) || !reader.TryReadUInt16(out _)) return false;
        if (!reader.TryReadUInt32(out uint ttl) || !reader.TryReadUInt16(out ushort rdLength)) return false;
        if (reader.Remaining < rdLength) return false;

        if (header.QdCount == 0)
        {
            message.Name = recordName;
            message.HasScope = recordScope;
        }

        if (type == NbnsType.Nb && rdLength >= 6)
        {
            message.RdataOffset = reader.Position;
            message.RdataLength = rdLength;
            reader.TryReadUInt16(out message.NbFlags);
            reader.TryReadUInt32(out message.Address);
            message.Ttl = ttl;
            message.HasNbRecord = true;
        }
        return true;
    }
}
