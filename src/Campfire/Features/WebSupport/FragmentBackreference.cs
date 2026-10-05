namespace Campfire.Features.WebSupport;

/// <summary>Request-local copies of an earlier, identical, non-overlapping segment.</summary>
internal static class FragmentBackreference
{
    private static ReadOnlySpan<int> LengthBase => [3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258];
    private static ReadOnlySpan<int> LengthExtra => [0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,0];
    private static ReadOnlySpan<int> DistanceBase => [1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577];
    private static ReadOnlySpan<int> DistanceExtra => [0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13];

    internal static byte[] Encode(int length, int distance)
    {
        if (length < 3 || length > distance || distance > 32768) throw new ArgumentOutOfRangeException(nameof(distance));
        var writer = new Bits();
        writer.Put(2, 3); // Non-final fixed Huffman block, BTYPE=01.
        while (length != 0)
        {
            var count = Math.Min(length, 258);
            if (length - count is 1 or 2) count -= 3;
            var code = FindCode(LengthBase, count);
            writer.Symbol(257 + code);
            writer.Put(count - LengthBase[code], LengthExtra[code]);
            code = FindCode(DistanceBase, distance);
            writer.Reversed(code, 5);
            writer.Put(distance - DistanceBase[code], DistanceExtra[code]);
            length -= count;
        }
        writer.Symbol(256); // End of block.
        writer.Put(0, 3); // Non-final empty stored block, followed by byte padding.
        writer.Align();
        writer.Append([0, 0, 255, 255]);
        return writer.ToArray();
    }

    private static int FindCode(ReadOnlySpan<int> bases, int value)
    {
        var code = bases.Length - 1;
        while (bases[code] > value) code--;
        return code;
    }

    private sealed class Bits
    {
        private readonly List<byte> bytes = [];
        private uint word;
        private int count;
        internal void Put(int value, int bits)
        {
            word |= (uint)value << count;
            count += bits;
            while (count >= 8) { bytes.Add((byte)word); word >>= 8; count -= 8; }
        }
        internal void Reversed(int value, int bits)
        {
            var reverse = 0;
            for (var bit = 0; bit < bits; bit++) { reverse = (reverse << 1) | (value & 1); value >>= 1; }
            Put(reverse, bits);
        }
        internal void Symbol(int symbol)
        {
            if (symbol <= 143) Reversed(0x30 + symbol, 8);
            else if (symbol <= 255) Reversed(0x190 + symbol - 144, 9);
            else if (symbol <= 279) Reversed(symbol - 256, 7);
            else Reversed(0xc0 + symbol - 280, 8);
        }
        internal void Align() { if (count != 0) Put(0, 8 - count); }
        internal void Append(ReadOnlySpan<byte> data) { foreach (var value in data) bytes.Add(value); }
        internal byte[] ToArray() => bytes.ToArray();
    }
}
