using System.IO.Compression;
using System.Text;
using Campfire.Contracts;
using Campfire.Features.WebSupport;
using Xunit;

namespace Campfire.SystemTests;

public sealed class FragmentGzipTests
{
    public static IEnumerable<object[]> CopyBoundaries()
    {
        var cases = new HashSet<(int Length, int Distance)>();
        foreach (var length in new[] { 3,4,10,11,18,19,34,35,130,131,257,258,259,260,261,516,517,518,519,1000,32768 })
            foreach (var distance in new[] { length, Math.Min(32768, length + 31), 32768 }.Distinct())
                cases.Add((length, distance));
        foreach (var length in new[] { 3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258 })
            foreach (var boundary in new[] { length, length + 1 }.Where(value => value <= 258))
                cases.Add((boundary, 32768));
        foreach (var distance in new[] { 3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577,32768 })
            foreach (var boundary in new[] { distance - 1, distance, distance + 1 }.Where(value => value is >= 3 and <= 32768))
                cases.Add((3, boundary));
        foreach (var (length, distance) in cases) yield return [length, distance];
    }
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static byte[] Decode(byte[] gzip)
    {
        using var input = new MemoryStream(gzip);
        using var decoder = new GZipStream(input, CompressionMode.Decompress);
        using var result = new MemoryStream();
        decoder.CopyTo(result);
        return result.ToArray();
    }
    private static byte[] Encode(FragmentGzipEncoder encoder, IReadOnlyList<HtmlSegment> segments)
    {
        Assert.True(encoder.TryEncode(segments, out var gzip));
        Assert.Equal(segments.SelectMany(s => s.Bytes.ToArray()).ToArray(), Decode(gzip));
        if (Environment.GetEnvironmentVariable("CAMPFIRE_REQUIRE_NATIVE_COMPRESSION") == "1") Assert.True(encoder.NativeAvailable);
        return gzip;
    }

    [Fact]
    public void ManagedFallbackAndSmallResponsePreserveDecodedBytes()
    {
        var encoder = new FragmentGzipEncoder(1024, 2, 1024, useNative: false);
        Assert.False(encoder.TryEncode([new(Bytes("small"))], out var small));
        Assert.Empty(small);
        var segments = new HtmlSegment[] { new(Bytes(string.Concat(Enumerable.Repeat("雪 ⛄ 😀 naïve\r\n", 200)))) };
        Assert.True(encoder.TryEncode(segments, out var gzip));
        Assert.Equal(segments[0].Bytes.ToArray(), Decode(gzip));
        Assert.Equal(0, encoder.CachedEntries);
    }

    [Fact]
    public void FreshSameLengthSecretsReuseOnlyImmutablePieces()
    {
        var encoder = new FragmentGzipEncoder(2_000_000, 128, 0);
        var before = Bytes(string.Concat(Enumerable.Repeat("<form class=\"popup unicode雪\">", 20)));
        var after = Bytes("<p>popup unicode雪</p></form>" + new string('q', 200));
        Encode(encoder, [new(before, true), new(Bytes(new string('x', 128))), new(after, true)]);
        var entries = encoder.CachedEntries;
        var second = Encode(encoder, [new(before, true), new(Bytes(new string('y', 128))), new(after, true)]);
        Assert.Equal(entries, encoder.CachedEntries);
        Assert.DoesNotContain((byte)'x', Decode(second));
        Encode(encoder, [new(before, true), new(Bytes(new string('z', 129))), new(after, true)]);
        if (encoder.NativeAvailable) Assert.True(encoder.CachedEntries > entries);
    }

    [Fact]
    public void NulInputNeverUsesMaskedDictionaryOrGlobalCompressionCache()
    {
        var encoder = new FragmentGzipEncoder(2_000_000, 128, 0);
        var before = Bytes("cached-known-history" + new string('h', 500));
        var after = Bytes("cached-known-history\0secret-tail" + new string('h', 500));
        Encode(encoder, [new(before, true), new(Bytes("real unknown bytes")), new(after, true)]);
        if (encoder.NativeAvailable) Assert.Equal(1, encoder.CachedEntries);
    }

