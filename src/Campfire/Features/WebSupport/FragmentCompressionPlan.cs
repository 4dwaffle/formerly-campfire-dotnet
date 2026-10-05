using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Campfire.Contracts;

namespace Campfire.Features.WebSupport;

public sealed partial class FragmentGzipEncoder
{
    // Only stable byte owners/ranges, dynamic lengths and alias positions survive
    // a call. No dynamic owner, value, checksum or encoded dynamic bytes are kept.
    // Both caches carve their budgets from the one configured total.
    private readonly ConcurrentDictionary<int, CompressionPlan[]> plans = new();
    private readonly Queue<CompressionPlan> planFifo = [];
    private long planBytes;
    private int planEntries;
    private int PlanEntryLimit => Math.Min(128, maxCacheEntries / 4);
    private long PlanByteLimit => PlanEntryLimit == 0 ? 0 : maxCacheBytes / 4;
    private int FragmentEntryLimit => maxCacheEntries - PlanEntryLimit;
    private long FragmentByteLimit => maxCacheBytes - PlanByteLimit;
    private byte[]? PlanEncode(IReadOnlyList<HtmlSegment> segments)
    {
        if (segments.Count < 16 || PlanByteLimit < 4096 || PlanEntryLimit == 0) return null;
        var hash = 17;
        foreach (var segment in segments)
        {
            if (!MemoryMarshal.TryGetArray(segment.Bytes, out var source)) return null;
            hash = unchecked(hash * 31 + segment.Bytes.Length);
            if (segment.Cacheable)
            {
                hash = unchecked(hash * 31 + (source.Array is null ? 0 : RuntimeHelpers.GetHashCode(source.Array)));
                hash = unchecked(hash * 31 + source.Offset);
            }
            else hash = unchecked(hash * 31 - 1);
        }
        if (plans.TryGetValue(hash, out var bucket))
            foreach (var plan in bucket)
                if (plan.Matches(segments))
                {
                    if (Volatile.Read(ref cacheLogged) == 0 && Interlocked.CompareExchange(ref cacheLogged, 1, 0) == 0)
                        Console.Error.WriteLine("Campfire fragment gzip: cache reuse active");
                    if (Volatile.Read(ref planLogged) == 0 && Interlocked.CompareExchange(ref planLogged, 1, 0) == 0)
                        Console.Error.WriteLine("Campfire fragment gzip: assembly plan reuse active");
                    return plan.Encode(segments);
                }
        var created = BuildPlan(segments, hash);
        if (created is null) return null;
        if (created.Cost <= PlanByteLimit)
            lock (cacheLock)
            {
                var found = false;
                if (plans.TryGetValue(hash, out bucket))
                    foreach (var prior in bucket)
                        if (prior.Matches(segments)) { created = prior; found = true; break; }
                if (!found)
                {
                    while (planEntries >= PlanEntryLimit || planBytes + created.Cost > PlanByteLimit)
                    {
                        var old = planFifo.Dequeue();
                        var oldBucket = plans[old.Hash];
                        if (oldBucket.Length == 1) plans.TryRemove(old.Hash, out _);
                        else plans[old.Hash] = oldBucket.Where(entry => !ReferenceEquals(entry, old)).ToArray();
                        planBytes -= old.Cost;
                        planEntries--;
                    }
                    plans[hash] = plans.TryGetValue(hash, out bucket) ? [..bucket, created] : [created];
                    planFifo.Enqueue(created); planBytes += created.Cost; planEntries++;
                }
            }
        return created.Encode(segments);
    }

