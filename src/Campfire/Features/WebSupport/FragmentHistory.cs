using System.Runtime.InteropServices;

namespace Campfire.Features.WebSupport;

/// <summary>The last 32KiB of known immutable bytes and unknown byte lengths.</summary>
internal sealed class FragmentHistory
{
    private readonly List<HistorySlice> slices = [];
    private int start;
    private int length;
    private int fingerprint;
    internal ReadOnlySpan<HistorySlice> Slices => CollectionsMarshal.AsSpan(slices)[start..];
    // An order-independent rolling sum chooses a cache bucket only. The encoder
    // compares every ordered descriptor exactly before reusing compressed bytes.
    internal int Fingerprint => fingerprint;

    internal void Append(ReadOnlyMemory<byte> bytes, bool cacheable)
    {
        if (bytes.IsEmpty) return;
        ArraySegment<byte> source = default;
        var known = cacheable && MemoryMarshal.TryGetArray(bytes, out source);
        if (!known) source = default;
        if (bytes.Length >= 32768)
        {
            slices.Clear();
            start = 0;
            length = 32768;
            var tail = new HistorySlice(known ? new ArraySegment<byte>(source.Array!, source.Offset + source.Count - 32768, 32768) : default, 32768);
            slices.Add(tail);
            fingerprint = tail.GetHashCode();
            return;
        }
        if (!known && slices.Count > start && slices[^1].Known.Array is null)
        {
            var old = slices[^1];
            var merged = new HistorySlice(default, checked(old.Length + bytes.Length));
            slices[^1] = merged;
            fingerprint = unchecked(fingerprint - old.GetHashCode() + merged.GetHashCode());
        }
        else
        {
            var added = new HistorySlice(source, bytes.Length);
            slices.Add(added);
            fingerprint = unchecked(fingerprint + added.GetHashCode());
        }
        length += bytes.Length;
        var excess = length - 32768;
        while (excess > 0)
        {
            var first = slices[start];
            var remove = Math.Min(excess, first.Length);
            fingerprint = unchecked(fingerprint - first.GetHashCode());
            if (remove == first.Length) start++;
            else
            {
                var remaining = first.Known.Array is null ? default : new ArraySegment<byte>(first.Known.Array, first.Known.Offset + remove, first.Length - remove);
                var trimmed = new HistorySlice(remaining, first.Length - remove);
                slices[start] = trimmed;
                fingerprint = unchecked(fingerprint + trimmed.GetHashCode());
            }
            excess -= remove;
            length -= remove;
        }
        if (start > 256) { slices.RemoveRange(0, start); start = 0; }
    }

    internal static byte[] Dictionary(ReadOnlySpan<HistorySlice> slices)
    {
        var length = 0;
        var anyKnown = false;
        foreach (var slice in slices) { length += slice.Length; anyKnown |= slice.Known.Array is not null; }
        if (!anyKnown) return [];
        var dictionary = new byte[length];
        var offset = 0;
        foreach (var slice in slices)
        {
            if (slice.Known.Array is not null) slice.Known.AsSpan().CopyTo(dictionary.AsSpan(offset));
            offset += slice.Length;
        }
        // Unknown bytes remain NUL. Only NUL-free current input can use the
        // dictionary: every match is then entirely known, at the same distance
        // in the decoder's real history. No actual request bytes enter it.
        return dictionary;
    }
}

internal readonly record struct HistorySlice(ArraySegment<byte> Known, int Length);