    [Fact]
    public void NewImmutableVersionDoesNotReuseOlderFragment()
    {
        var encoder = new FragmentGzipEncoder(1_000_000, 128, 0);
        var initial = Bytes("<article>old text " + new string('m', 400) + "</article>");
        Encode(encoder, [new(initial, true)]);
        var updated = initial.ToArray();
        Bytes("new").CopyTo(updated, 9);
        var output = Encode(encoder, [new(updated, true)]);
        Assert.Contains("new", Encoding.UTF8.GetString(Decode(output)));
        if (encoder.NativeAvailable) Assert.Equal(2, encoder.CachedEntries);
    }

    [Fact]
    public void OwnerIdentityAndSliceRangesAreDistinctCacheInputs()
    {
        var encoder = new FragmentGzipEncoder(1_000_000, 128, 0);
        var owner = Bytes("<p>first</p><p>second</p>");
        Encode(encoder, [new(owner.AsMemory(0, 12), true), new(owner.AsMemory(12), true)]);
        Encode(encoder, [new(owner.AsMemory(12), true), new(owner.AsMemory(0, 12), true)]);
        if (encoder.NativeAvailable) Assert.Equal(4, encoder.CachedEntries);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(32767, 1)]
    [InlineData(32768, 32767)]
    [InlineData(32769, 32768)]
    [InlineData(65536, 32769)]
    public void MaskedHistoryMatchesActualDecoderAtWindowBoundaries(int knownLength, int gapLength)
    {
        var encoder = new FragmentGzipEncoder(2_000_000, 128, 0);
        var known = Enumerable.Range(0, knownLength).Select(i => (byte)(i % 254 + 1)).ToArray();
        var input = known.AsMemory(Math.Max(0, known.Length - 500)).ToArray();
        Encode(encoder, [new(known, true), new(Enumerable.Repeat((byte)'x', gapLength).ToArray()), new(input, true)]);
        Encode(encoder, [new(known, true), new(Enumerable.Repeat((byte)'y', gapLength).ToArray()), new(input, true)]);
    }

    [Fact]
    public void ManyFragmentsCombineCrcAndLengthAcrossUnicodeAndEmptySegments()
    {
        var encoder = new FragmentGzipEncoder(8_000_000, 1024, 0);
        var token = Bytes("<input value=\"雪request-local-secret😀\">");
        var segments = Enumerable.Range(0, 400).SelectMany(i => new HtmlSegment[]
            {new(Bytes($"<form id=\"{i}\">☃ emoji😀</form>"),true),new(token),new(ReadOnlyMemory<byte>.Empty,true)}).ToArray();
        Encode(encoder, segments);
        Encode(encoder, segments);
    }