    private CompressionPlan? BuildPlan(IReadOnlyList<HtmlSegment> segments, int hash)
    {
        var descriptors = new PlanDescriptor[segments.Count];
        var dynamicGroups = new Dictionary<ArraySegment<byte>, int>();
        var groups = new List<PlanGroup>();
        var operations = new List<PlanOperation>();
        var history = new FragmentHistory();
        var priorPositions = new Dictionary<int, long>();
        var knownOwners = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        using var staticRun = new MemoryStream();
        long total = 0; foreach (var segment in segments) total += segment.Bytes.Length;
        long position = 0;
        uint staticCrc = 0;
        // CRC concatenation is linear: each segment's CRC is multiplied by
        // the shift operator for all bytes after it, then XORed. Constant
        // pieces contribute once here. Repeated dynamic aliases XOR those
        // operators into one coefficient evaluated with their fresh CRC.
        // Thus a warm room folds a few dynamic groups instead of 686 pieces.
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            MemoryMarshal.TryGetArray(segment.Bytes, out var source);
            if (segment.Cacheable)
            {
                if (segment.Bytes.Span.Contains((byte)0)) return null;
                if (source.Array is { } owner) knownOwners.Add(owner);
                descriptors[index] = new(source, source.Count, -1);
                if (!segment.Bytes.IsEmpty)
                {
                    var piece = CachedPiece(segment.Bytes, history.Slices, history.Fingerprint);
                    staticRun.Write(piece.Deflate);
                    staticCrc ^= FragmentCrc.Multiply(piece.Crc, FragmentCrc.Shift(checked((int)(total - position - source.Count))));
                }
            }
            else if (!segment.Bytes.IsEmpty)
            {
                if (!dynamicGroups.TryGetValue(source, out var group))
                {
                    group = groups.Count; dynamicGroups.Add(source, group); groups.Add(new(index, 0));
                }
                var first = groups[group].First;
                descriptors[index] = new(default, source.Count, first);
                groups[group] = groups[group] with { Coefficient = groups[group].Coefficient ^ FragmentCrc.Shift(checked((int)(total-position-source.Count))) };
                if (priorPositions.TryGetValue(group, out var prior) && source.Count >= 3 && position-prior >= source.Count && position-prior <= 32768)
                    staticRun.Write(FragmentBackreference.Encode(source.Count, (int)(position-prior)));
                else
                {
                    if (staticRun.Length != 0) { operations.Add(new(staticRun.ToArray(), -1, -1)); staticRun.SetLength(0); }
                    operations.Add(new([], index, group));
                }
                priorPositions[group] = position;
            }
            else descriptors[index] = new(default, 0, index);
            history.Append(segment.Bytes, segment.Cacheable); position += segment.Bytes.Length;
        }
        if (staticRun.Length != 0) operations.Add(new(staticRun.ToArray(), -1, -1));
        long cost = 256 + descriptors.Length * 40L + groups.Count * 16L + operations.Count * 48L;
        foreach (var owner in knownOwners) cost += owner.LongLength;
        foreach (var op in operations) cost += op.Bytes.Length;
        return new(hash, descriptors, groups.ToArray(), operations.ToArray(), staticCrc, checked((uint)total), cost);
    }

    private readonly record struct PlanDescriptor(ArraySegment<byte> Known, int Length, int First);
    private readonly record struct PlanGroup(int First, uint Coefficient);
    private readonly record struct PlanOperation(byte[] Bytes, int Index, int Group);
    private sealed record CompressionPlan(int Hash, PlanDescriptor[] Descriptors, PlanGroup[] Groups, PlanOperation[] Operations, uint StaticCrc, uint Length, long Cost)
    {
        internal bool Matches(IReadOnlyList<HtmlSegment> segments)
        {
            if (segments.Count != Descriptors.Length) return false;
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index]; var descriptor = Descriptors[index];
                if (segment.Bytes.Length != descriptor.Length || segment.Cacheable != (descriptor.First == -1)) return false;
                MemoryMarshal.TryGetArray(segment.Bytes, out var current);
                if (descriptor.First == -1)
                {
                    if (!Same(current, descriptor.Known)) return false;
                }
                else if (descriptor.First != index && descriptor.Length != 0)
                {
                    // Copies may refer to another dynamic slot only when the
                    // current request has that exact same owner and range.
                    // Distinct old groups may merge safely: their coefficients
                    // still use current bytes; splitting requires a new plan.
                    MemoryMarshal.TryGetArray(segments[descriptor.First].Bytes, out var first);
                    if (!Same(current, first)) return false;
                }
            }
            return true;
        }
        private static bool Same(ArraySegment<byte> left, ArraySegment<byte> right) => ReferenceEquals(left.Array,right.Array) && left.Offset==right.Offset && left.Count==right.Count;
        internal byte[] Encode(IReadOnlyList<HtmlSegment> segments)
        {
            var checksums = new uint[Groups.Length];
            var pieces = new byte[Operations.Length][];
            var length = 20;
            var operationIndex = 0;
            foreach (var operation in Operations)
            {
                if (operation.Index < 0) pieces[operationIndex] = operation.Bytes;
                else
                {
                    var input = segments[operation.Index].Bytes;
                    var piece = FragmentDeflate.Encode(input, [], 1);
                    pieces[operationIndex] = piece.Deflate; checksums[operation.Group] = piece.Crc;
                }
                length = checked(length + pieces[operationIndex++].Length);
            }
            var crc = StaticCrc;
            for (var index = 0; index < Groups.Length; index++) crc ^= FragmentCrc.Multiply(checksums[index], Groups[index].Coefficient);
            var output = GC.AllocateUninitializedArray<byte>(length);
            ReadOnlySpan<byte> header = [0x1f,0x8b,8,0,0,0,0,0,0,3]; header.CopyTo(output);
            var position = 10;
            foreach (var piece in pieces) { piece.CopyTo(output, position); position += piece.Length; }
            output[position++] = 3; output[position++] = 0;
            var trailer = output.AsSpan(position,8);
            BinaryPrimitives.WriteUInt32LittleEndian(trailer,crc); BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..],Length);
            return output;
        }
    }
}
