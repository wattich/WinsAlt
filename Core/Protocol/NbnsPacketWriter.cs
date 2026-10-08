using System.Buffers.Binary;
using WinsAlt.Core.Domain;

namespace WinsAlt.Core.Protocol;

/// <summary>
/// Sequential writer over a caller-owned buffer (a worker's reusable response buffer).
/// The caller sizes the buffer; every packet built here is far below <see cref="MaxPacket"/>.
/// </summary>
public ref struct NbnsPacketWriter
{
    /// <summary>RFC 1002 caps NBNS datagrams at 576 bytes.</summary>
    public const int MaxPacket = 576;

    private readonly Span<byte> _buffer;
    private int _pos;

    public NbnsPacketWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _pos = 0;
    }

    public readonly int Length => _pos;

    public void WriteHeader(ushort transactionId, ushort flags, ushort qdCount, ushort anCount, ushort nsCount = 0, ushort arCount = 0)
    {
        new NbnsHeader(transactionId, flags, qdCount, anCount, nsCount, arCount).Write(_buffer[_pos..]);
        _pos += NbnsHeader.Size;
    }

    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_buffer[_pos..], value);
        _pos += 2;
    }

    public void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_buffer[_pos..], value);
        _pos += 4;
    }

    public void WriteName(NameKey name) => _pos += NbnsName.Encode(name, _buffer[_pos..]);

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_buffer[_pos..]);
        _pos += bytes.Length;
    }

    public void WriteQuestion(NameKey name, ushort type)
    {
        WriteName(name);
        WriteUInt16(type);
        WriteUInt16(NbnsType.ClassIn);
    }

    public void WriteRecordHeader(NameKey name, ushort type, uint ttl, ushort rdLength)
    {
        WriteName(name);
        WriteUInt16(type);
        WriteUInt16(NbnsType.ClassIn);
        WriteUInt32(ttl);
        WriteUInt16(rdLength);
    }
}

/// <summary>Builders for every packet the server emits. All write into a caller-supplied span.</summary>
public static class NbnsPackets
{
    /// <summary>Most addresses returned for one name (WINS caps internet groups at 25 members).</summary>
    public const int MaxAddresses = 25;

    /// <summary>
    /// A response carrying one NB record: positive name query response, or a positive/negative
    /// registration / refresh / release response (the opcode mirrors the request).
    /// </summary>
    public static int WriteNameResponse(Span<byte> destination, ushort transactionId, NbnsOpcode opcode,
        NbnsRcode rcode, bool recursionDesired, NameKey name, uint ttl, ushort nbFlags, ReadOnlySpan<uint> addresses)
    {
        ushort nm = (ushort)(NbnsFlags.AuthoritativeAnswer | NbnsFlags.RecursionAvailable
                             | (recursionDesired ? NbnsFlags.RecursionDesired : 0));
        if (addresses.Length > MaxAddresses) addresses = addresses[..MaxAddresses];

        var w = new NbnsPacketWriter(destination);
        w.WriteHeader(transactionId, NbnsHeader.MakeFlags(true, opcode, nm, rcode), qdCount: 0, anCount: 1);
        w.WriteRecordHeader(name, NbnsType.Nb, ttl, (ushort)(addresses.Length * 6));
        foreach (uint address in addresses)
        {
            w.WriteUInt16(nbFlags);
            w.WriteUInt32(address);
        }
        return w.Length;
    }

    /// <summary>
    /// Node status response (RFC 1002 §4.2.18) - the answer to <c>nbtstat -A</c>. The RDATA (name
    /// table + statistics) is prepared in advance by the caller and only copied here.
    /// </summary>
    public static int WriteNodeStatusResponse(Span<byte> destination, ushort transactionId, NameKey name, ReadOnlySpan<byte> rdata)
    {
        var w = new NbnsPacketWriter(destination);
        w.WriteHeader(transactionId,
            NbnsHeader.MakeFlags(true, NbnsOpcode.Query, NbnsFlags.AuthoritativeAnswer, NbnsRcode.None), qdCount: 0, anCount: 1);
        w.WriteRecordHeader(name, NbnsType.NbStat, ttl: 0, (ushort)rdata.Length);
        w.WriteBytes(rdata);
        return w.Length;
    }

    /// <summary>Negative name query response: RCODE set, one NULL record with empty RDATA.</summary>
    public static int WriteNegativeQueryResponse(Span<byte> destination, ushort transactionId,
        bool recursionDesired, NameKey name, NbnsRcode rcode)
    {
        ushort nm = (ushort)(NbnsFlags.AuthoritativeAnswer | NbnsFlags.RecursionAvailable
                             | (recursionDesired ? NbnsFlags.RecursionDesired : 0));

        var w = new NbnsPacketWriter(destination);
        w.WriteHeader(transactionId, NbnsHeader.MakeFlags(true, NbnsOpcode.Query, nm, rcode), qdCount: 0, anCount: 1);
        w.WriteRecordHeader(name, NbnsType.Null, ttl: 0, rdLength: 0);
        return w.Length;
    }

    /// <summary>
    /// WACK ("wait for acknowledgement"): tells a registering client to keep waiting
    /// <paramref name="waitSeconds"/> while the current owner is challenged. RDATA echoes the
    /// flags word of the request being held.
    /// </summary>
    public static int WriteWack(Span<byte> destination, ushort transactionId, NameKey name, uint waitSeconds, ushort requestFlags)
    {
        var w = new NbnsPacketWriter(destination);
        w.WriteHeader(transactionId,
            NbnsHeader.MakeFlags(true, NbnsOpcode.Wack, NbnsFlags.AuthoritativeAnswer, NbnsRcode.None), qdCount: 0, anCount: 1);
        w.WriteRecordHeader(name, NbnsType.Nb, waitSeconds, rdLength: 2);
        w.WriteUInt16(requestFlags);
        return w.Length;
    }

    /// <summary>
    /// A unicast name query: sent to a name's current owner to challenge it (no recursion), or to
    /// a partner name server to resolve a name (<paramref name="recursionDesired"/>, as a client would).
    /// </summary>
    public static int WriteNameQuery(Span<byte> destination, ushort transactionId, NameKey name, bool recursionDesired = false)
    {
        var w = new NbnsPacketWriter(destination);
        ushort nm = recursionDesired ? NbnsFlags.RecursionDesired : (ushort)0;
        w.WriteHeader(transactionId, NbnsHeader.MakeFlags(false, NbnsOpcode.Query, nm, NbnsRcode.None), qdCount: 1, anCount: 0);
        w.WriteQuestion(name, NbnsType.Nb);
        return w.Length;
    }
}