    [Fact]
    public async Task ConcurrentRequestsNeverShareTheirDynamicBytes()
    {
        var encoder = new FragmentGzipEncoder(2_000_000, 128, 0);
        var immutable = Bytes("<form>shared stable markup雪" + new string('s', 300));
        await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            var token = Bytes($"<input value=\"request-{index:D4}-secret\">");
            var segments = Enumerable.Range(0, 50).SelectMany(_ => new HtmlSegment[] { new(immutable, true), new(token) }).ToArray();
            Encode(encoder, segments);
        })));
        Assert.True(encoder.CachedBytes <= 2_000_000);
        Assert.True(encoder.CachedEntries <= 128);
    }

    [Fact]
    public void EvictionCountsFullRetainedOwnersAndBoundsEntries()
    {
        var encoder = new FragmentGzipEncoder(16_000, 3, 0);
        for (var index = 0; index < 40; index++)
        {
            var owner = new byte[8000];
            Array.Fill(owner, (byte)(index % 20 + 'a'));
            Encode(encoder, [new(owner.AsMemory(100, 20), true)]);
            Assert.True(encoder.CachedBytes <= 16_000);
            Assert.True(encoder.CachedEntries <= 3);
        }
        if (encoder.NativeAvailable) Assert.Equal(1, encoder.CachedEntries);
    }

    [Theory]
    [MemberData(nameof(CopyBoundaries))]
    public void RequestLocalCopiesDecodeAtLengthCodeAndDistanceBoundaries(int length, int distance)
    {
        var encoder = new FragmentGzipEncoder(100_000, 16, 0);
        var token = Enumerable.Range(0, length).Select(i => (byte)(i * 17 % 255 + 1)).ToArray();
        var pad = Enumerable.Repeat((byte)'p', distance - length).ToArray();
        Encode(encoder, [new(token), new(pad, true), new(token)]);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(100, 32769)]
    [InlineData(32769, 32769)]
    public void IneligibleLocalCopyFallsBackToNativeCompression(int length, int distance)
    {
        var encoder = new FragmentGzipEncoder(100_000, 16, 0);
        var token = Enumerable.Range(0, length).Select(i => (byte)(i % 256)).ToArray();
        Encode(encoder, [new(token), new(new byte[distance - length], true), new(token)]);
    }

    [Fact]
    public void LocalCopiesRequireOwnerAndSliceIdentityRatherThanEqualSecretValues()
    {
        var encoder = new FragmentGzipEncoder(100_000, 16, 0);
        var token = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var same = Encode(encoder, [new(token), new(token)]);
        var distinct = Encode(encoder, [new(token), new(token.ToArray())]);
        if (encoder.NativeAvailable) Assert.True(distinct.Length > same.Length + 100);
    }

    [Fact]
    public void FullWindowHistoryPreservesMultipleKnownSlicesSeparatedByChangingSecrets()
    {
        var encoder = new FragmentGzipEncoder(1_000_000, 128, 0);
        var older = Bytes(new string('a', 1000) + "<p>older-known-unicode雪</p>");
        var newer = Bytes(new string('b', 1000) + "<form>newer-known</form>");
        var matchesOlder = older.AsMemory(700).ToArray();
        Encode(encoder, [new(older, true),new(Bytes("first secret")),new(newer, true),new(Bytes("secondsecret")),new(matchesOlder,true)]);
        var entries = encoder.CachedEntries;
        Encode(encoder, [new(older, true),new(Bytes("alter secret")),new(newer, true),new(Bytes("differenttxt")),new(matchesOlder,true)]);
        Assert.Equal(entries, encoder.CachedEntries);
    }

    [Fact]
    public void FingerprintCollisionFromReorderedHistoryCannotReuseWrongDeflateDictionary()
    {
        var encoder = new FragmentGzipEncoder(1_000_000, 128, 0);
        var first = Bytes("first-known-history:" + new string('a', 800));
        var second = Bytes("second-known-history:" + new string('b', 800));
        var target = first.Concat(second).ToArray();
        Encode(encoder, [new(first,true),new(second,true),new(target,true)]);
        // A rolling descriptor sum intentionally has the same fingerprint for
        // [first,second] and [second,first]. Exact ordered comparison is essential:
        // otherwise a copied DEFLATE match can silently read the wrong history.
        Encode(encoder, [new(second,true),new(first,true),new(target,true)]);
        Encode(encoder, [new(first,true),new(second,true),new(target,true)]);
        if (encoder.NativeAvailable) Assert.Equal(6, encoder.CachedEntries);
    }

    [Fact]
    public void AssemblyPlanReusesStablePiecesWithFreshUnicodeAndNulSecrets()
    {
        var encoder = new FragmentGzipEncoder(4_000_000, 128, 0);
        var stable = Enumerable.Range(0, 24).Select(index => Bytes($"<article id=\"{index}\">雪" + new string('s', 200) + "</article>")).ToArray();
        HtmlSegment[] Page(byte[] secret) => stable.SelectMany(owner => new HtmlSegment[] { new(owner, true), new(secret) }).ToArray();
        Encode(encoder, Page(Bytes("<input value=\"first-secret-雪\">")));
        var entries = encoder.CachedPlanEntries;
        if (encoder.NativeAvailable) Assert.Equal(1, entries);
        var fresh = Bytes("<input value=\"other-secret-雪\">");
        Encode(encoder, Page(fresh));
        Assert.Equal(entries, encoder.CachedPlanEntries);
        fresh = fresh.ToArray(); fresh[8] = 0;
        var actual = Decode(Encode(encoder, Page(fresh)));
        Assert.Contains((byte)0, actual);
        Assert.DoesNotContain("first-secret", Encoding.UTF8.GetString(actual));
        Assert.Equal(entries, encoder.CachedPlanEntries);
    }

    [Fact]
    public void AssemblyPlanValidatesDynamicAliasLayoutAndCrcContributions()
    {
        var encoder = new FragmentGzipEncoder(4_000_000, 128, 0);
        var stable = Bytes("<form>shared immutable markup" + new string('s', 400));
        var first = Bytes("<input value=\"AAAAAAAAAAAA\">");
        var second = Bytes("<input value=\"BBBBBBBBBBBB\">");
        var shared = Enumerable.Range(0, 24).SelectMany(_ => new HtmlSegment[] {new(stable,true),new(first)}).ToArray();
        Encode(encoder, shared);
        var distinct = Enumerable.Range(0, 24).SelectMany(index => new HtmlSegment[] {new(stable,true),new(index%2==0 ? first : second)}).ToArray();
        Encode(encoder, distinct); Encode(encoder, distinct);
        var freshFirst = Bytes("<input value=\"CCCCCCCCCCCC\">");
        var freshSecond = Bytes("<input value=\"DDDDDDDDDDDD\">");
        Encode(encoder, Enumerable.Range(0, 24).SelectMany(index => new HtmlSegment[] {new(stable,true),new(index%2==0 ? freshFirst : freshSecond)}).ToArray());
        // Merging previously distinct aliases is still correct: each CRC term
        // is evaluated from the current group bytes, even when they coincide.
        Encode(encoder, shared);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(259, 32768)]
    [InlineData(100, 32769)]
    [InlineData(32769, 32769)]
    public void AssemblyPlanHandlesOutOfWindowAndIneligibleDynamicCopies(int secretLength, int gapLength)
    {
        var encoder = new FragmentGzipEncoder(8_000_000, 256, 0);
        var secret = Enumerable.Range(0, secretLength).Select(index => (byte)(index%251+1)).ToArray();
        var gap = Enumerable.Repeat((byte)'g', gapLength).ToArray();
        var page = Enumerable.Range(0, 9).SelectMany(_ => new HtmlSegment[] {new(secret),new(gap,true)}).ToArray();
        Encode(encoder,page);
        secret = secret.ToArray(); Array.Fill(secret,(byte)'z');
        Encode(encoder,Enumerable.Range(0,9).SelectMany(_=>new HtmlSegment[]{new(secret),new(gap,true)}).ToArray());
    }

    [Fact]
    public void AssemblyPlanVersionsAndEvictionShareConfiguredCacheBudget()
    {
        var encoder = new FragmentGzipEncoder(120_000, 16, 0);
        for (var version = 0; version < 24; version++)
        {
            var stable = Bytes($"<article>version-{version:D2}" + new string('q', 400));
            var token = Bytes("<input value=\"request-token\">");
            var page = Enumerable.Range(0, 12).SelectMany(_ => new HtmlSegment[] {new(stable,true),new(token)}).ToArray();
            var decoded = Decode(Encode(encoder,page));
            Assert.Contains($"version-{version:D2}",Encoding.UTF8.GetString(decoded));
            Assert.True(encoder.CachedBytes<=120_000);
            Assert.True(encoder.CachedEntries<=16);
            Assert.True(encoder.CachedPlanEntries<=4);
        }
    }

    [Fact]
    public async Task ConcurrentAssemblyPlansAndNativeContextsResetWithoutCrossRequestBytes()
    {
        var encoder = new FragmentGzipEncoder(8_000_000, 256, 0);
        var stable = Bytes("<article>immutable-shared雪" + new string('q', 300));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            for (var round = 0; round < 6; round++)
            {
                var changing = Bytes($"<aside>version-{index:D2}-{round:D2}" + new string((char)('a'+index%26),1000) + "</aside>");
                var secret = Bytes($"<input value=\"secret-{index:D2}-{round:D2}-😀\">");
                var segments = Enumerable.Range(0, 12).SelectMany(_ => new HtmlSegment[] {new(stable,true),new(secret)}).ToArray();
                segments[0] = new(changing,true);
                Encode(encoder,segments);
            }
        })));
        Assert.True(encoder.CachedBytes<=8_000_000);
        Assert.True(encoder.CachedEntries<=256);
    }
}
