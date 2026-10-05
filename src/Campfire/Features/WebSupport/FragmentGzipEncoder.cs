using System.IO.Compression;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Campfire.Contracts;

namespace Campfire.Features.WebSupport;

/// <summary>
/// Encodes HTML segments as one gzip member, without retaining request-specific
/// bytes. Cacheable memory owners must be entirely shareable/token-free and remain
/// immutable for their entire lifetime;
/// all other segment bytes must remain unchanged for the duration of TryEncode.
/// </summary>
public sealed partial class FragmentGzipEncoder
{
    private readonly object cacheLock = new();
    private readonly ConcurrentDictionary<FragmentKey, CacheEntry[]> cache = new();
    private readonly Queue<CacheEntry> fifo = [];
    private readonly long maxCacheBytes;
    private readonly int maxCacheEntries;
    private readonly int minimumBytes;
    private bool nativeUnavailable;
    private long cacheBytes;
    private int cacheEntries;
    private int nativeLogged;
    private int cacheLogged;
    private int planLogged;

    public FragmentGzipEncoder() : this(64 * 1024 * 1024, 4096, 1024) { }
    public FragmentGzipEncoder(long maxCacheBytes, int maxCacheEntries, int minimumBytes, bool useNative = true)
    {
        if (maxCacheBytes < 0 || maxCacheEntries < 0 || minimumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxCacheBytes));
        this.maxCacheBytes = maxCacheBytes;
        this.maxCacheEntries = maxCacheEntries;
        this.minimumBytes = minimumBytes;
        nativeUnavailable = !useNative;
    }

    public int CachedEntries => Volatile.Read(ref cacheEntries) + Volatile.Read(ref planEntries);
    public int CachedPlanEntries => Volatile.Read(ref planEntries);
    public long CachedBytes { get { lock (cacheLock) return cacheBytes + planBytes; } }
    public bool NativeAvailable => !Volatile.Read(ref nativeUnavailable);

    public bool TryEncode(IReadOnlyList<HtmlSegment> segments, out byte[] gzip)
    {
        long length = 0;
        foreach (var segment in segments) length = checked(length + segment.Bytes.Length);
        if (length < minimumBytes || length > int.MaxValue)
        {
            gzip = [];
            return false;
        }
        if (!Volatile.Read(ref nativeUnavailable))
        {
            try
            {
                gzip = PlanEncode(segments) ?? Fragments(segments);
                if (Volatile.Read(ref nativeLogged) == 0 && Interlocked.CompareExchange(ref nativeLogged, 1, 0) == 0)
                    Console.Error.WriteLine("Campfire fragment gzip: native zlib");
                return true;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // Development platforms without the helper still encode the full
                // response with one managed gzip compressor, preserving the wire contract.
                Volatile.Write(ref nativeUnavailable, true);
            }
        }
        gzip = WholeResponse(segments);
        return true;
    }

    private byte[] Fragments(IReadOnlyList<HtmlSegment> segments)
    {
        using var output = new MemoryStream();
        output.Write([0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 3]);
        var history = new FragmentHistory();
        // This dictionary exists only during one encoding, and compares memory
        // owner identity plus offset/length, never the values of secret bytes.
        var local = new Dictionary<ArraySegment<byte>, LocalPiece>();
        var copies = new Dictionary<(int Length, int Distance), byte[]>();
        long position = 0;
        uint crc = 0;
        uint length = 0;
        foreach (var segment in segments)
        {
            if (segment.Bytes.IsEmpty) continue;
            FragmentPiece piece;
            if (segment.Cacheable) piece = CachedPiece(segment.Bytes, history.Slices, history.Fingerprint);
            else
            {
                var isArray = MemoryMarshal.TryGetArray(segment.Bytes, out var source);
                if (isArray && local.TryGetValue(source, out var prior) &&
                    segment.Bytes.Length >= 3 && position - prior.Position >= segment.Bytes.Length && position - prior.Position <= 32768)
                {
                    var key = (segment.Bytes.Length, (int)(position - prior.Position));
                    if (!copies.TryGetValue(key, out var reference))
                        copies.Add(key, reference = FragmentBackreference.Encode(key.Item1, key.Item2));
                    // A block of copy commands contains only lengths/distances.
                    // Reuse it only inside this request; CRC remains the earlier
                    // identical request-local slice's checksum.
                    piece = new(reference, prior.Crc, prior.Shift);
                }
                else
                    piece = FragmentDeflate.Encode(segment.Bytes,
                        segment.Bytes.Span.Contains((byte)0) ? [] : FragmentHistory.Dictionary(history.Slices), 1);
                if (isArray) local[source] = new(position, piece.Crc, piece.Shift);
            }
            output.Write(piece.Deflate);
            crc = FragmentCrc.Combine(crc, piece);
            length = unchecked(length + (uint)segment.Bytes.Length);
            position += segment.Bytes.Length;
            history.Append(segment.Bytes, segment.Cacheable);
        }
        output.Write([3, 0]); // One final, empty fixed-Huffman block.
        Span<byte> trailer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, crc);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], length);
        output.Write(trailer);
        return output.ToArray();
    }

    private FragmentPiece CachedPiece(ReadOnlyMemory<byte> current, ReadOnlySpan<HistorySlice> history, int fingerprint)
    {
        if (!MemoryMarshal.TryGetArray(current, out var source))
            return FragmentDeflate.Encode(current, current.Span.Contains((byte)0) ? [] : FragmentHistory.Dictionary(history), 6);
        var key = new FragmentKey(source, fingerprint, history.Length);
        // A fingerprint only chooses a bucket. Compare the exact descriptor
        // sequence on every hit, so collisions cannot select a wrong dictionary.
        if (Find(key, history) is { } hit)
        {
            var piece = hit.Piece.Value;
            if (Volatile.Read(ref cacheLogged) == 0 && Interlocked.CompareExchange(ref cacheLogged, 1, 0) == 0)
                Console.Error.WriteLine("Campfire fragment gzip: cache reuse active");
            return piece;
        }
        // NUL would invalidate the masked-history proof. Such input is never
        // cached and uses a fresh raw compressor without a dictionary.
        if (current.Span.Contains((byte)0)) return FragmentDeflate.Encode(current, [], 6);
        // Count full owners conservatively: slices retain the complete immutable
        // arrays. Deduplicate owners inside a single entry, but charge each entry
        // for them independently. Compression reserves its output bound up front.
        var owners = new HashSet<byte[]>(ReferenceEqualityComparer.Instance) { source.Array! };
        long cost = current.Length + current.Length / 8 + 512L + history.Length * 32L;
        foreach (var slice in history) if (slice.Known.Array is { } owner) owners.Add(owner);
        foreach (var owner in owners) cost = checked(cost + owner.LongLength);
        if (cost > FragmentByteLimit || FragmentEntryLimit == 0) return FragmentDeflate.Encode(current, FragmentHistory.Dictionary(history), 6);
        var snapshot = history.ToArray(); // Only misses allocate retained descriptors.
        Lazy<FragmentPiece> lazy;
        lock (cacheLock)
        {
            if (Find(key, history) is { } existing) lazy = existing.Piece;
            else
            {
                while (cacheEntries >= FragmentEntryLimit || cacheBytes + cost > FragmentByteLimit)
                {
                    var old = fifo.Dequeue();
                    var oldBucket = cache[old.Key];
                    if (oldBucket.Length == 1) cache.TryRemove(old.Key, out _);
                    else cache[old.Key] = oldBucket.Where(entry => !ReferenceEquals(entry, old)).ToArray();
                    cacheBytes -= old.Cost;
                    cacheEntries--;
                }
                lazy = new(() => FragmentDeflate.Encode(current, FragmentHistory.Dictionary(snapshot), 6), LazyThreadSafetyMode.ExecutionAndPublication);
                var entry = new CacheEntry(key, snapshot, lazy, cost);
                cache[key] = cache.TryGetValue(key, out var bucket) ? [.. bucket, entry] : [entry];
                fifo.Enqueue(entry);
                cacheBytes += cost;
                cacheEntries++;
            }
        }
        return lazy.Value;
    }

    private CacheEntry? Find(FragmentKey key, ReadOnlySpan<HistorySlice> history)
    {
        if (cache.TryGetValue(key, out var bucket))
            foreach (var entry in bucket)
            {
                if (history.Length != entry.History.Length) continue;
                var equal = true;
                for (var index = 0; index < history.Length; index++)
                {
                    var left = history[index];
                    var right = entry.History[index];
                    // ArraySegment's generic equality path boxes value types.
                    // Compare exact owner identity and ranges explicitly instead:
                    // this also keeps the fingerprint-collision proof visible.
                    if (left.Length != right.Length || !ReferenceEquals(left.Known.Array, right.Known.Array) ||
                        left.Known.Offset != right.Known.Offset || left.Known.Count != right.Known.Count)
                    {
                        equal = false;
                        break;
                    }
                }
                if (equal) return entry;
            }
        return null;
    }

    private readonly record struct FragmentKey(ArraySegment<byte> Current, int HistoryHash, int HistoryCount);
    private sealed record CacheEntry(FragmentKey Key, HistorySlice[] History, Lazy<FragmentPiece> Piece, long Cost);
    private readonly record struct LocalPiece(long Position, uint Crc, uint Shift);

    private static byte[] WholeResponse(IReadOnlyList<HtmlSegment> segments)
    {
        using var output = new MemoryStream();
        using (var compressor = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            foreach (var segment in segments) compressor.Write(segment.Bytes.Span);
        return output.ToArray();
    }
}
