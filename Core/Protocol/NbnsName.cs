using System.Runtime.CompilerServices;
using WinsAlt.Core.Domain;

namespace WinsAlt.Core.Protocol;

/// <summary>
/// RFC 1001 §14.1 first-level encoding: each of the 16 name bytes is split into two nibbles and
/// each nibble is sent as 'A' + nibble, giving one 32-byte label (length byte 0x20), optionally
/// followed by NetBIOS scope labels, then a zero terminator.
/// </summary>
public static class NbnsName
{
    /// <summary>Encoded size of a name with no scope: length byte + 32 chars + terminator.</summary>
    public const int EncodedLength = 34;

    private const byte LabelLength = 0x20;
    private const byte PointerMask = 0xC0;

    /// <summary>
    /// Decodes the name at <paramref name="offset"/>. A DNS-style compression pointer (0xC0xx) is
    /// followed once, and only backwards, so a crafted packet cannot loop the parser.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> packet, int offset, out NameKey name, out int consumed, out bool hasScope)
    {
        name = default;
        consumed = 0;
        hasScope = false;
        if ((uint)offset >= (uint)packet.Length) return false;

        byte first = packet[offset];
        if ((first & PointerMask) == PointerMask)
        {
            if (offset + 1 >= packet.Length) return false;
            int target = ((first & 0x3F) << 8) | packet[offset + 1];
            if (target >= offset) return false;
            if (!TryDecodeLabels(packet, target, out name, out _, out hasScope)) return false;
            consumed = 2;
            return true;
        }

        return TryDecodeLabels(packet, offset, out name, out consumed, out hasScope);
    }

    private static bool TryDecodeLabels(ReadOnlySpan<byte> packet, int offset, out NameKey name, out int consumed, out bool hasScope)
    {
        name = default;
        consumed = 0;
        hasScope = false;

        // length byte + 32 encoded chars + at least the terminator
        if (packet.Length - offset < EncodedLength || packet[offset] != LabelLength) return false;

        // 32 encoded chars -> 32 nibbles -> two big-endian ulongs, validated as they are folded in.
        // One bounds check for the whole label (the slice), none inside the loops.
        ReadOnlySpan<byte> encoded = packet.Slice(offset + 1, 32);
        if (!TryFold(encoded[..16], out ulong high) || !TryFold(encoded[16..], out ulong low)) return false;

        int pos = offset + 33;
        while (true)
        {
            if (pos >= packet.Length) return false;
            byte len = packet[pos];
            if (len == 0) { pos++; break; }
            if ((len & PointerMask) != 0) return false;
            hasScope = true;
            pos += 1 + len;
        }

        name = new NameKey(high, low);
        consumed = pos - offset;
        return true;
    }

    /// <summary>Folds 16 first-level-encoded chars ('A'..'P', one nibble each) into 8 bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryFold(ReadOnlySpan<byte> sixteen, out ulong value)
    {
        value = 0;
        uint invalid = 0;
        for (int i = 0; i < sixteen.Length; i++)
        {
            uint nibble = (uint)(sixteen[i] - 'A');
            invalid |= nibble;
            value = (value << 4) | (nibble & 0x0F);
        }
        return invalid <= 0x0F; // any char outside 'A'..'P' sets a bit above the low nibble
    }

    /// <summary>Writes the 34-byte encoded form (no scope). Returns the number of bytes written.</summary>
    public static int Encode(NameKey name, Span<byte> destination)
    {
        Span<byte> label = destination[..EncodedLength]; // one bounds check for the whole name
        label[0] = LabelLength;
        ulong high = name.Hi, low = name.Lo;
        for (int i = 0; i < 16; i++)
        {
            int shift = 60 - i * 4;
            label[1 + i] = (byte)('A' + (int)((high >> shift) & 0x0F));
            label[17 + i] = (byte)('A' + (int)((low >> shift) & 0x0F));
        }
        label[33] = 0;
        return EncodedLength;
    }
}
