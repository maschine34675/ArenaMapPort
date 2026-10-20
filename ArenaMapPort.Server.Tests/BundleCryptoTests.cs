using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArenaMapPort.Crypto;
using ArenaMapPort.Shared;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ArenaMapPort.Server.Tests;

// Tests for ArenaMapPort.Crypto (docs/PLAN-ENCRYPTION.md, area CORE): the primitives on both providers, the AMPB container, the decrypting
// stream, the key derivation and key wrap, the key file. The Crypto sources are linked into this assembly, so the tests exercise the code
// that ships in the client and in the BundleCrypt tool. Every provider-dependent test runs on the managed provider and on Windows CNG.

/// <summary>Runs a theory once per crypto provider: "managed" (System.Security.Cryptography) and "cng" (bcrypt.dll through P/Invoke).</summary>
public sealed class ProviderDataAttribute : DataAttribute
{
    public override IEnumerable<object[]> GetData(MethodInfo testMethod)
    {
        yield return new object[] { "managed" };
        yield return new object[] { "cng" };
    }
}

internal static class Crypt
{
    public const string Map = "arena_test";
    public static readonly byte[] KMap = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 1)).ToArray();

    /// <summary>The provider for a theory row; null for "cng" where CNG does not exist (not Windows), so the row returns early.</summary>
    public static ICryptoProvider? Get(string name) => name == "cng" ? CryptoProviders.Cng : CryptoProviders.Managed;

    public static byte[] Hex(string hex) => Convert.FromHexString(hex);
    public static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    public static byte[] Random(int length, int seed)
    {
        byte[] b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    public static byte[] Pattern(int length)
    {
        byte[] b = new byte[length];
        for (int i = 0; i < length; i++)
            b[i] = (byte)(i * 31 + 7);
        return b;
    }

    public static byte[] Encrypt(byte[] plain, ICryptoProvider p, int chunkSize = 4096, string map = Map, byte[]? key = null)
    {
        using var dest = new MemoryStream();
        Ampb.Encrypt(new MemoryStream(plain), dest, map, key ?? KMap, p, chunkSize);
        return dest.ToArray();
    }

    public static AmpbReadStream Open(byte[] container, ICryptoProvider p, int slots = 16, string? map = Map, byte[]? key = null, Stream? source = null)
        => new AmpbReadStream(source ?? new MemoryStream(container), map, key ?? KMap, p, slots);

    /// <summary>Runs <paramref name="body"/> on <paramref name="count"/> dedicated threads and rethrows what any of them threw.</summary>
    public static void RunThreads(int count, Action<int> body)
    {
        var errors = new List<Exception>();
        var threads = Enumerable.Range(0, count).Select(i => new Thread(() =>
        {
            try { body(i); }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        if (errors.Count > 0)
            throw new AggregateException(errors);
    }

    public static byte[] ReadAll(Stream s)
    {
        s.Position = 0;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>True when opening and fully reading the container fails with an AmpbException; any other exception type fails the test.</summary>
    public static bool Detects(byte[] container, ICryptoProvider p, string? map = Map)
    {
        try
        {
            using var s = new AmpbReadStream(new MemoryStream(container), map, KMap, p, 2);
            byte[] all = new byte[s.Length];
            int total = 0;
            while (total < all.Length)
            {
                int n = s.Read(all, total, all.Length - total);
                if (n <= 0)
                    break;
                total += n;
            }
            return false;
        }
        catch (AmpbException)
        {
            return true;
        }
    }

    /// <summary>The container's version of the AES-CTR construction, built from System.Security.Cryptography only (an independent reference).</summary>
    public static byte[] ReferenceCtr(byte[] key, byte[] nonce, ulong firstBlock, byte[] data, int offset, int count)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] result = new byte[count];
        byte[] counter = new byte[16];
        Array.Copy(nonce, counter, 8);
        for (int block = 0; block * 16 < count; block++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(counter.AsSpan(8), firstBlock + (ulong)block);
            byte[] ks = aes.EncryptEcb(counter, PaddingMode.None);
            for (int j = 0; j < 16 && block * 16 + j < count; j++)
                result[block * 16 + j] = (byte)(data[offset + block * 16 + j] ^ ks[j]);
        }
        return result;
    }
}

/// <summary>A read-through wrapper that records every read of the container (position and bytes) so tests can say what was touched, and when.</summary>
internal sealed class CountingStream : Stream
{
    readonly Stream _inner;
    public readonly List<(long Position, int Count)> Reads = new();
    public long MaxReadEnd;
    public bool Disposed;

    public CountingStream(byte[] data) => _inner = new MemoryStream(data);

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        long position = _inner.Position;
        int n = _inner.Read(buffer, offset, count);
        lock (Reads)
        {
            Reads.Add((position, n));
            MaxReadEnd = Math.Max(MaxReadEnd, position + n);
        }
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public int ReadsAt(long position)
    {
        lock (Reads)
            return Reads.Count(r => r.Position == position);
    }
}

/// <summary>
/// The primitives of ArenaMapPort.Crypto: known answers (FIPS 180-4, RFC 4231, RFC 5869, FIPS-197, SP 800-38A) on both providers, the equality of
/// Windows CNG, the managed provider and System.Security.Cryptography on random inputs, and the provider selection.
/// </summary>
public sealed class CryptoPrimitivesTests
{
    [Fact]
    public void Cng_IsAvailableAndSelectedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.NotNull(CryptoProviders.Cng);
        Assert.Null(CryptoProviders.CngUnavailableReason);
        Assert.Equal("CNG", CryptoProviders.Cng!.Name);
        Assert.Equal("managed", CryptoProviders.Managed.Name);
        string? warning = null;
        Assert.Same(CryptoProviders.Cng, CryptoProviders.Select(w => warning = w));
        Assert.Null(warning);
    }

    [Fact]
    public void Select_ReturnsTheOverrideWhenOneIsSet()
    {
        // Harmless for tests running in parallel: the override is an equivalent, correct provider.
        CryptoProviders.Override = CryptoProviders.Managed;
        try
        {
            Assert.Same(CryptoProviders.Managed, CryptoProviders.Select(null));
        }
        finally
        {
            CryptoProviders.Override = null;
        }
    }

    [Fact]
    public void Selector_FallsBackToManagedWithOneWarning_WhenTheNativeProbeFails()
    {
        var selector = new ProviderSelector(CryptoProviders.Managed, () => throw new DllNotFoundException("bcrypt.dll"));
        Assert.Null(selector.Cng);
        Assert.Equal("DllNotFoundException: bcrypt.dll", selector.CngUnavailableReason);
        Assert.Same(CryptoProviders.Managed, selector.Select(null));   // no delegate yet: the warning is not used up
        var warnings = new List<string>();
        Assert.Same(CryptoProviders.Managed, selector.Select(warnings.Add));
        Assert.Same(CryptoProviders.Managed, selector.Select(warnings.Add));
        Assert.Equal("Windows CNG unavailable (DllNotFoundException: bcrypt.dll); using managed crypto, which is slow: map loading will take noticeably longer",
            Assert.Single(warnings));
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("hmac")]
    [InlineData("aes")]
    [InlineData("stream")]
    public void Selector_RejectsAProviderThatAnswersWrongWithoutAnException(string defect)
    {
        var selector = new ProviderSelector(CryptoProviders.Managed, () => new BrokenProvider(defect));
        Assert.Null(selector.Cng);
        Assert.Contains("known-answer test failed for broken", selector.CngUnavailableReason);
        var warnings = new List<string>();
        Assert.Same(CryptoProviders.Managed, selector.Select(warnings.Add));
        Assert.Contains("using managed crypto, which is slow", Assert.Single(warnings));
    }

    [Fact]
    public void Selector_UsesTheNativeProviderWhenItPassesItsSelfTest_AndNeverWarns()
    {
        var selector = new ProviderSelector(CryptoProviders.Managed, () => new BrokenProvider("none"));
        Assert.NotNull(selector.Cng);
        Assert.Null(selector.CngUnavailableReason);
        string? warning = null;
        Assert.Same(selector.Cng, selector.Select(w => warning = w));
        Assert.Null(warning);
    }

    /// <summary>A provider that wraps the managed one and answers wrongly in one place, to prove the self-test notices it.</summary>
    sealed class BrokenProvider : ICryptoProvider
    {
        readonly string _defect;
        readonly ICryptoProvider _inner = CryptoProviders.Managed;

        public BrokenProvider(string defect) => _defect = defect;
        public string Name => "broken";

        public byte[] Sha256(byte[] data, int offset, int count)
        {
            byte[] hash = _inner.Sha256(data, offset, count);
            if (_defect == "sha")
                hash[0] ^= 1;
            return hash;
        }

        public byte[] HmacSha256(byte[] key, byte[] data, int offset, int count)
        {
            byte[] mac = _inner.HmacSha256(key, data, offset, count);
            if (_defect == "hmac")
                mac[31] ^= 1;
            return mac;
        }

        public IAesKey NewAes(byte[] key32) => _defect == "aes" ? new CopyingAes() : _inner.NewAes(key32);

        public ISha256Stream NewSha256() => _inner.NewSha256();

        public IHashStream NewHmacSha256(byte[] key) =>
            _defect == "stream" ? new WrongStream(_inner.NewHmacSha256(key)) : _inner.NewHmacSha256(key);

        sealed class CopyingAes : IAesKey
        {
            public void EncryptEcb(byte[] src, int srcOffset, byte[] dst, int dstOffset, int count) => Array.Copy(src, srcOffset, dst, dstOffset, count);
            public void Dispose() { }
        }

        sealed class WrongStream : IHashStream
        {
            readonly IHashStream _inner;
            public WrongStream(IHashStream inner) => _inner = inner;
            public void Update(byte[] data, int offset, int count) => _inner.Update(data, offset, Math.Max(0, count - 1));
            public byte[] Finish() => _inner.Finish();
            public void Dispose() => _inner.Dispose();
        }
    }

    [Theory, ProviderData]
    public void SelfTest_PassesOnEveryProvider(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        ProviderSelfTest.Run(p);
    }

    [Theory, ProviderData]
    public void Sha256_KnownAnswers(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Crypt.ToHex(p.Sha256(Array.Empty<byte>(), 0, 0)));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Crypt.ToHex(p.Sha256(null!, 0, 0)));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Crypt.ToHex(p.Sha256(Crypt.Ascii("abc"), 0, 3)));
        byte[] two = Crypt.Ascii("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq");
        Assert.Equal("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1", Crypt.ToHex(p.Sha256(two, 0, two.Length)));

        byte[] million = new byte[1_000_000];
        Array.Fill(million, (byte)'a');
        const string millionA = "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0";
        Assert.Equal(millionA, Crypt.ToHex(p.Sha256(million, 0, million.Length)));
        using ISha256Stream s = p.NewSha256();
        for (int off = 0; off < million.Length;)
        {
            int n = Math.Min(7777, million.Length - off);
            s.Update(million, off, n);
            off += n;
        }
        Assert.Equal(millionA, Crypt.ToHex(s.Finish()));
    }

    [Theory, ProviderData]
    public void Sha256_RejectsBadRanges_AndAFinishedStream(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        Assert.ThrowsAny<ArgumentException>(() => p.Sha256(new byte[4], 2, 3));
        Assert.ThrowsAny<ArgumentException>(() => p.Sha256(new byte[4], -1, 1));
        ISha256Stream s = p.NewSha256();
        s.Update(new byte[3], 0, 3);
        s.Finish();
        Assert.Throws<ObjectDisposedException>(() => s.Update(new byte[1], 0, 1));
        Assert.Throws<ObjectDisposedException>(() => s.Finish());
    }

    [Theory, ProviderData]
    public void HmacSha256_Rfc4231(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] data1 = Crypt.Ascii("Hi There");
        byte[] key1 = Enumerable.Repeat((byte)0x0b, 20).ToArray();
        Assert.Equal("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7", Crypt.ToHex(p.HmacSha256(key1, data1, 0, data1.Length)));

        byte[] data2 = Crypt.Ascii("what do ya want for nothing?");
        Assert.Equal("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", Crypt.ToHex(p.HmacSha256(Crypt.Ascii("Jefe"), data2, 0, data2.Length)));

        byte[] key3 = Enumerable.Repeat((byte)0xaa, 20).ToArray();
        byte[] data3 = Enumerable.Repeat((byte)0xdd, 50).ToArray();
        Assert.Equal("773ea91e36800e46854db8ebd09181a72959098b3ef8c122d9635514ced565fe", Crypt.ToHex(p.HmacSha256(key3, data3, 0, data3.Length)));

        byte[] key6 = Enumerable.Repeat((byte)0xaa, 131).ToArray();
        byte[] data6 = Crypt.Ascii("Test Using Larger Than Block-Size Key - Hash Key First");
        Assert.Equal("60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54", Crypt.ToHex(p.HmacSha256(key6, data6, 0, data6.Length)));

        using IHashStream stream = p.NewHmacSha256(key6);
        stream.Update(data6, 0, 10);
        stream.Update(data6, 10, data6.Length - 10);
        Assert.Equal("60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54", Crypt.ToHex(stream.Finish()));
    }

    [Theory, ProviderData]
    public void HmacSha256_EmptyKeyEqualsTheReference(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] data = Crypt.Random(1000, 1);
        Assert.Equal(Crypt.ToHex(HMACSHA256.HashData(Array.Empty<byte>(), data)), Crypt.ToHex(p.HmacSha256(Array.Empty<byte>(), data, 0, data.Length)));
        using IHashStream stream = p.NewHmacSha256(Array.Empty<byte>());
        stream.Update(data, 0, data.Length);
        Assert.Equal(Crypt.ToHex(HMACSHA256.HashData(Array.Empty<byte>(), data)), Crypt.ToHex(stream.Finish()));
    }

    [Theory, ProviderData]
    public void Hkdf_Rfc5869_Cases1To3(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;

        byte[] ikm1 = Enumerable.Repeat((byte)0x0b, 22).ToArray();
        byte[] salt1 = Enumerable.Range(0, 13).Select(i => (byte)i).ToArray();
        byte[] info1 = Enumerable.Range(0xf0, 10).Select(i => (byte)i).ToArray();
        byte[] prk1 = Hkdf.Extract(p, salt1, ikm1);
        Assert.Equal("077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5", Crypt.ToHex(prk1));
        Assert.Equal("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", Crypt.ToHex(Hkdf.Expand(p, prk1, info1, 42)));

        byte[] ikm2 = Enumerable.Range(0, 80).Select(i => (byte)i).ToArray();
        byte[] salt2 = Enumerable.Range(0x60, 80).Select(i => (byte)i).ToArray();
        byte[] info2 = Enumerable.Range(0xb0, 80).Select(i => (byte)i).ToArray();
        byte[] prk2 = Hkdf.Extract(p, salt2, ikm2);
        Assert.Equal("06a6b88c5853361a06104c9ceb35b45cef760014904671014a193f40c15fc244", Crypt.ToHex(prk2));
        Assert.Equal("b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71cc30c58179ec3e87c14c01d5c1f3434f1d87",
            Crypt.ToHex(Hkdf.Expand(p, prk2, info2, 82)));

        byte[] prk3 = Hkdf.Extract(p, Array.Empty<byte>(), ikm1);
        Assert.Equal("19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04", Crypt.ToHex(prk3));
        Assert.Equal("8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8", Crypt.ToHex(Hkdf.Expand(p, prk3, Array.Empty<byte>(), 42)));
        Assert.Equal(Crypt.ToHex(prk3), Crypt.ToHex(Hkdf.Extract(p, null!, ikm1)));
    }

    [Theory, ProviderData]
    public void Hkdf_MatchesTheFrameworkImplementation_AndRefusesTooMuchOutput(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        var rng = new Random(5);
        for (int i = 0; i < 30; i++)
        {
            byte[] ikm = Crypt.Random(rng.Next(1, 100), i);
            byte[] salt = Crypt.Random(rng.Next(0, 100), i + 100);
            byte[] info = Crypt.Random(rng.Next(0, 100), i + 200);
            int length = rng.Next(0, 300);
            byte[] expected = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, info);
            Assert.Equal(Crypt.ToHex(expected), Crypt.ToHex(Hkdf.Expand(p, Hkdf.Extract(p, salt, ikm), info, length)));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => Hkdf.Expand(p, new byte[32], Array.Empty<byte>(), 255 * 32 + 1));
        Assert.Equal(255 * 32, Hkdf.Expand(p, new byte[32], Array.Empty<byte>(), 255 * 32).Length);
    }

    [Theory, ProviderData]
    public void Aes256_Fips197_C3(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] plain = Crypt.Hex("00112233445566778899aabbccddeeff");
        byte[] output = new byte[16];
        using (IAesKey aes = p.NewAes(key))
            aes.EncryptEcb(plain, 0, output, 0, 16);
        Assert.Equal("8ea2b7ca516745bfeafc49904b496089", Crypt.ToHex(output));

        // in place, and several blocks at odd offsets
        byte[] buffer = new byte[3 + 48];
        for (int b = 0; b < 3; b++)
            Buffer.BlockCopy(plain, 0, buffer, 3 + 16 * b, 16);
        using (IAesKey aes = p.NewAes(key))
        {
            aes.EncryptEcb(buffer, 3, buffer, 3, 48);
            Assert.Throws<ArgumentException>(() => aes.EncryptEcb(buffer, 3, buffer, 3, 17));
            aes.EncryptEcb(buffer, 0, buffer, 0, 0);
        }
        Assert.Equal(string.Concat(Enumerable.Repeat("8ea2b7ca516745bfeafc49904b496089", 3)), Crypt.ToHex(buffer[3..]));
        Assert.Throws<ArgumentException>(() => p.NewAes(new byte[16]));
        IAesKey disposed = p.NewAes(key);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.EncryptEcb(plain, 0, output, 0, 16));
    }

    [Theory, ProviderData]
    public void Ctr_Sp80038a_F55(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] key = Crypt.Hex("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
        byte[] nonce = Crypt.Hex("f0f1f2f3f4f5f6f7");
        byte[] plain = Crypt.Hex("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");
        string expected = "601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c52b0930daa23de94ce87017ba2d84988ddfc9c58db67aada613c2dd08457941a6";
        byte[] output = new byte[64];
        using (IAesKey aes = p.NewAes(key))
        {
            // SP 800-38A's first counter block is f0f1f2f3f4f5f6f7 f8f9fafbfcfdfeff: the nonce, then the big-endian counter 0xf8f9fafbfcfdfeff
            AesCtr.Transform(aes, nonce, 0xf8f9fafbfcfdfeffUL, plain, 0, output, 0, 64);
            Assert.Equal(expected, Crypt.ToHex(output));

            // the same stream in a different split, in place, decrypting back: 21 bytes (a partial second block), then the last two blocks
            byte[] split = (byte[])output.Clone();
            AesCtr.Transform(aes, nonce, 0xf8f9fafbfcfdfeffUL, split, 0, split, 0, 21);
            AesCtr.Transform(aes, nonce, 0xf8f9fafbfcfdfeffUL + 2, split, 32, split, 32, 32);
            Assert.Equal(Crypt.ToHex(plain[..21]), Crypt.ToHex(split[..21]));
            Assert.Equal(Crypt.ToHex(output[21..32]), Crypt.ToHex(split[21..32]));   // untouched
            Assert.Equal(Crypt.ToHex(plain[32..]), Crypt.ToHex(split[32..]));
        }
    }

    [Theory, ProviderData]
    public void Ctr_MatchesAnIndependentConstruction_AtOddOffsetsAndCounts(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        var rng = new Random(11);
        ulong[] firsts = { 0, 1, 0xFFFFFFF0UL, 0xFFFFFFFFUL, 0x123456789ABCUL, 0x00FFFFFFFFFFFF00UL };
        byte[] key = Crypt.Random(32, 12);
        using IAesKey aesKey = p.NewAes(key);
        for (int i = 0; i < 24; i++)
        {
            int count = i < 6 ? new[] { 1, 15, 16, 17, 65535, 65536 }[i] : rng.Next(1, 200_000);
            int srcOffset = rng.Next(0, 9);
            int dstOffset = rng.Next(0, 9);
            byte[] nonce = Crypt.Random(8, i);
            byte[] src = Crypt.Random(srcOffset + count + 5, i + 50);
            ulong first = firsts[i % firsts.Length];
            byte[] dst = new byte[dstOffset + count + 5];
            AesCtr.Transform(aesKey, nonce, first, src, srcOffset, dst, dstOffset, count);
            byte[] expected = Crypt.ReferenceCtr(key, nonce, first, src, srcOffset, count);
            Assert.True(expected.AsSpan().SequenceEqual(dst.AsSpan(dstOffset, count)), $"case {i}: count {count}, first {first:x}");
            Assert.All(dst.Take(dstOffset), b => Assert.Equal(0, b));
            Assert.All(dst.Skip(dstOffset + count), b => Assert.Equal(0, b));
        }
    }

    [Fact]
    public void Cng_Managed_AndTheFramework_AgreeOn100RandomInputs()
    {
        ICryptoProvider? cng = CryptoProviders.Cng;
        if (cng == null)
            return;
        ICryptoProvider managed = CryptoProviders.Managed;
        var rng = new Random(99);
        for (int i = 0; i < 100; i++)
        {
            int size = i % 10 == 0 ? rng.Next(1 << 20, 5 << 20) : (i % 10 == 1 ? 0 : rng.Next(1, 100_000));
            int offset = rng.Next(0, 17) | 1;   // odd
            byte[] buffer = Crypt.Random(offset + size + 3, i);
            byte[] reference = SHA256.HashData(buffer.AsSpan(offset, size));
            Assert.Equal(Crypt.ToHex(reference), Crypt.ToHex(cng.Sha256(buffer, offset, size)));
            Assert.Equal(Crypt.ToHex(reference), Crypt.ToHex(managed.Sha256(buffer, offset, size)));

            byte[] key = Crypt.Random(rng.Next(0, 200), i + 1000);
            byte[] mac = HMACSHA256.HashData(key, buffer.AsSpan(offset, size));
            Assert.Equal(Crypt.ToHex(mac), Crypt.ToHex(cng.HmacSha256(key, buffer, offset, size)));
            Assert.Equal(Crypt.ToHex(mac), Crypt.ToHex(managed.HmacSha256(key, buffer, offset, size)));

            using (IHashStream stream = cng.NewHmacSha256(key))
            {
                int half = size / 2;
                stream.Update(buffer, offset, half);
                stream.Update(buffer, offset + half, size - half);
                Assert.Equal(Crypt.ToHex(mac), Crypt.ToHex(stream.Finish()));
            }

            byte[] aesKey = Crypt.Random(32, i + 2000);
            int blocks = Math.Min(size / 16, 4096);
            byte[] reference16;
            using (Aes frameworkAes = Aes.Create())
            {
                frameworkAes.Key = aesKey;
                reference16 = frameworkAes.EncryptEcb(buffer.AsSpan(offset, blocks * 16), PaddingMode.None);
            }
            foreach (ICryptoProvider p in new[] { cng, managed })
            {
                byte[] output = new byte[offset + blocks * 16 + 1];
                using IAesKey k = p.NewAes(aesKey);
                k.EncryptEcb(buffer, offset, output, offset, blocks * 16);
                Assert.True(reference16.AsSpan().SequenceEqual(output.AsSpan(offset, blocks * 16)), $"AES case {i} on {p.Name}");
            }
        }
    }
}

/// <summary>The AMPB container: round trips at the chunk boundaries, determinism, a pinned golden container, and every kind of damage.</summary>
public sealed class AmpbContainerTests
{
    public static IEnumerable<object[]> RoundTripCases()
    {
        foreach (string provider in new[] { "managed", "cng" })
        {
            foreach (int size in new[] { 1, 15, 16, 17, 4095, 4096, 4097, 3 * 4096 + 5 })
                yield return new object[] { provider, 4096, size };
            foreach (int size in new[] { 1, (1 << 20) - 1, 1 << 20, (1 << 20) + 1, 3 * (1 << 20) + 5 })
                yield return new object[] { provider, 1 << 20, size };
        }
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void RoundTrip_AtTheChunkBoundaries(string name, int chunkSize, int size)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] plain = Crypt.Random(size, size);
        byte[] container = Crypt.Encrypt(plain, p, chunkSize);

        AmpbInfo info = Ampb.Inspect(new MemoryStream(container));
        Assert.True(info.IsValid);
        AmpbHeader h = info.Header!;
        Assert.Equal(size, h.PlaintextLength);
        Assert.Equal(chunkSize, h.ChunkSize);
        Assert.Equal((size + chunkSize - 1) / chunkSize, h.ChunkCount);
        Assert.Equal(Crypt.Map, h.MapKey);
        Assert.Equal(container.Length, h.FileLength);
        Assert.Equal(h.PayloadOffset + size, container.Length);
        Assert.Equal(0, h.PayloadOffset % 16);
        if (size >= 16)
            Assert.False(plain.AsSpan().SequenceEqual(container.AsSpan((int)h.PayloadOffset, size)));

        using var dest = new MemoryStream();
        Assert.Equal(size, Ampb.Decrypt(new MemoryStream(container), dest, Crypt.KMap, p, Crypt.Map));
        Assert.Equal(Crypt.ToHex(plain), Crypt.ToHex(dest.ToArray()));
        using AmpbReadStream stream = Crypt.Open(container, p);
        Assert.Equal(size, stream.Length);
        Assert.Equal(Crypt.ToHex(plain), Crypt.ToHex(Crypt.ReadAll(stream)));
    }

    [Theory, ProviderData]
    public void Encrypt_RefusesAnEmptyPlaintext_AndBadArguments(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        using var dest = new MemoryStream();
        Assert.Throws<ArgumentException>(() => Ampb.Encrypt(new MemoryStream(), dest, Crypt.Map, Crypt.KMap, p, 4096));
        var one = new MemoryStream(new byte[1]);
        Assert.Throws<ArgumentException>(() => Ampb.Encrypt(one, dest, "Arena", Crypt.KMap, p, 4096));
        Assert.Throws<ArgumentException>(() => Ampb.Encrypt(one, dest, "", Crypt.KMap, p, 4096));
        Assert.Throws<ArgumentException>(() => Ampb.Encrypt(one, dest, new string('a', 65), Crypt.KMap, p, 4096));
        Assert.Throws<ArgumentException>(() => Ampb.Encrypt(one, dest, Crypt.Map, new byte[31], p, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ampb.Encrypt(one, dest, Crypt.Map, Crypt.KMap, p, 4095));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ampb.Encrypt(one, dest, Crypt.Map, Crypt.KMap, p, 2048));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ampb.Encrypt(one, dest, Crypt.Map, Crypt.KMap, p, 32 << 20));
        Assert.Throws<ArgumentException>(() => Ampb.Encrypt(new NonSeekable(new byte[10]), dest, Crypt.Map, Crypt.KMap, p, 4096));
    }

    [Fact]
    public void Golden_Container_IsPinnedByteForByte()
    {
        // 5000 patterned bytes, 4 KiB chunks, K_map = (i * 7 + 1), map key "arena_test". If this hash changes, the on-disk format changed:
        // that needs a new container version, not a new constant here. The value was first reproduced byte for byte by an independent
        // implementation of docs/PLAN-ENCRYPTION.md 0.5 written in PowerShell on System.Security.Cryptography (no ArenaMapPort code, 2026-10-19).
        byte[] container = Crypt.Encrypt(Crypt.Pattern(5000), CryptoProviders.Managed, 4096);
        Assert.Equal(160 + 5000, container.Length);
        // the fixed header fields do not depend on any key: magic, version 1, flags 0, chunk 4096, 2 chunks, 5000 bytes, payload at 160
        Assert.Equal("414d5042" + "0100" + "0000" + "00100000" + "02000000" + "8813000000000000" + "a000000000000000", Crypt.ToHex(container[..32]));
        Assert.Equal("0a00" + "6172656e615f74657374" + "000000000000", Crypt.ToHex(container[40..58]));
        string hash = Crypt.ToHex(SHA256.HashData(container));
        Assert.True(hash == GoldenSha256, "the golden container changed: " + hash);
        if (CryptoProviders.Cng != null)
        {
            string viaCng = Crypt.ToHex(SHA256.HashData(Crypt.Encrypt(Crypt.Pattern(5000), CryptoProviders.Cng, 4096)));
            Assert.True(viaCng == GoldenSha256, "the golden container differs under CNG: " + viaCng);
        }
    }

    const string GoldenSha256 = "bdad3455ca3a2ccf9d05037ccbe639fc442f26d90c91d6d1668982070ec57a10";

    [Theory, ProviderData]
    public void Encryption_IsDeterministic_AndTheNonceFollowsThePlaintext(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] plain = Crypt.Random(10_000, 3);
        byte[] a = Crypt.Encrypt(plain, p);
        byte[] b = Crypt.Encrypt(plain, p);
        Assert.True(a.AsSpan().SequenceEqual(b));

        byte[] changed = (byte[])plain.Clone();
        changed[7777] ^= 1;
        byte[] c = Crypt.Encrypt(changed, p);
        Assert.False(a.AsSpan(32, 8).SequenceEqual(c.AsSpan(32, 8)), "one plaintext byte must change the nonce");

        byte[] otherKey = (byte[])Crypt.KMap.Clone();
        otherKey[0] ^= 1;
        byte[] d = Crypt.Encrypt(plain, p, key: otherKey);
        Assert.False(a.AsSpan(32, 8).SequenceEqual(d.AsSpan(32, 8)));

        byte[] otherMap = Crypt.Encrypt(plain, p, map: "arena_other");
        Assert.False(a.AsSpan(64).SequenceEqual(otherMap.AsSpan(64)));
    }

    [Fact]
    public void BothProviders_ProduceIdenticalContainers()
    {
        ICryptoProvider? cng = CryptoProviders.Cng;
        if (cng == null)
            return;
        foreach (int size in new[] { 1, 4096, 4097, 100_000 })
        {
            byte[] plain = Crypt.Random(size, size);
            byte[] fromCng = Crypt.Encrypt(plain, cng);
            byte[] fromManaged = Crypt.Encrypt(plain, CryptoProviders.Managed);
            Assert.True(fromCng.AsSpan().SequenceEqual(fromManaged), $"size {size}");
            // and each provider opens the other's container
            using AmpbReadStream viaCng = Crypt.Open(fromManaged, cng);
            using AmpbReadStream viaManaged = Crypt.Open(fromCng, CryptoProviders.Managed);
            Assert.Equal(Crypt.ToHex(plain), Crypt.ToHex(Crypt.ReadAll(viaCng)));
            Assert.Equal(Crypt.ToHex(plain), Crypt.ToHex(Crypt.ReadAll(viaManaged)));
        }
    }

    [Fact]
    public void TheRealChunkSizeMatchesThePlan_AndTheEncryptedBytesMatchTheIndependentReference()
    {
        // The container payload is the CTR stream of the plaintext: check it against the independent reference construction.
        byte[] plain = Crypt.Random(300_000, 9);
        byte[] container = Crypt.Encrypt(plain, CryptoProviders.Managed, 4096);
        AmpbHeader h = Ampb.Inspect(new MemoryStream(container)).Header!;
        byte[] kEnc = HKDF.Expand(HashAlgorithmName.SHA256, Crypt.KMap, 32, Crypt.Ascii("ArenaMapPort bundle enc v1"));
        byte[] expected = Crypt.ReferenceCtr(kEnc, h.Nonce, 0, plain, 0, plain.Length);
        Assert.True(expected.AsSpan().SequenceEqual(container.AsSpan((int)h.PayloadOffset)));
        // the nonce is the first 8 bytes of HMAC(K_nonce, plaintext)
        byte[] kNonce = HKDF.Expand(HashAlgorithmName.SHA256, Crypt.KMap, 32, Crypt.Ascii("ArenaMapPort bundle nonce v1"));
        Assert.Equal(Crypt.ToHex(HMACSHA256.HashData(kNonce, plain)[..8]), Crypt.ToHex(h.Nonce));
        // the header MAC is HMAC(K_mac, everything before it)
        byte[] kMac = HKDF.Expand(HashAlgorithmName.SHA256, Crypt.KMap, 32, Crypt.Ascii("ArenaMapPort bundle mac v1"));
        int macAt = (int)h.PayloadOffset - 32;
        Assert.Equal(Crypt.ToHex(HMACSHA256.HashData(kMac, container.AsSpan(0, macAt))), Crypt.ToHex(container[macAt..(int)h.PayloadOffset]));
        // every table entry is the SHA-256 of the ciphertext chunk
        for (int i = 0; i < h.ChunkCount; i++)
        {
            int start = (int)h.PayloadOffset + i * 4096;
            int length = Math.Min(4096, plain.Length - i * 4096);
            Assert.Equal(Crypt.ToHex(SHA256.HashData(container.AsSpan(start, length))), Crypt.ToHex(h.GetChunkHash(i)));
        }
        Assert.Equal(1 << 20, Ampb.DefaultChunkSize);
    }

    [Theory, ProviderData]
    public void EveryFlippedByte_IsDetected_AsAnAmpbException(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] container = Crypt.Encrypt(Crypt.Random(3 * 4096 + 5, 21), p);
        Assert.False(Crypt.Detects(container, p));
        for (int i = 0; i < container.Length; i++)
        {
            byte[] tampered = (byte[])container.Clone();
            tampered[i] ^= 0x01;
            Assert.True(Crypt.Detects(tampered, p), $"a flipped byte at offset {i} was not detected");
        }
    }

    [Theory, ProviderData]
    public void FlippedBytes_ReportTheRightKind(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] container = Crypt.Encrypt(Crypt.Random(3 * 4096 + 5, 22), p);
        AmpbHeader h = Ampb.Inspect(new MemoryStream(container)).Header!;
        Func<int, AmpbError> kindOf = offset =>
        {
            byte[] t = (byte[])container.Clone();
            t[offset] ^= 0x80;
            return Assert.Throws<AmpbException>(() => Crypt.Open(t, p, 2).Dispose()).Kind;
        };
        Assert.Equal(AmpbError.BadMagic, kindOf(0));
        Assert.Equal(AmpbError.BadMagic, kindOf(3));
        Assert.Equal(AmpbError.BadVersion, kindOf(4));
        Assert.Equal(AmpbError.BadVersion, kindOf(7));
        Assert.Equal(AmpbError.BadHeader, kindOf(9));                        // chunk size
        Assert.Equal(AmpbError.HeaderAuth, kindOf(32));                      // nonce
        Assert.Equal(AmpbError.HeaderAuth, kindOf(60));                      // padding after the map key
        Assert.Equal(AmpbError.HeaderAuth, kindOf((int)h.TableOffset + 5));  // chunk table
        Assert.Equal(AmpbError.HeaderAuth, kindOf((int)h.PayloadOffset - 1)); // the MAC itself

        // the payload is read lazily: only the chunk that was touched fails
        byte[] payloadFlip = (byte[])container.Clone();
        payloadFlip[(int)h.PayloadOffset + 2 * 4096 + 17] ^= 1;
        using AmpbReadStream s = Crypt.Open(payloadFlip, p, 2);
        AmpbException ex = Assert.Throws<AmpbException>(() => s.ReadAt(2 * 4096, new byte[10], 0, 10));
        Assert.Equal(AmpbError.ChunkIntegrity, ex.Kind);
        Assert.Equal(2, ex.Chunk);
        Assert.Same(ex.GetType(), typeof(AmpbException));
        Assert.True(ex is IOException);
    }

    [Theory, ProviderData]
    public void ReservedFieldsAndStructure_AreRefused(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] container = Crypt.Encrypt(Crypt.Random(3 * 4096 + 5, 23), p);
        Func<Action<byte[]>, AmpbError> kindOf = patch =>
        {
            byte[] t = (byte[])container.Clone();
            patch(t);
            return Assert.Throws<AmpbException>(() => Crypt.Open(t, p, 2).Dispose()).Kind;
        };
        Assert.Equal(AmpbError.BadVersion, kindOf(t => t[4] = 2));
        Assert.Equal(AmpbError.BadVersion, kindOf(t => t[4] = 0));
        Assert.Equal(AmpbError.BadVersion, kindOf(t => t[6] = 1));
        Assert.Equal(AmpbError.BadHeader, kindOf(t => Ampb.WriteU32(t, 8, 4097)));         // not a multiple of 16
        Assert.Equal(AmpbError.BadHeader, kindOf(t => Ampb.WriteU32(t, 8, 2048)));         // below the minimum
        Assert.Equal(AmpbError.BadHeader, kindOf(t => Ampb.WriteU32(t, 8, 32 << 20)));     // above the maximum
        Assert.Equal(AmpbError.BadHeader, kindOf(t => Ampb.WriteU32(t, 12, 5)));           // chunk count does not match the length
        Assert.Equal(AmpbError.BadHeader, kindOf(t => Ampb.WriteU64(t, 16, 0)));           // empty plaintext
        Assert.Equal(AmpbError.BadHeader, kindOf(t => Ampb.WriteU64(t, 16, long.MaxValue)));
        Assert.Equal(AmpbError.BadHeader, kindOf(t => Ampb.WriteU64(t, 24, 99)));          // payload offset
        Assert.Equal(AmpbError.BadHeader, kindOf(t => Ampb.WriteU16(t, 40, 0)));           // key length
        Assert.Equal(AmpbError.BadHeader, kindOf(t => Ampb.WriteU16(t, 40, 65)));
        Assert.Equal(AmpbError.BadHeader, kindOf(t => Ampb.WriteU16(t, 40, 11)));          // valid length, wrong layout
        Assert.Equal(AmpbError.BadHeader, kindOf(t => t[42] = (byte)'A'));                 // outside [a-z0-9_]
        Assert.Equal(AmpbError.BadHeader, kindOf(t => t[44] = (byte)'-'));
    }

    [Theory, ProviderData]
    public void TruncatedAndLongerFiles_AreRefused(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] container = Crypt.Encrypt(Crypt.Random(3 * 4096 + 5, 24), p);
        foreach (int cut in new[] { 1, 4096, 4096 + 5, 100, container.Length - 20, container.Length - 4 })
        {
            byte[] shorter = container[..(container.Length - cut)];
            Assert.Equal(AmpbError.Truncated, Assert.Throws<AmpbException>(() => Crypt.Open(shorter, p, 2).Dispose()).Kind);
        }
        Assert.Equal(AmpbError.Truncated, Assert.Throws<AmpbException>(() => Crypt.Open(container[..41], p, 2).Dispose()).Kind);
        Assert.Equal(AmpbError.BadMagic, Assert.Throws<AmpbException>(() => Crypt.Open(container[..3], p, 2).Dispose()).Kind);
        Assert.Equal(AmpbError.BadMagic, Assert.Throws<AmpbException>(() => Crypt.Open(Array.Empty<byte>(), p, 2).Dispose()).Kind);

        byte[] longer = new byte[container.Length + 1];
        container.CopyTo(longer, 0);
        Assert.Equal(AmpbError.BadHeader, Assert.Throws<AmpbException>(() => Crypt.Open(longer, p, 2).Dispose()).Kind);
    }

    [Theory, ProviderData]
    public void WrongKey_FailsInTheHeader_BeforeAnyPayloadIsRead(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] container = Crypt.Encrypt(Crypt.Random(3 * 4096 + 5, 25), p);
        AmpbHeader h = Ampb.Inspect(new MemoryStream(container)).Header!;
        byte[] wrong = (byte[])Crypt.KMap.Clone();
        wrong[31] ^= 0x10;
        var counting = new CountingStream(container);
        AmpbException ex = Assert.Throws<AmpbException>(() => new AmpbReadStream(counting, Crypt.Map, wrong, p));
        Assert.Equal(AmpbError.HeaderAuth, ex.Kind);
        Assert.True(counting.MaxReadEnd <= h.PayloadOffset, $"read up to {counting.MaxReadEnd}, the payload starts at {h.PayloadOffset}");
        Assert.True(counting.Disposed, "a failed open closes the source it owns");
        Assert.Throws<ArgumentException>(() => new AmpbReadStream(new MemoryStream(container), Crypt.Map, new byte[16], p));
    }

    [Theory, ProviderData]
    public void TheHeaderMapKey_MustMatchTheExpectedOne(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] container = Crypt.Encrypt(Crypt.Random(5000, 26), p);
        AmpbException ex = Assert.Throws<AmpbException>(() => Crypt.Open(container, p, 2, "arena_bowl"));
        Assert.Equal(AmpbError.WrongMap, ex.Kind);
        Assert.Contains("arena_test", ex.Message);
        using (AmpbReadStream any = Crypt.Open(container, p, 2, null))
            Assert.Equal(Crypt.Map, any.Header.MapKey);
        Assert.Equal(AmpbError.WrongMap, Assert.Throws<AmpbException>(() => Ampb.ReadHeader(new MemoryStream(container), Crypt.KMap, p, "arena_bowl")).Kind);
        Assert.True(Ampb.ReadHeader(new MemoryStream(container), Crypt.KMap, p, Crypt.Map).Authenticated);
    }

    [Theory]
    [InlineData("managed", 4096)]
    [InlineData("managed", 65536)]
    [InlineData("managed", 16 << 20)]
    [InlineData("cng", 4096)]
    [InlineData("cng", 131072)]
    [InlineData("cng", 16 << 20)]
    public void EveryAcceptedChunkSize_RoundTrips_AndAShortFileDoesNotAllocateAFullChunk(string name, int chunkSize)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] plain = Crypt.Random(chunkSize == 16 << 20 ? 70_000 : 3 * chunkSize + 11, chunkSize);
        byte[] container = Crypt.Encrypt(plain, p, chunkSize);
        AmpbHeader h = Ampb.Inspect(new MemoryStream(container)).Header!;
        Assert.Equal(chunkSize, h.ChunkSize);
        Assert.Equal(chunkSize == 16 << 20 ? 1 : 4, h.ChunkCount);
        using AmpbReadStream s = Crypt.Open(container, p);
        Assert.Equal(Crypt.ToHex(plain), Crypt.ToHex(Crypt.ReadAll(s)));
    }

    [Fact]
    public void Open_ByPath_RejectsBadArgumentsWithoutLeavingTheFileLocked()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ampb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "x.bundle");
            File.WriteAllBytes(path, Crypt.Encrypt(Crypt.Random(500, 1), CryptoProviders.Managed));
            Assert.Throws<ArgumentOutOfRangeException>(() => Ampb.Open(path, Crypt.KMap, cacheSlots: 0));
            byte[] wrong = (byte[])Crypt.KMap.Clone();
            wrong[0] ^= 1;
            Assert.Equal(AmpbError.HeaderAuth, Assert.Throws<AmpbException>(() => Ampb.Open(path, wrong)).Kind);
            File.Delete(path);   // would throw if either failed call had left the file open
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Inspect_DescribesFilesWithoutAKey()
    {
        byte[] container = Crypt.Encrypt(Crypt.Random(9000, 27), CryptoProviders.Managed);
        AmpbInfo ok = Ampb.Inspect(new MemoryStream(container));
        Assert.True(ok.IsAmpb);
        Assert.True(ok.IsValid);
        Assert.Null(ok.Error);
        Assert.False(ok.Header!.Authenticated);
        Assert.Equal(3, ok.Header.ChunkCount);
        Assert.Equal(8, ok.Header.Nonce.Length);

        byte[] unityFs = Crypt.Ascii("UnityFS\0").Concat(new byte[200]).ToArray();
        AmpbInfo plain = Ampb.Inspect(new MemoryStream(unityFs));
        Assert.False(plain.IsAmpb);
        Assert.False(plain.IsValid);
        Assert.Equal(AmpbError.BadMagic, plain.Error!.Kind);
        Assert.Null(plain.Header);

        AmpbInfo cut = Ampb.Inspect(new MemoryStream(container[..(container.Length - 1)]));
        Assert.True(cut.IsAmpb);
        Assert.False(cut.IsValid);
        Assert.Equal(AmpbError.Truncated, cut.Error!.Kind);

        Assert.True(Ampb.IsAmpb(new MemoryStream(container)));
        Assert.False(Ampb.IsAmpb(new MemoryStream(unityFs)));
        Assert.False(Ampb.IsAmpb(new MemoryStream(new byte[2])));
        var positioned = new MemoryStream(container) { Position = 77 };
        Assert.True(Ampb.IsAmpb(positioned));
        Assert.Equal(77, positioned.Position);
    }

    [Theory, ProviderData]
    public void VerifyChunks_NamesTheDamagedChunk(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] container = Crypt.Encrypt(Crypt.Random(3 * 4096 + 5, 28), p);
        AmpbHeader h = Ampb.ReadHeader(new MemoryStream(container), Crypt.KMap, p, Crypt.Map);
        Assert.Equal(4, Ampb.VerifyChunks(new MemoryStream(container), h, p));
        container[(int)h.PayloadOffset + 3 * 4096 + 2] ^= 1;
        AmpbException ex = Assert.Throws<AmpbException>(() => Ampb.VerifyChunks(new MemoryStream(container), h, p));
        Assert.Equal(AmpbError.ChunkIntegrity, ex.Kind);
        Assert.Equal(3, ex.Chunk);
    }

    [Fact]
    public void Files_RoundTripThroughDiskAndOpenByPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ampb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] plain = Crypt.Random(2 * 1048576 + 123, 29);
            string source = Path.Combine(dir, "plain.bundle");
            string target = Path.Combine(dir, "enc.bundle");
            File.WriteAllBytes(source, plain);
            using (FileStream src = File.OpenRead(source))
            using (FileStream dst = new FileStream(target, FileMode.Create, FileAccess.ReadWrite))
            {
                AmpbHeader written = Ampb.Encrypt(src, dst, Crypt.Map, Crypt.KMap);
                Assert.Equal(1 << 20, written.ChunkSize);
                Assert.Equal(3, written.ChunkCount);
                Assert.True(written.Authenticated);
            }
            Assert.True(Ampb.IsAmpb(target));
            Assert.False(Ampb.IsAmpb(source));
            Assert.True(Ampb.Inspect(target).IsValid);
            using (AmpbReadStream s = Ampb.Open(target, Crypt.KMap, expectedMapKey: Crypt.Map))
            {
                Assert.Equal(plain.Length, s.Length);
                Assert.Equal(Crypt.ToHex(SHA256.HashData(plain)), Crypt.ToHex(SHA256.HashData(Crypt.ReadAll(s))));
            }
            // readable while the file is open elsewhere (Syncthing, a second reader)
            using (FileStream other = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (AmpbReadStream s = Ampb.Open(target, Crypt.KMap))
            {
                byte[] one = new byte[1];
                Assert.Equal(1, s.ReadAt(1048576, one, 0, 1));
                Assert.Equal(plain[1048576], one[0]);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    sealed class NonSeekable : MemoryStream
    {
        public NonSeekable(byte[] data) : base(data) { }
        public override bool CanSeek => false;
    }
}

/// <summary>
/// The decrypting stream AssetBundle.LoadFromStream will read: Length/Position/Seek, reads across chunk boundaries and at the end, the read patterns
/// Unity uses (header first, then random offsets), the LRU cache, lazy verification, damaged chunks, ownership of the source and concurrent readers.
/// </summary>
public sealed class AmpbReadStreamTests
{
    const int Chunk = 4096;
    static readonly byte[] Plain = Crypt.Random(4 * Chunk + 123, 41);   // 5 chunks, the last one short

    [Theory, ProviderData]
    public void Stream_Properties_AndUnsupportedOperations(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        AmpbReadStream s = Crypt.Open(Crypt.Encrypt(Plain, p, Chunk), p);
        Assert.True(s.CanRead);
        Assert.True(s.CanSeek);
        Assert.False(s.CanWrite);
        Assert.Equal(Plain.Length, s.Length);
        Assert.Equal(0, s.Position);
        Assert.Throws<NotSupportedException>(() => s.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => s.SetLength(1));
        s.Flush();
        Assert.Throws<ArgumentNullException>(() => s.Read(null!, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Read(new byte[4], -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Read(new byte[4], 0, -1));
        Assert.Throws<ArgumentException>(() => s.Read(new byte[4], 2, 3));
        Assert.Equal(0, s.Read(new byte[4], 0, 0));
        s.Dispose();
        Assert.False(s.CanRead);
        Assert.Throws<ObjectDisposedException>(() => s.Read(new byte[4], 0, 4));
        Assert.Throws<ObjectDisposedException>(() => s.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => s.Seek(0, SeekOrigin.Begin));
        s.Dispose();   // twice is fine
    }

    [Theory, ProviderData]
    public void Seek_AllOrigins_AndPastTheEnd(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        using AmpbReadStream s = Crypt.Open(Crypt.Encrypt(Plain, p, Chunk), p);
        Assert.Equal(10, s.Seek(10, SeekOrigin.Begin));
        Assert.Equal(15, s.Seek(5, SeekOrigin.Current));
        Assert.Equal(15, s.Position);
        Assert.Equal(Plain.Length - 1, s.Seek(-1, SeekOrigin.End));
        Assert.Equal(Plain[^1], s.ReadByte());
        Assert.Equal(-1, s.ReadByte());
        Assert.Equal(Plain.Length + 100, s.Seek(100, SeekOrigin.End));
        Assert.Equal(0, s.Read(new byte[10], 0, 10));
        Assert.Equal(Plain.Length + 100, s.Position);
        Assert.Throws<IOException>(() => s.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => s.Seek(-(Plain.Length + 1), SeekOrigin.End));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Position = -1);
        Assert.Throws<ArgumentException>(() => s.Seek(0, (SeekOrigin)9));
        s.Position = 3;
        Assert.Equal(Plain[3], s.ReadByte());
        Assert.Equal(4, s.Position);
    }

    [Theory, ProviderData]
    public void Reads_AcrossChunkBoundaries_AndAtTheEnd(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        using AmpbReadStream s = Crypt.Open(Crypt.Encrypt(Plain, p, Chunk), p);
        byte[] buffer = new byte[10];
        s.Position = Chunk - 3;
        Assert.Equal(10, s.Read(buffer, 0, 10));
        Assert.True(Plain.AsSpan(Chunk - 3, 10).SequenceEqual(buffer));
        Assert.Equal(Chunk + 7, s.Position);

        // one call larger than several chunks, into an offset buffer
        byte[] big = new byte[3 + 3 * Chunk];
        s.Position = Chunk / 2;
        Assert.Equal(3 * Chunk, s.Read(big, 3, 3 * Chunk));
        Assert.True(Plain.AsSpan(Chunk / 2, 3 * Chunk).SequenceEqual(big.AsSpan(3)));

        // the end: the short remainder, then zero
        byte[] tail = new byte[1000];
        s.Position = Plain.Length - 50;
        Assert.Equal(50, s.Read(tail, 0, 1000));
        Assert.True(Plain.AsSpan(Plain.Length - 50).SequenceEqual(tail.AsSpan(0, 50)));
        Assert.Equal(0, s.Read(tail, 0, 1000));

        // everything in one call
        byte[] all = new byte[Plain.Length];
        s.Position = 0;
        Assert.Equal(Plain.Length, s.Read(all, 0, all.Length));
        Assert.True(Plain.AsSpan().SequenceEqual(all));

        // ReadByte over a boundary
        s.Position = Chunk - 1;
        Assert.Equal(Plain[Chunk - 1], s.ReadByte());
        Assert.Equal(Plain[Chunk], s.ReadByte());
    }

    [Theory]
    [InlineData("managed", 16)]
    [InlineData("managed", 2)]
    [InlineData("cng", 16)]
    [InlineData("cng", 2)]
    [InlineData("cng", 1)]
    public void UnityPattern_HeaderFirst_ThenSeededRandomReads(string name, int slots)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        using AmpbReadStream s = Crypt.Open(Crypt.Encrypt(Plain, p, Chunk), p, slots);
        byte[] header = new byte[64];
        Assert.Equal(64, s.Read(header, 0, 64));
        Assert.True(Plain.AsSpan(0, 64).SequenceEqual(header));

        var rng = new Random(2024);
        byte[] buffer = new byte[3 * Chunk];
        for (int i = 0; i < 2000; i++)
        {
            int offset = rng.Next(0, Plain.Length + 10);
            int length = rng.Next(0, 3 * Chunk);
            s.Seek(offset, SeekOrigin.Begin);
            int got = s.Read(buffer, 0, length);
            int expected = Math.Max(0, Math.Min(length, Plain.Length - offset));
            Assert.Equal(expected, got);
            Assert.True(Plain.AsSpan(Math.Min(offset, Plain.Length), expected).SequenceEqual(buffer.AsSpan(0, expected)), $"read {i} at {offset} length {length}");
            Assert.Equal(offset + got, s.Position);
        }
    }

    [Theory, ProviderData]
    public void Cache_OfTwoSlots_EvictsTheLeastRecentlyUsed_AndEachChunkIsHashedOnce(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] container = Crypt.Encrypt(Plain, p, Chunk);
        var counting = new CountingStream(container);
        using var s = new AmpbReadStream(counting, Crypt.Map, Crypt.KMap, p, 2);
        long payload = s.Header.PayloadOffset;
        byte[] one = new byte[1];

        // LRU: 0, 1, 0 (hit), 2 (evicts 1), 0 (hit), 1 (miss again)
        foreach (int chunk in new[] { 0, 1, 0, 2, 0, 1 })
            s.ReadAt((long)chunk * Chunk, one, 0, 1);
        AmpbStats stats = s.Stats;
        Assert.Equal(4, stats.CacheMisses);
        Assert.Equal(2, stats.CacheHits);
        Assert.Equal(2, counting.ReadsAt(payload + 1 * Chunk));        // chunk 1 was read from the source twice
        Assert.Equal(1, counting.ReadsAt(payload + 0 * Chunk));
        Assert.Equal(3, stats.ChunksVerified);                         // 0, 1, 2: chunk 1 is not hashed again after eviction
        Assert.Equal(4, stats.ChunksDecrypted);
        Assert.Equal(4L * Chunk, stats.BytesDecrypted);

        // two full sequential passes over 5 chunks with 2 slots: every access misses, every chunk is hashed once
        var sequential = new CountingStream(container);
        using var t = new AmpbReadStream(sequential, Crypt.Map, Crypt.KMap, p, 2);
        Crypt.ReadAll(t);
        Crypt.ReadAll(t);
        AmpbStats two = t.Stats;
        Assert.Equal(10, two.CacheMisses);
        Assert.Equal(5, two.ChunksVerified);
        Assert.Equal(10, two.ChunksDecrypted);
        Assert.Equal(2L * Plain.Length, two.BytesDecrypted);
        Assert.Equal(2, sequential.ReadsAt(t.Header.PayloadOffset));
        Assert.True(two.CryptoMilliseconds >= 0 && two.ReadMilliseconds >= 0);
    }

    [Theory, ProviderData]
    public void Verification_IsLazy_NothingBeyondTheHeaderIsReadUntilNeeded(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        var counting = new CountingStream(Crypt.Encrypt(Plain, p, Chunk));
        using var s = new AmpbReadStream(counting, Crypt.Map, Crypt.KMap, p);
        Assert.True(counting.MaxReadEnd <= s.Header.PayloadOffset);
        Assert.Equal(0, s.Stats.ChunksVerified);
        s.ReadAt(3L * Chunk + 5, new byte[10], 0, 10);
        Assert.Equal(1, s.Stats.ChunksVerified);
        Assert.Equal(1, counting.ReadsAt(s.Header.PayloadOffset + 3L * Chunk));
        Assert.Equal(1, counting.Reads.Count(r => r.Position >= s.Header.PayloadOffset));
    }

    [Theory, ProviderData]
    public void ACorruptChunk_FailsOnlyTheReadsThatTouchIt(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] container = Crypt.Encrypt(Plain, p, Chunk);
        AmpbHeader h = Ampb.Inspect(new MemoryStream(container)).Header!;
        container[(int)h.PayloadOffset + 2 * Chunk + 100] ^= 0xFF;
        using AmpbReadStream s = Crypt.Open(container, p, 3);
        byte[] buffer = new byte[Chunk];
        foreach (int good in new[] { 0, 1, 3, 4 })
        {
            Assert.Equal(good == 4 ? 123 : Chunk, s.ReadAt((long)good * Chunk, buffer, 0, Chunk));
            Assert.True(Plain.AsSpan(good * Chunk, good == 4 ? 123 : Chunk).SequenceEqual(buffer.AsSpan(0, good == 4 ? 123 : Chunk)));
        }
        Assert.Null(s.FirstError);

        AmpbException first = Assert.Throws<AmpbException>(() => s.ReadAt(2L * Chunk, buffer, 0, 1));
        Assert.Equal(AmpbError.ChunkIntegrity, first.Kind);
        Assert.Equal(2, first.Chunk);
        Assert.Contains("chunk 2", first.Message);
        Assert.Same(first, s.FirstError);                                  // kept for the owner to log later, from the game thread

        s.Position = 2L * Chunk - 5;                                       // a read that spans into the damaged chunk fails too
        Assert.Equal(AmpbError.ChunkIntegrity, Assert.Throws<AmpbException>(() => s.Read(buffer, 0, 10)).Kind);
        Assert.Equal(2L * Chunk - 5, s.Position);                          // a failed read does not move the position

        Assert.Equal(10, s.ReadAt(10, buffer, 0, 10));                     // the good chunks keep working
        Assert.Equal(Chunk, s.ReadAt(3L * Chunk, buffer, 0, Chunk));
        Assert.Equal(2, s.FirstError!.Chunk);
    }

    [Theory, ProviderData]
    public void ReadAt_DoesNotMoveThePosition_AndValidatesItsArguments(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        using AmpbReadStream s = Crypt.Open(Crypt.Encrypt(Plain, p, Chunk), p);
        s.Position = 77;
        byte[] buffer = new byte[20];
        Assert.Equal(20, s.ReadAt(Chunk - 10, buffer, 0, 20));
        Assert.True(Plain.AsSpan(Chunk - 10, 20).SequenceEqual(buffer));
        Assert.Equal(77, s.Position);
        Assert.Equal(0, s.ReadAt(Plain.Length, buffer, 0, 20));
        Assert.Equal(0, s.ReadAt(Plain.Length + 1000, buffer, 0, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.ReadAt(-1, buffer, 0, 1));
        Assert.Throws<ArgumentException>(() => s.ReadAt(0, buffer, 15, 10));
    }

    [Theory, ProviderData]
    public void TheSource_IsClosedWithTheStream_UnlessLeftOpen(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] container = Crypt.Encrypt(Plain, p, Chunk);
        var owned = new CountingStream(container);
        new AmpbReadStream(owned, Crypt.Map, Crypt.KMap, p).Dispose();
        Assert.True(owned.Disposed);

        var shared = new CountingStream(container);
        new AmpbReadStream(shared, Crypt.Map, Crypt.KMap, p, 16, true).Dispose();
        Assert.False(shared.Disposed);
        shared.Dispose();
    }

    [Theory, ProviderData]
    public void EightThreads_RandomReads_AreExact(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] plain = Crypt.Random(12 * Chunk + 7, 77);
        using AmpbReadStream s = Crypt.Open(Crypt.Encrypt(plain, p, Chunk), p, 3);   // 3 slots over 13 chunks: constant eviction
        Crypt.RunThreads(8, seed =>
        {
            var rng = new Random(seed);
            byte[] buffer = new byte[2 * Chunk];
            for (int i = 0; i < 500; i++)
            {
                int offset = rng.Next(0, plain.Length);
                int length = rng.Next(1, 2 * Chunk);
                int got = s.ReadAt(offset, buffer, 0, length);
                int expected = Math.Min(length, plain.Length - offset);
                if (got != expected || !plain.AsSpan(offset, expected).SequenceEqual(buffer.AsSpan(0, expected)))
                    throw new InvalidOperationException($"thread {seed} read {i} at {offset} length {length} returned wrong data");
            }
        });
        Assert.Equal(plain.Length, s.ReadAt(0, new byte[plain.Length], 0, plain.Length));   // touch every chunk once, in case the random reads missed one
        AmpbStats stats = s.Stats;
        Assert.True(stats.CacheHits + stats.CacheMisses >= 8 * 500);   // every read touched at least one chunk
        Assert.Equal(stats.CacheMisses, stats.ChunksDecrypted);
        Assert.Equal(13, stats.ChunksVerified);                         // each of the 13 chunks was hashed exactly once, however often it was evicted
    }

    [Theory, ProviderData]
    public void SeekAndRead_FromSeveralThreads_NeverCorruptTheStream(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] plain = Crypt.Random(12 * Chunk + 7, 78);
        using AmpbReadStream s = Crypt.Open(Crypt.Encrypt(plain, p, Chunk), p, 2);
        Crypt.RunThreads(4, t =>
        {
            var rng = new Random(t + 100);
            byte[] buffer = new byte[Chunk];
            for (int i = 0; i < 400; i++)
            {
                s.Seek(rng.Next(0, plain.Length), SeekOrigin.Begin);
                int got = s.Read(buffer, 0, rng.Next(1, Chunk));
                if (got < 0 || got > Chunk)
                    throw new InvalidOperationException("impossible count");
            }
        });
        // after the storm the stream still returns exact data
        byte[] check = new byte[500];
        Assert.Equal(500, s.ReadAt(5 * Chunk - 100, check, 0, 500));
        Assert.True(plain.AsSpan(5 * Chunk - 100, 500).SequenceEqual(check));
    }
}

/// <summary>In-memory stand-in for the player's Arena installation: files by plain relative path, with failure injection and an open log.</summary>
internal sealed class FakeFiles : IArenaFiles
{
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
    public readonly List<string> Opened = new();
    public Func<string, Exception?>? FailLength;
    public Func<string, Exception?>? FailOpen;
    /// <summary>Bytes Open returns instead of the file's (a file that changed between the size check and the read).</summary>
    public Func<string, byte[]?>? Replace;

    public long Length(string relativePath)
    {
        Exception? failure = FailLength?.Invoke(relativePath);
        if (failure != null)
            throw failure;
        return Files.TryGetValue(relativePath, out byte[]? bytes) ? bytes.Length : -1;
    }

    public Stream Open(string relativePath)
    {
        Opened.Add(relativePath);
        Exception? failure = FailOpen?.Invoke(relativePath);
        if (failure != null)
            throw failure;
        return new MemoryStream(Replace?.Invoke(relativePath) ?? Files[relativePath]);
    }

    public FakeFiles Clone()
    {
        var copy = new FakeFiles();
        foreach (var pair in Files)
            copy.Files[pair.Key] = (byte[])pair.Value.Clone();
        return copy;
    }
}

/// <summary>
/// Key derivation, key wrap and key resolution: the pinned labels, the sample of each file (offsets, whole small files, a golden digest from an
/// independent implementation), wrap and unwrap, the deterministic salt, sensitivity to one byte inside the sample, to the order and size of the files
/// and to the sample parameters, several wraps per key file, and every failure cause (E1-E6 of docs/PLAN-ENCRYPTION.md 0.9; key source of section 6).
/// </summary>
public sealed class KeyDerivationTests : IDisposable
{
    const string Map = "arena_test";
    const string Version = "0.6.1.0.48206";
    static readonly string[] Paths =
    {
        "EscapeFromTarkovArena_Data/sharedassets41.assets.resS",
        "EscapeFromTarkovArena_Data/sharedassets41.resource",
        "EscapeFromTarkovArena_Data/sharedassets45.assets.resS",
    };

    /// <summary>8 blocks of 4 KiB: files up to 32 KiB are read whole, the 2.5 MB file through its sample.</summary>
    static readonly KeySample Sample = new(4096, 8);

    readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); }
            catch (Exception) { /* best effort */ }
        }
    }

    // a file read whole, one read through its sample and a tiny one
    static FakeFiles Install(int seed = 0)
    {
        var fs = new FakeFiles();
        fs.Files[Paths[0]] = Crypt.Random(5000, 1 + seed);
        fs.Files[Paths[1]] = Crypt.Random(2_500_000, 2 + seed);
        fs.Files[Paths[2]] = Crypt.Random(100, 3 + seed);
        return fs;
    }

    // straight from the bytes, so the open log of the FakeFiles records only what Resolve opens
    static byte[] Digest(FakeFiles fs, string path, ICryptoProvider p, KeySample? sample = null) =>
        KeyResolver.FileDigest(new MemoryStream(fs.Files[path]), path, fs.Files[path].Length, sample ?? Sample, p, out _);

    static KeyWrap MakeWrap(FakeFiles fs, ICryptoProvider p, string builtFor = Version, string[]? paths = null, byte[]? kMap = null, string map = Map, KeySample? sample = null)
    {
        paths ??= Paths;
        var entries = paths.Select(x => new KeyFileEntry(x, fs.Files[x].Length)).ToList();
        byte[][] digests = paths.Select(x => Digest(fs, x, p, sample)).ToArray();
        return KeyResolver.Wrap(map, kMap ?? Crypt.KMap, builtFor, sample ?? Sample, entries, digests, p);
    }

    static KeyFile KeyOf(params KeyWrap[] wraps)
    {
        var kf = new KeyFile(Map);
        foreach (KeyWrap w in wraps)
            kf.Wraps.Add(w);
        return kf;
    }

    static KeyWrap WithSample(KeyWrap w, KeySample sample) => new(w.BuiltFor, sample, w.Files, w.Salt, w.WrappedKey, w.Check);

    [Theory, ProviderData]
    public void Resolve_RecoversTheContentKey_FromTheArenaFiles(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        FakeFiles fs = Install();
        KeyResolution r = KeyResolver.Resolve(KeyOf(MakeWrap(fs, p)), @"D:\Arena", Version, fs, p, Map);
        Assert.True(r.Ok, r.Detail);
        Assert.Equal(Crypt.ToHex(Crypt.KMap), Crypt.ToHex(r.MapKey));
        Assert.Equal(KeyFailure.None, r.Cause);
        Assert.Equal(0, r.WrapIndex);
        Assert.Equal(3, r.FilesHashed);
        Assert.Equal(5000L + 8 * 4096 + 100, r.BytesHashed);       // the small files whole, the large one through its sample
        Assert.Equal(5000L + 2_500_000 + 100, r.BytesListed);
        Assert.Equal(Version, r.BuiltFor);
        Assert.Equal(p.Name, r.Provider);
        Assert.True(r.Ms >= 0);
        Assert.Null(r.Detail);
        Assert.Equal(Paths, fs.Opened);
    }

    [Fact]
    public void BothProviders_ProduceTheSameWrap_AndEachUnlocksTheOthers()
    {
        ICryptoProvider? cng = CryptoProviders.Cng;
        if (cng == null)
            return;
        FakeFiles fs = Install();
        KeyWrap a = MakeWrap(fs, cng);
        KeyWrap b = MakeWrap(fs, CryptoProviders.Managed);
        Assert.Equal(Crypt.ToHex(a.Salt), Crypt.ToHex(b.Salt));
        Assert.Equal(Crypt.ToHex(a.WrappedKey), Crypt.ToHex(b.WrappedKey));
        Assert.Equal(Crypt.ToHex(a.Check), Crypt.ToHex(b.Check));
        Assert.True(KeyResolver.Resolve(KeyOf(a), "x", Version, fs, CryptoProviders.Managed).Ok);
        Assert.True(KeyResolver.Resolve(KeyOf(b), "x", Version, fs, cng).Ok);
    }

    [Theory, ProviderData]
    public void KekInfo_IsPinned_AndTheKekIsHkdfOfTheIkm(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        Assert.Equal("ArenaMapPort bundle key v2 arena_bowl", Encoding.UTF8.GetString(KeyResolver.KekInfo("arena_bowl")));
        byte[] ikm = Crypt.Random(96, 5);
        byte[] salt = Crypt.Random(32, 6);
        byte[] expected = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 72, salt, Crypt.Ascii("ArenaMapPort bundle key v2 arena_bowl"));
        Kek kek = KeyResolver.DeriveKek(ikm, salt, "arena_bowl", p);
        Assert.Equal(Crypt.ToHex(expected[..32]), Crypt.ToHex(kek.Enc));
        Assert.Equal(Crypt.ToHex(expected[32..64]), Crypt.ToHex(kek.Mac));
        Assert.Equal(Crypt.ToHex(expected[64..72]), Crypt.ToHex(kek.Nonce));
        Kek other = KeyResolver.DeriveKek(ikm, salt, "arena_bay5", p);
        Assert.NotEqual(Crypt.ToHex(kek.Enc), Crypt.ToHex(other.Enc));
    }

    [Fact]
    public void TheSampleOffsets_DependOnlyOnTheSize_StartAtZero_EndAtTheEnd_AndNeverOverlap()
    {
        var sample = new KeySample(4096, 8);
        Assert.Equal(32768, sample.BytesPerFile);
        Assert.True(sample.ReadsWholeFile(32768));
        Assert.False(sample.ReadsWholeFile(32769));
        Assert.Equal(31_488, sample.BytesRead(31_488));
        Assert.Equal(32768, sample.BytesRead(578_163_152));
        // the offsets the independent Python reference printed for 300,000 bytes
        Assert.Equal(new long[] { 0, 42272, 84544, 126816, 169088, 211360, 253632, 295904 }, Enumerable.Range(0, 8).Select(k => sample.BlockOffset(300_000, k)));
        foreach (long size in new long[] { 32769, 40_000, 1_000_003, 578_163_152, 5L << 32 })
        {
            long[] offsets = Enumerable.Range(0, 8).Select(k => sample.BlockOffset(size, k)).ToArray();
            Assert.Equal(0, offsets[0]);
            Assert.Equal(size, offsets[^1] + 4096);
            for (int k = 1; k < offsets.Length; k++)
                Assert.True(offsets[k] >= offsets[k - 1] + 4096, size + ": blocks " + (k - 1) + " and " + k + " overlap");
        }
        Assert.Throws<InvalidOperationException>(() => sample.BlockOffset(32768, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => sample.BlockOffset(300_000, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => sample.BlockOffset(300_000, -1));
    }

    [Theory]
    [InlineData(4096, 2, null)]
    [InlineData(65536, 16, null)]
    [InlineData(1 << 20, 64, null)]
    [InlineData(4095, 16, "block_size")]
    [InlineData(4097, 16, "block_size")]
    [InlineData(0, 16, "block_size")]
    [InlineData((1 << 20) + 4096, 2, "block_size")]
    [InlineData(65536, 1, "blocks")]
    [InlineData(4096, 1025, "blocks")]
    [InlineData(1 << 20, 65, "64 MiB")]
    public void TheSampleRules_AreChecked(int blockSize, int blocks, string? problem)
    {
        string? actual = new KeySample(blockSize, blocks).Problem();
        if (problem == null)
            Assert.Null(actual);
        else
            Assert.Contains(problem, actual);
    }

    [Theory, ProviderData]
    public void FileDigest_IsPinnedByAnIndependentImplementation(string name)
    {
        // Values printed by a 20-line Python reference (hashlib only, no ArenaMapPort code, 2026-10-20) of docs/PLAN-ENCRYPTION.md section 6 over the
        // byte pattern (i * 31 + 7) & 0xFF. If they change, every shipped key file stops working: that needs a new key file schema, not a new constant here.
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        (string Path, int Size, KeySample Sample, string Digest)[] cases =
        {
            ("EscapeFromTarkovArena_Data/sharedassets41.assets.resS", 300_000, new KeySample(4096, 8), "5ca9efc805b8508376d0a7db8f658f5e2c4e253753938269b1ab12d16f19bf27"),
            ("EscapeFromTarkovArena_Data/sharedassets48.resource", 31_488, new KeySample(4096, 8), "03a2793c183a99f9d546de14979eb958bb51d4fada7f5f571455706733c2a2a8"),
            ("EscapeFromTarkovArena_Data/sharedassets95.assets.resS", 1_048_577, new KeySample(65536, 16), "c6d3c1b27fbe2b4e08ef18dc4b8a3a157609803c4072227a4665720959e62a2e"),
        };
        foreach (var c in cases)
        {
            byte[] digest = KeyResolver.FileDigest(new MemoryStream(Crypt.Pattern(c.Size)), c.Path, c.Size, c.Sample, p, out long read);
            Assert.Equal(c.Digest, Crypt.ToHex(digest));
            Assert.Equal(c.Sample.BytesRead(c.Size), read);
        }
        Assert.Equal("ArenaMapPort file sample v2\0a/b\0" + "7\0" + "4096\0" + "8\0",
            Encoding.UTF8.GetString(KeyResolver.SampleHeader("a/b", 7, new KeySample(4096, 8))));
    }

    [Theory, ProviderData]
    public void FileDigest_BindsThePathTheSizeAndTheSample_AndReadsOnlyTheSample(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        byte[] data = Crypt.Random(300_000, 9);
        string path = Paths[0];
        string Hex(string at, long size, KeySample sample, byte[] bytes) => Crypt.ToHex(KeyResolver.FileDigest(new MemoryStream(bytes), at, size, sample, p, out _));
        string baseline = Hex(path, data.Length, Sample, data);
        Assert.NotEqual(baseline, Hex(Paths[2], data.Length, Sample, data));                 // same bytes under another name
        Assert.NotEqual(baseline, Hex(path, data.Length, new KeySample(4096, 9), data));      // another sample
        Assert.NotEqual(baseline, Hex(path, data.Length, new KeySample(8192, 8), data));
        // equal to SHA-256 of the header and the eight blocks, computed here by hand
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            sha.AppendData(KeyResolver.SampleHeader(path, data.Length, Sample));
            for (int k = 0; k < 8; k++)
                sha.AppendData(data, (int)Sample.BlockOffset(data.Length, k), 4096);
            Assert.Equal(Crypt.ToHex(sha.GetHashAndReset()), baseline);
        }
        // a byte between two blocks is not read; a byte inside the first, a middle and the last block is
        long gap = Sample.BlockOffset(data.Length, 3) + 4096 + 100;
        Assert.True(gap < Sample.BlockOffset(data.Length, 4));
        foreach ((long offset, bool read) in new[] { (0L, true), (Sample.BlockOffset(data.Length, 4) + 7, true), (data.Length - 1L, true), (gap, false) })
        {
            byte[] changed = (byte[])data.Clone();
            changed[offset] ^= 0x01;
            Assert.Equal(read, Hex(path, data.Length, Sample, changed) != baseline);
        }
        // a stream that does not hold the listed size
        Assert.Throws<ArenaFileChangedException>(() => KeyResolver.FileDigest(new MemoryStream(data), path, data.Length + 1, Sample, p, out _));
        Assert.Throws<ArenaFileChangedException>(() => KeyResolver.FileDigest(new MemoryStream(new byte[5000]), path, 4999, Sample, p, out _));
        Assert.Throws<ArenaFileChangedException>(() => KeyResolver.FileDigest(new MemoryStream(new byte[5000]), path, 5001, Sample, p, out _));
        Assert.Throws<ArgumentException>(() => KeyResolver.FileDigest(new MemoryStream(data), path, data.Length, new KeySample(100, 8), p, out _));
    }

    [Theory, ProviderData]
    public void Wrap_IsDeterministic_AndTheSaltFollowsItsInputs(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        FakeFiles fs = Install();
        KeyWrap a = MakeWrap(fs, p);
        KeyWrap b = MakeWrap(fs, p);
        Assert.Equal(Crypt.ToHex(a.Salt), Crypt.ToHex(b.Salt));
        Assert.Equal(Crypt.ToHex(a.WrappedKey), Crypt.ToHex(b.WrappedKey));
        Assert.Equal(Crypt.ToHex(a.Check), Crypt.ToHex(b.Check));
        Assert.Equal(32, a.Salt.Length);
        Assert.Equal(32, a.WrappedKey.Length);
        Assert.Equal(32, a.Check.Length);
        Assert.Equal(4096, a.Sample.BlockSize);
        Assert.Equal(8, a.Sample.Blocks);
        Assert.NotEqual(Crypt.ToHex(Crypt.KMap), Crypt.ToHex(a.WrappedKey));

        byte[] otherKey = (byte[])Crypt.KMap.Clone();
        otherKey[5] ^= 1;
        Assert.NotEqual(Crypt.ToHex(a.Salt), Crypt.ToHex(MakeWrap(fs, p, kMap: otherKey).Salt));
        Assert.NotEqual(Crypt.ToHex(a.Salt), Crypt.ToHex(MakeWrap(fs, p, builtFor: "0.6.2.0.1").Salt));
        Assert.NotEqual(Crypt.ToHex(a.Salt), Crypt.ToHex(MakeWrap(fs, p, paths: new[] { Paths[1], Paths[0], Paths[2] }).Salt));
        Assert.NotEqual(Crypt.ToHex(a.Salt), Crypt.ToHex(MakeWrap(fs, p, map: "arena_other").Salt));
        Assert.NotEqual(Crypt.ToHex(a.Salt), Crypt.ToHex(MakeWrap(fs, p, sample: new KeySample(4096, 9)).Salt));
        Assert.Throws<ArgumentException>(() => KeyResolver.Wrap(Map, Crypt.KMap, Version, new KeySample(4096, 1), a.Files, new byte[3][], p));
    }

    [Theory, ProviderData]
    public void Wrap_IsPinnedByAGoldenVector(string name)
    {
        // Fixed inputs (K_map = i * 7 + 1, Bowl's two sharedassets41 files with their 0.6.1 sizes, two digests of short stand-in strings, sample 64 KiB x 16).
        // The three values were first produced by an independent implementation of docs/PLAN-ENCRYPTION.md 0.2 and section 6 in PowerShell on
        // System.Security.Cryptography (no ArenaMapPort code, 2026-10-20). If they change, every shipped key file stops working: that needs a new key file
        // schema, not a new constant here.
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        var files = new List<KeyFileEntry>
        {
            new("EscapeFromTarkovArena_Data/sharedassets41.assets.resS", 578163152),
            new("EscapeFromTarkovArena_Data/sharedassets41.resource", 5433376),
        };
        byte[][] digests =
        {
            SHA256.HashData(Encoding.UTF8.GetBytes("sharedassets41.assets.resS sample")),
            SHA256.HashData(Encoding.UTF8.GetBytes("sharedassets41.resource sample")),
        };
        KeyWrap wrap = KeyResolver.Wrap("arena_test", Crypt.KMap, "0.6.1.0.48206", new KeySample(65536, 16), files, digests, p);
        Assert.Equal("0d2225a7262a73586b27da3b329cf145578799174e1f0dd6b78345a543666338", Crypt.ToHex(wrap.Salt));
        Assert.Equal("d4ca7790cb402b68b845a375231201116729aea8c7757a13e4ef808bbcbdd36f", Crypt.ToHex(wrap.WrappedKey));
        Assert.Equal("c05cda0c89d8d3590f5650767b573c3b04619f3132d1fd3e6ab70078a263ccad", Crypt.ToHex(wrap.Check));

        // and the player side recovers K_map from the same two digests
        byte[] ikm = digests.SelectMany(h => h).ToArray();
        Kek kek = KeyResolver.DeriveKek(ikm, wrap.Salt, "arena_test", p);
        Assert.Equal(Crypt.ToHex(Crypt.KMap), Crypt.ToHex(KeyResolver.Unwrap(wrap, "arena_test", kek, p)!));
    }

    [Theory, ProviderData]
    public void Unwrap_VerifiesTheCheckFirst_AndBindsTheMapKey(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        FakeFiles fs = Install();
        KeyWrap wrap = MakeWrap(fs, p);
        byte[] ikm = Paths.SelectMany(x => Digest(fs, x, p)).ToArray();
        Kek kek = KeyResolver.DeriveKek(ikm, wrap.Salt, Map, p);
        Assert.Equal(Crypt.ToHex(Crypt.KMap), Crypt.ToHex(KeyResolver.Unwrap(wrap, Map, kek, p)!));
        Assert.Null(KeyResolver.Unwrap(wrap, "arena_other", kek, p));

        byte[] badCheck = (byte[])wrap.Check.Clone();
        badCheck[31] ^= 1;
        Assert.Null(KeyResolver.Unwrap(new KeyWrap(wrap.BuiltFor, wrap.Sample, wrap.Files, wrap.Salt, wrap.WrappedKey, badCheck), Map, kek, p));
        byte[] badWrapped = (byte[])wrap.WrappedKey.Clone();
        badWrapped[0] ^= 1;
        Assert.Null(KeyResolver.Unwrap(new KeyWrap(wrap.BuiltFor, wrap.Sample, wrap.Files, wrap.Salt, badWrapped, wrap.Check), Map, kek, p));

        Assert.False(ConstantTime.AreEqual(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }));
        Assert.False(ConstantTime.AreEqual(null!, new byte[1]));
        Assert.True(ConstantTime.AreEqual(Array.Empty<byte>(), Array.Empty<byte>()));
    }

    [Theory, ProviderData]
    public void OneChangedByte_InsideTheSample_GivesContentDiffers_AndOutsideItDoesNot(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        FakeFiles fs = Install();
        KeyFile key = KeyOf(MakeWrap(fs, p));
        long block5 = Sample.BlockOffset(2_500_000, 5);
        foreach ((string path, long offset) in new[] { (Paths[0], 0L), (Paths[0], 4999L), (Paths[1], 0L), (Paths[1], block5 + 4095), (Paths[1], 2_499_999L), (Paths[2], 99L) })
        {
            FakeFiles changed = fs.Clone();
            changed.Files[path][offset] ^= 0x01;
            KeyResolution r = KeyResolver.Resolve(key, "x", Version, changed, p);
            Assert.False(r.Ok);
            Assert.Equal(KeyFailure.ContentDiffers, r.Cause);
            Assert.Null(r.MapKey);
            Assert.Contains("do not unlock the key", r.Detail);
            Assert.Equal(Version, r.BuiltFor);
        }
        // a byte between two blocks of the large file is not part of the key: an update that touches only such bytes keeps the key working
        FakeFiles untouched = fs.Clone();
        untouched.Files[Paths[1]][block5 + 4096 + 1000] ^= 0x01;
        Assert.True(KeyResolver.Resolve(key, "x", Version, untouched, p).Ok);
    }

    [Theory, ProviderData]
    public void TheFileOrder_AndTheSampleParameters_ArePartOfTheKey(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        FakeFiles fs = Install();
        KeyWrap wrap = MakeWrap(fs, p);
        // the same wrap with two entries swapped (each entry keeps its own size, so the sizes still match)
        var swapped = new List<KeyFileEntry> { wrap.Files[1], wrap.Files[0], wrap.Files[2] };
        KeyWrap reordered = new(wrap.BuiltFor, wrap.Sample, swapped, wrap.Salt, wrap.WrappedKey, wrap.Check);
        KeyResolution r = KeyResolver.Resolve(KeyOf(reordered), "x", Version, fs, p);
        Assert.False(r.Ok);
        Assert.Equal(KeyFailure.ContentDiffers, r.Cause);
        // a key file whose sample parameters were edited reads other bytes and does not unlock
        Assert.Equal(KeyFailure.ContentDiffers, KeyResolver.Resolve(KeyOf(WithSample(wrap, new KeySample(4096, 9))), "x", Version, fs, p).Cause);
        Assert.Equal(KeyFailure.ContentDiffers, KeyResolver.Resolve(KeyOf(WithSample(wrap, new KeySample(8192, 8))), "x", Version, fs, p).Cause);
        Assert.True(KeyResolver.Resolve(KeyOf(wrap), "x", Version, fs, p).Ok);
    }

    [Theory, ProviderData]
    public void AMissingOrUnreadableFile_IsArenaFileUnreadable(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        FakeFiles fs = Install();
        KeyFile key = KeyOf(MakeWrap(fs, p));

        FakeFiles missing = fs.Clone();
        missing.Files.Remove(Paths[1]);
        KeyResolution r = KeyResolver.Resolve(key, "x", Version, missing, p);
        Assert.Equal(KeyFailure.ArenaFileUnreadable, r.Cause);
        Assert.Equal(Paths[1], r.FailedPath);
        Assert.Contains("does not exist", r.Detail);

        fs.FailLength = path => path == Paths[2] ? new UnauthorizedAccessException("access denied") : null;
        r = KeyResolver.Resolve(key, "x", Version, fs, p);
        Assert.Equal(KeyFailure.ArenaFileUnreadable, r.Cause);
        Assert.Equal(Paths[2], r.FailedPath);
        Assert.Equal("access denied", r.Detail);

        fs.FailLength = null;
        fs.FailOpen = path => path == Paths[1] ? new IOException("The process cannot access the file") : null;
        r = KeyResolver.Resolve(key, "x", Version, fs, p);
        Assert.Equal(KeyFailure.ArenaFileUnreadable, r.Cause);
        Assert.Equal(Paths[1], r.FailedPath);
        Assert.Contains("cannot access", r.Detail);
    }

    [Theory, ProviderData]
    public void ADifferentSize_IsSizeDiffers_WithTheFactsForBothMessageForms(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        FakeFiles fs = Install();
        KeyFile key = KeyOf(MakeWrap(fs, p, builtFor: "0.6.0.0.47752"));
        FakeFiles other = fs.Clone();
        other.Files[Paths[1]] = other.Files[Paths[1]].Concat(new byte[] { 1 }).ToArray();

        KeyResolution newer = KeyResolver.Resolve(key, "x", "0.6.1.0.48206", other, p);   // "built for X but Y is installed"
        Assert.Equal(KeyFailure.SizeDiffers, newer.Cause);
        Assert.Equal(Paths[1], newer.FailedPath);
        Assert.Equal(2_500_001, newer.ActualSize);
        Assert.Equal(2_500_000, newer.ExpectedSize);
        Assert.Equal("0.6.0.0.47752", newer.BuiltFor);
        Assert.Equal("0.6.1.0.48206", newer.InstalledVersion);
        Assert.Contains("2500001 bytes, 2500000 expected", newer.Detail);

        KeyResolution same = KeyResolver.Resolve(key, "x", "0.6.0.0.47752", other, p);    // equal versions: "differs in size from the key file"
        Assert.Equal(KeyFailure.SizeDiffers, same.Cause);
        Assert.Equal(same.BuiltFor, same.InstalledVersion);
        Assert.Null(KeyResolver.Resolve(key, "x", null, other, p).InstalledVersion);
    }

    [Theory, ProviderData]
    public void AFileThatChangesBetweenTheSizeCheckAndTheRead_IsSizeDiffers(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        FakeFiles fs = Install();
        KeyFile key = KeyOf(MakeWrap(fs, p));
        // the sampled file: its stream reports another length
        fs.Replace = path => path == Paths[1] ? new byte[2_400_000] : null;
        KeyResolution r = KeyResolver.Resolve(key, "x", Version, fs, p);
        Assert.Equal(KeyFailure.SizeDiffers, r.Cause);
        Assert.Equal(Paths[1], r.FailedPath);
        Assert.Equal(2_400_000, r.ActualSize);
        Assert.Equal(2_500_000, r.ExpectedSize);
        Assert.Contains("changed size while it was read", r.Detail);
        // a file read whole that ends early
        fs.Replace = path => path == Paths[0] ? new byte[4000] : null;
        r = KeyResolver.Resolve(key, "x", Version, fs, p);
        Assert.Equal(KeyFailure.SizeDiffers, r.Cause);
        Assert.Equal(Paths[0], r.FailedPath);
        Assert.Equal(4000, r.ActualSize);
    }

    [Theory, ProviderData]
    public void SeveralWraps_TheOneThatAppliesWins_AndTheNewestExplainsAFailure(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        FakeFiles oldBuild = Install(0);
        FakeFiles newBuild = Install(0);
        newBuild.Files[Paths[0]] = Crypt.Random(5200, 99);   // the next Arena build changed one file
        KeyFile key = KeyOf(MakeWrap(oldBuild, p, builtFor: "old"), MakeWrap(newBuild, p, builtFor: "new"));

        KeyResolution onOld = KeyResolver.Resolve(key, "x", "old", oldBuild, p);
        Assert.True(onOld.Ok);
        Assert.Equal(0, onOld.WrapIndex);
        Assert.Equal("old", onOld.BuiltFor);
        KeyResolution onNew = KeyResolver.Resolve(key, "x", "new", newBuild, p);
        Assert.True(onNew.Ok);
        Assert.Equal(1, onNew.WrapIndex);
        Assert.Equal("new", onNew.BuiltFor);
        Assert.Equal(Crypt.ToHex(Crypt.KMap), Crypt.ToHex(onNew.MapKey));

        FakeFiles neither = Install(0);
        neither.Files[Paths[2]] = Crypt.Random(101, 5);
        KeyResolution failed = KeyResolver.Resolve(key, "x", "third", neither, p);
        Assert.Equal(KeyFailure.SizeDiffers, failed.Cause);
        Assert.Equal("new", failed.BuiltFor);                        // reported against the last (newest) wrap
        Assert.Equal(Paths[0], failed.FailedPath);                   // the newest wrap's first mismatch

        // wrong content under the old wrap's sizes while the newest wrap does not apply: the sizes matched somewhere
        FakeFiles modified = oldBuild.Clone();
        modified.Files[Paths[0]][10] ^= 1;
        Assert.Equal(KeyFailure.ContentDiffers, KeyResolver.Resolve(key, "x", "old", modified, p).Cause);
    }

    [Theory, ProviderData]
    public void ResolveFile_ReportsAMissingAndAnInvalidKeyFile(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        string dir = TempDir();
        FakeFiles fs = Install();

        string path = Path.Combine(dir, Map + ".json");
        KeyResolution missing = KeyResolver.ResolveFile(path, "x", Version, fs, p, Map);
        Assert.Equal(KeyFailure.KeyFileMissing, missing.Cause);
        Assert.Contains(path, missing.Detail);
        Assert.Equal(KeyFailure.KeyFileMissing, KeyResolver.ResolveFile(Path.Combine(dir, "nope", "x.json"), "x", Version, fs, p).Cause);

        File.WriteAllText(path, "{ not json");
        KeyResolution invalid = KeyResolver.ResolveFile(path, "x", Version, fs, p, Map);
        Assert.Equal(KeyFailure.KeyFileInvalid, invalid.Cause);
        Assert.False(string.IsNullOrEmpty(invalid.Detail));

        KeyFile good = KeyOf(MakeWrap(fs, p));
        good.Save(path);
        KeyResolution ok = KeyResolver.ResolveFile(path, "x", Version, fs, p, Map);
        Assert.True(ok.Ok, ok.Detail);
        Assert.Equal(Crypt.ToHex(Crypt.KMap), Crypt.ToHex(ok.MapKey));

        KeyResolution wrongMap = KeyResolver.ResolveFile(path, "x", Version, fs, p, "arena_bowl");
        Assert.Equal(KeyFailure.KeyFileInvalid, wrongMap.Cause);
        Assert.Contains("arena_test", wrongMap.Detail);

        // a key file of the retired level-file scheme (schema 1) is refused like any unknown schema
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schema\": 2", "\"schema\": 1"));
        KeyResolution retired = KeyResolver.ResolveFile(path, "x", Version, fs, p, Map);
        Assert.Equal(KeyFailure.KeyFileInvalid, retired.Cause);
        Assert.Equal("unsupported key file schema 1", retired.Detail);

        File.WriteAllBytes(path, new byte[70_000]);
        Assert.Equal(KeyFailure.KeyFileInvalid, KeyResolver.ResolveFile(path, "x", Version, fs, p).Cause);
    }

    [Theory, ProviderData]
    public void Resolve_NeverThrows_AnUnexpectedErrorIsInternal(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        FakeFiles fs = Install();
        KeyFile key = KeyOf(MakeWrap(fs, p));
        fs.FailOpen = _ => new InvalidOperationException("boom");
        KeyResolution r = KeyResolver.Resolve(key, "x", Version, fs, p);
        Assert.Equal(KeyFailure.Internal, r.Cause);
        Assert.Contains("InvalidOperationException: boom", r.Detail);

        Assert.Equal(KeyFailure.KeyFileInvalid, KeyResolver.Resolve(new KeyFile(Map), "x", Version, fs, p).Cause);
        Assert.Equal(KeyFailure.KeyFileInvalid, KeyResolver.Resolve(null!, "x", Version, fs, p).Cause);
        Assert.Equal(KeyFailure.Internal, KeyResolver.Resolve(key, "x", Version, null!, p).Cause);
    }

    [Theory, ProviderData]
    public void SharedFiles_AreHashedOnce_AcrossWraps(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        FakeFiles fs = Install();
        // two wraps over the same files: the first has a wrong check value, the second is good; the files are hashed once for both
        KeyWrap first = MakeWrap(fs, p, builtFor: "a");
        KeyWrap tampered = new(first.BuiltFor, first.Sample, first.Files, first.Salt, first.WrappedKey, new byte[32]);
        KeyWrap second = MakeWrap(fs, p, builtFor: "b");
        KeyResolution r = KeyResolver.Resolve(KeyOf(tampered, second), "x", "b", fs, p);
        Assert.True(r.Ok);
        Assert.Equal(1, r.WrapIndex);
        Assert.Equal(3, r.FilesHashed);
        Assert.Equal(3, fs.Opened.Count);
        // a wrap with other sample parameters reads the files again (other bytes)
        fs.Opened.Clear();
        KeyWrap other = MakeWrap(fs, p, builtFor: "c", sample: new KeySample(8192, 4));
        r = KeyResolver.Resolve(KeyOf(tampered, other), "x", "c", fs, p);
        Assert.True(r.Ok);
        Assert.Equal(6, r.FilesHashed);
    }

    [Theory, ProviderData]
    public void FileDigest_ThroughTheRealFileAccess_EqualsTheStreamDigest(string name)
    {
        ICryptoProvider? p = Crypt.Get(name);
        if (p == null)
            return;
        string dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "EscapeFromTarkovArena_Data"));
        byte[] large = Crypt.Random(1_200_000, 2);
        byte[] small = Crypt.Random(3000, 1);
        File.WriteAllBytes(Path.Combine(dir, "EscapeFromTarkovArena_Data", "sharedassets41.assets.resS"), large);
        File.WriteAllBytes(Path.Combine(dir, "EscapeFromTarkovArena_Data", "sharedassets41.resource"), small);
        var files = new DirectoryArenaFiles(dir);
        foreach ((string rel, byte[] data) in new[] { (Paths[0], large), (Paths[1], small) })
        {
            byte[] fromDisk = KeyResolver.FileDigest(files, new KeyFileEntry(rel, data.Length), Sample, p, out long read);
            byte[] fromMemory = KeyResolver.FileDigest(new MemoryStream(data), rel, data.Length, Sample, p, out _);
            Assert.Equal(Crypt.ToHex(fromMemory), Crypt.ToHex(fromDisk));
            Assert.Equal(Sample.BytesRead(data.Length), read);
        }
        Assert.Throws<ArenaFileChangedException>(() => KeyResolver.FileDigest(files, new KeyFileEntry(Paths[0], large.Length - 1), Sample, p, out _));
        Assert.Throws<FileNotFoundException>(() => KeyResolver.FileDigest(files, new KeyFileEntry(Paths[2], 5), Sample, p, out _));
    }

    [Fact]
    public void DirectoryArenaFiles_ReadsInsideTheInstallation_AndRefusesEverythingElse()
    {
        string dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "EscapeFromTarkovArena_Data"));
        File.WriteAllBytes(Path.Combine(dir, "EscapeFromTarkovArena_Data", "level41"), new byte[] { 1, 2, 3 });
        var files = new DirectoryArenaFiles(dir);
        Assert.Equal(3, files.Length("EscapeFromTarkovArena_Data/level41"));
        Assert.Equal(3, files.Length(@"EscapeFromTarkovArena_Data\level41"));
        Assert.Equal(-1, files.Length("EscapeFromTarkovArena_Data/level42"));
        using (Stream s = files.Open("EscapeFromTarkovArena_Data/level41"))
        {
            Assert.Equal(3, s.Length);
            Assert.True(s.CanSeek);
        }
        foreach (string bad in new[] { "../x", "..", "a/../../x", "C:/Windows/win.ini", "/abs", "", "a//b" })
            Assert.Throws<IOException>(() => files.Length(bad));
        Assert.Throws<IOException>(() => files.Open("../" + Path.GetFileName(dir) + "/EscapeFromTarkovArena_Data/level41"));
        // a shared read while a game process holds the file open for writing
        string locked = Path.Combine(dir, "EscapeFromTarkovArena_Data", "level41");
        using (var writer = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        using (Stream s = files.Open("EscapeFromTarkovArena_Data/level41"))
            Assert.Equal(3, s.Length);
        Assert.Throws<ArgumentException>(() => new DirectoryArenaFiles(" "));
    }

    string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ampb-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }
}

/// <summary>The shipped key file: the canonical writer (pinned), the strict reader (every malformed input), the caps, and the path rule.</summary>
public sealed class KeyFileTests : IDisposable
{
    readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); }
            catch (Exception) { /* best effort */ }
        }
    }

    static byte[] Fill(byte value) => Enumerable.Repeat(value, 32).ToArray();

    static readonly KeySample Sample64K = new(65536, 16);

    static readonly string[] BowlFiles = { "EscapeFromTarkovArena_Data/sharedassets41.assets.resS", "EscapeFromTarkovArena_Data/sharedassets41.resource" };

    static KeyWrap Wrap(string builtFor = "0.6.1.0.48206", int files = 2, byte salt = 0x11) => new(
        builtFor,
        Sample64K,
        Enumerable.Range(0, files).Select(i => new KeyFileEntry(i < 2 ? BowlFiles[i] : "EscapeFromTarkovArena_Data/sharedassets" + (43 + i) + ".assets.resS", i == 0 ? 578163152 : 5433376)).ToList(),
        Fill(salt), Fill(0x22), Fill(0x33));

    static KeyWrap Simple(string builtFor, IList<KeyFileEntry> files, byte[] salt, KeySample? sample = null) => new(builtFor, sample ?? Sample64K, files, salt, Fill(2), Fill(3));

    static KeyFile Sample(int wraps = 1)
    {
        var kf = new KeyFile("arena_bowl");
        for (int i = 0; i < wraps; i++)
            kf.Wraps.Add(Wrap());
        return kf;
    }

    static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    static readonly string H11 = new('1', 64);
    static readonly string H22 = new('2', 64);
    static readonly string H33 = new('3', 64);

    static readonly string Golden = Lines(
        "{",
        "  \"schema\": 2,",
        "  \"map_key\": \"arena_bowl\",",
        "  \"wraps\": [",
        "    {",
        "      \"built_for\": \"0.6.1.0.48206\",",
        "      \"sample\": { \"block_size\": 65536, \"blocks\": 16 },",
        "      \"files\": [",
        "        { \"path\": \"EscapeFromTarkovArena_Data/sharedassets41.assets.resS\", \"size\": 578163152 },",
        "        { \"path\": \"EscapeFromTarkovArena_Data/sharedassets41.resource\", \"size\": 5433376 }",
        "      ],",
        $"      \"salt\": \"{H11}\", \"wrapped_key\": \"{H22}\", \"check\": \"{H33}\"",
        "    }",
        "  ]",
        "}");

    const string ResS = "\"EscapeFromTarkovArena_Data/sharedassets41.assets.resS\"";

    [Fact]
    public void CanonicalWriter_IsPinnedByAGoldenString()
    {
        Assert.Equal(Golden, Sample().ToCanonicalJson());
        byte[] bytes = Sample().ToCanonicalBytes();
        Assert.NotEqual(0xEF, bytes[0]);                      // no byte order mark
        Assert.DoesNotContain((byte)'\r', bytes);             // LF only
        Assert.Equal((byte)'\n', bytes[^1]);                  // trailing newline
    }

    [Fact]
    public void CanonicalWriter_SeparatesSeveralWraps_AndWritesLowercaseHex()
    {
        KeyFile two = Sample();
        two.Wraps.Add(new KeyWrap("0.6.2.0.50000", new KeySample(4096, 2), new List<KeyFileEntry> { new(BowlFiles[0], 5) },
            Enumerable.Repeat((byte)0xAB, 32).ToArray(), Fill(0xCD), Fill(0xEF)));
        string json = two.ToCanonicalJson();
        Assert.Contains("    },\n    {\n      \"built_for\": \"0.6.2.0.50000\",\n      \"sample\": { \"block_size\": 4096, \"blocks\": 2 },", json);
        Assert.Contains(string.Concat(Enumerable.Repeat("ab", 32)), json);
        Assert.DoesNotContain("AB", json);
        Assert.Equal(json, KeyFile.Parse(json).ToCanonicalJson());
        Assert.Equal(2, KeyFile.Parse(json).Wraps.Count);
        Assert.Equal(4096, KeyFile.Parse(json).Wraps[1].Sample.BlockSize);
        Assert.Equal("{\n  \"schema\": 2,\n  \"map_key\": \"arena_bowl\",\n  \"wraps\": []\n}\n", new KeyFile("arena_bowl").ToCanonicalJson());
        Assert.Empty(KeyFile.Parse(new KeyFile("arena_bowl").ToCanonicalJson()).Wraps);
    }

    [Fact]
    public void Parse_RoundTrips_TheCanonicalForm()
    {
        KeyFile parsed = KeyFile.Parse(Golden);
        Assert.Equal("arena_bowl", parsed.MapKey);
        KeyWrap wrap = Assert.Single(parsed.Wraps);
        Assert.Equal("0.6.1.0.48206", wrap.BuiltFor);
        Assert.Equal(65536, wrap.Sample.BlockSize);
        Assert.Equal(16, wrap.Sample.Blocks);
        Assert.Equal(2, wrap.Files.Count);
        Assert.Equal(BowlFiles[0], wrap.Files[0].Path);
        Assert.Equal(578163152, wrap.Files[0].Size);
        Assert.Equal(5433376, wrap.Files[1].Size);
        Assert.Equal(H11, Crypt.ToHex(wrap.Salt));
        Assert.Equal(H22, Crypt.ToHex(wrap.WrappedKey));
        Assert.Equal(H33, Crypt.ToHex(wrap.Check));
        Assert.Equal(Golden, parsed.ToCanonicalJson());
        Assert.Equal(Golden, KeyFile.Parse(Encoding.UTF8.GetBytes(Golden)).ToCanonicalJson());

        string dir = Path.Combine(Path.GetTempPath(), "keyfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        string path = Path.Combine(dir, "arena_bowl.json");
        Sample().Save(path);
        Assert.Equal(Golden, File.ReadAllText(path));
        Assert.Equal(Golden, KeyFile.Load(path).ToCanonicalJson());
        Assert.Throws<FileNotFoundException>(() => KeyFile.Load(Path.Combine(dir, "absent.json")));
        File.WriteAllBytes(path, new byte[KeyFile.MaxBytes + 1]);
        Assert.Contains("64 KiB", Assert.Throws<KeyFileException>(() => KeyFile.Load(path)).Message);
    }

    [Fact]
    public void Parse_ToleratesABom_Escapes_UnknownKeys_AndWhitespaceVariants()
    {
        Assert.Equal(Golden, KeyFile.Parse("\uFEFF" + Golden).ToCanonicalJson());
        Assert.Equal(Golden, KeyFile.Parse(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Golden)).ToArray()).ToCanonicalJson());
        Assert.Equal(Golden, KeyFile.Parse(Golden.Replace("\n", "\r\n")).ToCanonicalJson());
        Assert.Equal(Golden, KeyFile.Parse(Golden.Replace("\n", "")).ToCanonicalJson());

        string escaped = Golden.Replace("\"0.6.1.0.48206\"", "\"0.6.\\u0031.0.\\u00348206\"").Replace(BowlFiles[0], "EscapeFromTarkovArena_Data\\/sharedassets41.assets.resS");
        KeyFile parsed = KeyFile.Parse(escaped);
        Assert.Equal("0.6.1.0.48206", parsed.Wraps[0].BuiltFor);
        Assert.Equal(BowlFiles[0], parsed.Wraps[0].Files[0].Path);

        string unknown = Golden
            .Replace("\"schema\": 2,", "\"schema\": 2, \"comment\": {\"a\": [1, 2.5e3, true, null, \"x\", {\"b\": []}]},")
            .Replace("\"built_for\"", "\"future\": [[[]]], \"built_for\"")
            .Replace("{ \"block_size\"", "{ \"stride\": 3, \"block_size\"")
            .Replace("{ \"path\"", "{ \"note\": \"n\", \"path\"");
        Assert.Equal(Golden, KeyFile.Parse(unknown).ToCanonicalJson());
        Assert.Equal(Golden, KeyFile.Parse(Golden.Replace(H11, H11.ToUpperInvariant())).ToCanonicalJson());
    }

    [Theory]
    [InlineData("\"schema\": 2", "\"schema\": 1", "unsupported key file schema 1")]
    [InlineData("\"schema\": 2", "\"schema\": 3", "schema")]
    [InlineData("\"schema\": 2", "\"schema\": 0", "schema")]
    [InlineData("\"schema\": 2", "\"schema\": \"2\"", "integer")]
    [InlineData("\"schema\": 2", "\"schema\": 2.0", "integer")]
    [InlineData("\"schema\": 2,", "", "'schema'")]
    [InlineData("\"map_key\"", "\"mapkey\"", "'map_key'")]
    [InlineData("\"arena_bowl\"", "\"Arena_Bowl\"", "map_key")]
    [InlineData("\"arena_bowl\"", "\"\"", "map_key")]
    [InlineData("\"arena_bowl\"", "7", "string")]
    [InlineData("\"wraps\"", "\"wrapz\"", "'wraps'")]
    [InlineData("\"wraps\": [", "\"wraps\": 5, \"x\": [", "array")]
    [InlineData("\"built_for\": \"0.6.1.0.48206\",", "", "'built_for'")]
    [InlineData("\"0.6.1.0.48206\"", "\"0.6\\n1\"", "control character")]
    [InlineData("\"sample\": { \"block_size\": 65536, \"blocks\": 16 },", "", "'sample'")]
    [InlineData("\"sample\": { \"block_size\": 65536, \"blocks\": 16 }", "\"sample\": [65536, 16]", "'sample' must be an object")]
    [InlineData("\"block_size\": 65536, ", "", "'block_size'")]
    [InlineData(", \"blocks\": 16", "", "'blocks'")]
    [InlineData("\"block_size\": 65536", "\"block_size\": 65537", "block_size must be a multiple of 4096")]
    [InlineData("\"block_size\": 65536", "\"block_size\": 2048", "block_size must be a multiple of 4096")]
    [InlineData("\"block_size\": 65536", "\"block_size\": 2097152", "block_size must be a multiple of 4096")]
    [InlineData("\"block_size\": 65536", "\"block_size\": -4096", "non-negative")]
    [InlineData("\"block_size\": 65536", "\"block_size\": 4294967296", "non-negative")]
    [InlineData("\"block_size\": 65536", "\"block_size\": \"65536\"", "integer")]
    [InlineData("\"blocks\": 16", "\"blocks\": 1", "blocks must be from 2")]
    [InlineData("\"blocks\": 16", "\"blocks\": 1025", "blocks must be from 2")]
    [InlineData("\"blocks\": 16", "\"blocks\": 1.5", "integer")]
    [InlineData("\"block_size\": 65536, \"blocks\": 16", "\"block_size\": 1048576, \"blocks\": 65", "64 MiB")]
    [InlineData("\"files\"", "\"fils\"", "'files'")]
    [InlineData(ResS, "\"../level41\"", "plain relative path")]
    [InlineData(ResS, "\"C:/level41\"", "plain relative path")]
    [InlineData(ResS, "\"/level41\"", "plain relative path")]
    [InlineData(ResS, "\"a/.. /b\"", "plain relative path")]
    [InlineData(ResS, "\"\"", "plain relative path")]
    [InlineData(ResS, "4", "string")]
    [InlineData("\"size\": 578163152", "\"size\": -1", "negative")]
    [InlineData("\"size\": 578163152", "\"size\": 1.5", "integer")]
    [InlineData("\"size\": 578163152", "\"size\": \"5\"", "integer")]
    [InlineData("\"size\": 578163152", "\"size\": 99999999999999999999", "integer")]
    [InlineData("\"size\": 578163152", "\"size\": 007", "leading zero")]
    [InlineData("\"schema\": 2,", "\"schema\": 2,,", "object key")]
    [InlineData("check\": \"3333", "check\": \"3333\u0001", "control character")]
    [InlineData("\"schema\": 2", "\"schema\": tru", "literal")]
    [InlineData("\"schema\": 2", "\"schema\": 2e", "number")]
    [InlineData("\"schema\": 2", "\"schema\": -", "number")]
    [InlineData("\"schema\": 2", "\"schema\": '2'", "unexpected character")]
    [InlineData("\"schema\": 2,", "\"schema\": 2, \"schema\": 2,", "duplicate key 'schema'")]
    [InlineData("\"built_for\": \"0.6.1.0.48206\",", "\"built_for\": \"a\", \"built_for\": \"b\",", "duplicate key 'built_for'")]
    [InlineData("\"blocks\": 16", "\"blocks\": 16, \"blocks\": 16", "duplicate key 'blocks'")]
    [InlineData("\"schema\": 2,", "\"schema\": 2, \"x\": \"\\q\",", "invalid escape")]
    [InlineData("\"schema\": 2,", "\"schema\": 2, \"x\": \"\\u12\",", "\\u")]
    public void Parse_RefusesMalformedInput(string find, string replace, string reasonContains)
    {
        Assert.Contains(find, Golden);
        KeyFileException ex = Assert.Throws<KeyFileException>(() => KeyFile.Parse(Golden.Replace(find, replace)));
        Assert.Contains(reasonContains, ex.Message);
    }

    [Fact]
    public void Parse_RefusesAWrapWithoutFiles()
    {
        string none = Golden.Replace(
            "      \"files\": [\n        { \"path\": \"EscapeFromTarkovArena_Data/sharedassets41.assets.resS\", \"size\": 578163152 },\n" +
            "        { \"path\": \"EscapeFromTarkovArena_Data/sharedassets41.resource\", \"size\": 5433376 }\n      ],", "      \"files\": [],");
        Assert.NotEqual(Golden, none);
        Assert.Contains("lists no files", Assert.Throws<KeyFileException>(() => KeyFile.Parse(none)).Message);
    }

    [Fact]
    public void Parse_RefusesBadHexAndTrailingContent()
    {
        string[] badValues =
        {
            H11[..62], H11 + "11", H11[..63] + "z", H11[..63] + "-", "", " " + H11[..63], "0x" + H11[..62],
        };
        foreach (string bad in badValues)
        {
            Assert.Contains("64 hex", Assert.Throws<KeyFileException>(() => KeyFile.Parse(Golden.Replace(H11, bad))).Message);
            Assert.Contains("64 hex", Assert.Throws<KeyFileException>(() => KeyFile.Parse(Golden.Replace(H22, bad))).Message);
            Assert.Contains("64 hex", Assert.Throws<KeyFileException>(() => KeyFile.Parse(Golden.Replace(H33, bad))).Message);
        }
        Assert.Contains("string", Assert.Throws<KeyFileException>(() => KeyFile.Parse(Golden.Replace("\"" + H33 + "\"", "null"))).Message);
        Assert.Contains("string", Assert.Throws<KeyFileException>(() => KeyFile.Parse(Golden.Replace("\"" + H11 + "\"", "5"))).Message);
        Assert.Contains("unexpected content", Assert.Throws<KeyFileException>(() => KeyFile.Parse(Golden + "x")).Message);
        Assert.Contains("unexpected content", Assert.Throws<KeyFileException>(() => KeyFile.Parse(Golden.TrimEnd() + "}")).Message);
        Assert.Contains("unexpected content", Assert.Throws<KeyFileException>(() => KeyFile.Parse(Golden + Golden)).Message);
    }

    [Fact]
    public void Parse_RefusesAnythingThatIsNotAnObject_OrNotUtf8_OrTooDeep()
    {
        foreach (string text in new[] { "", "   ", "[]", "5", "\"x\"", "null", "{", "{\"a\"", "{\"a\":", "{\"a\":1", "[1,]", "tru" })
            Assert.Throws<KeyFileException>(() => KeyFile.Parse(text));
        Assert.Throws<KeyFileException>(() => KeyFile.Parse((string)null!));
        Assert.Throws<KeyFileException>(() => KeyFile.Parse((byte[])null!));
        Assert.Contains("UTF-8", Assert.Throws<KeyFileException>(() => KeyFile.Parse(new byte[] { 0x7B, 0xFF, 0xFE, 0x7D })).Message);
        string deep = Golden.Replace("\"schema\": 2,", "\"schema\": 2, \"x\": " + new string('[', 40) + new string(']', 40) + ",");
        Assert.Contains("too deeply", Assert.Throws<KeyFileException>(() => KeyFile.Parse(deep)).Message);
        string fine = Golden.Replace("\"schema\": 2,", "\"schema\": 2, \"x\": " + new string('[', 10) + new string(']', 10) + ",");
        Assert.Equal(Golden, KeyFile.Parse(fine).ToCanonicalJson());
    }

    [Fact]
    public void Caps_AreEnforced_OnSeventeenWraps_ThirtyThreeFiles_AndSixtyFiveKiB()
    {
        string WrapJson(int files) =>
            "{\"built_for\":\"x\",\"sample\":{\"block_size\":65536,\"blocks\":16},\"files\":[" +
            string.Join(",", Enumerable.Range(0, files).Select(i => $"{{\"path\":\"a/f{i}\",\"size\":{i}}}")) +
            $"],\"salt\":\"{H11}\",\"wrapped_key\":\"{H22}\",\"check\":\"{H33}\"}}";
        string Doc(int wraps, int files) =>
            "{\"schema\":2,\"map_key\":\"arena_bowl\",\"wraps\":[" + string.Join(",", Enumerable.Repeat(WrapJson(files), wraps)) + "]}";

        Assert.Equal(16, KeyFile.Parse(Doc(16, 32)).Wraps.Count);
        Assert.Contains("more than 16 wraps", Assert.Throws<KeyFileException>(() => KeyFile.Parse(Doc(17, 1))).Message);
        Assert.Contains("more than 32 files", Assert.Throws<KeyFileException>(() => KeyFile.Parse(Doc(1, 33))).Message);
        Assert.Equal(32, KeyFile.Parse(Doc(1, 32)).Wraps[0].Files.Count);

        string padded = Doc(1, 1) + new string(' ', 65 * 1024);
        Assert.Contains("64 KiB", Assert.Throws<KeyFileException>(() => KeyFile.Parse(padded)).Message);
        Assert.Contains("64 KiB", Assert.Throws<KeyFileException>(() => KeyFile.Parse(Encoding.UTF8.GetBytes(padded))).Message);
        string exact = Doc(1, 1) + new string(' ', KeyFile.MaxBytes - Doc(1, 1).Length);
        Assert.Single(KeyFile.Parse(exact).Wraps);
    }

    [Fact]
    public void Writer_RefusesWhatTheReaderWouldRefuse()
    {
        KeyFile tooMany = Sample(17);
        Assert.Throws<KeyFileException>(() => tooMany.ToCanonicalJson());
        Assert.Throws<KeyFileException>(() => new KeyFile("Arena").ToCanonicalJson());
        KeyFile bad = new("arena_bowl");
        bad.Wraps.Add(Simple("v", new List<KeyFileEntry> { new("../x", 1) }, Fill(1)));
        Assert.Throws<KeyFileException>(() => bad.ToCanonicalJson());
        KeyFile shortSalt = new("arena_bowl");
        shortSalt.Wraps.Add(Simple("v", new List<KeyFileEntry> { new("a/b", 1) }, new byte[31]));
        Assert.Throws<KeyFileException>(() => shortSalt.ToCanonicalJson());
        KeyFile longBuilt = new("arena_bowl");
        longBuilt.Wraps.Add(Simple(new string('x', 65), new List<KeyFileEntry> { new("a/b", 1) }, Fill(1)));
        Assert.Throws<KeyFileException>(() => longBuilt.ToCanonicalJson());
        KeyFile negative = new("arena_bowl");
        negative.Wraps.Add(Simple("v", new List<KeyFileEntry> { new("a/b", -1) }, Fill(1)));
        Assert.Throws<KeyFileException>(() => negative.ToCanonicalJson());
        KeyFile tooManyFiles = new("arena_bowl");
        tooManyFiles.Wraps.Add(Wrap(files: 33));
        Assert.Throws<KeyFileException>(() => tooManyFiles.ToCanonicalJson());
        KeyFile noFiles = new("arena_bowl");
        noFiles.Wraps.Add(Simple("v", new List<KeyFileEntry>(), Fill(1)));
        Assert.Contains("lists no files", Assert.Throws<KeyFileException>(() => noFiles.ToCanonicalJson()).Message);
        KeyFile noSample = new("arena_bowl");
        noSample.Wraps.Add(new KeyWrap("v", null!, new List<KeyFileEntry> { new("a/b", 1) }, Fill(1), Fill(2), Fill(3)));
        Assert.Contains("has no sample", Assert.Throws<KeyFileException>(() => noSample.ToCanonicalJson()).Message);
        KeyFile badSample = new("arena_bowl");
        badSample.Wraps.Add(Simple("v", new List<KeyFileEntry> { new("a/b", 1) }, Fill(1), new KeySample(65536, 1)));
        Assert.Contains("sample blocks", Assert.Throws<KeyFileException>(() => badSample.ToCanonicalJson()).Message);
        KeyFile quoted = new("arena_bowl");
        quoted.Wraps.Add(Simple("a\"b\\c", new List<KeyFileEntry> { new("a/b", 1) }, Fill(1)));
        Assert.Equal("a\"b\\c", KeyFile.Parse(quoted.ToCanonicalJson()).Wraps[0].BuiltFor);
    }

    [Theory]
    // accepted (key file spelling, '/' separated) and the same rule's verdict for ArenaInstallation's '\' spelling
    [InlineData("EscapeFromTarkovArena_Data/level41", true)]
    [InlineData(@"EscapeFromTarkovArena_Data\level41", true)]
    [InlineData("level41", true)]
    [InlineData("a/b/c", true)]
    [InlineData("my folder/file name.dll", true)]
    [InlineData("a/..b/c", true)]
    [InlineData("a/b.c.d", true)]
    [InlineData("a/.hidden", true)]
    [InlineData("A/B", true)]
    [InlineData("x/y/z ", true)]
    [InlineData("a/b./c", true)]
    [InlineData("nul", true)]
    // refused
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("...", false)]
    [InlineData(".. ", false)]
    [InlineData(" ", false)]
    [InlineData("../x", false)]
    [InlineData(".. /x", false)]
    [InlineData("a/.. /../x", false)]
    [InlineData("a/.. ", false)]
    [InlineData(".../x", false)]
    [InlineData("a/./x", false)]
    [InlineData("a/ /x", false)]
    [InlineData("a//x", false)]
    [InlineData("a/", false)]
    [InlineData("/a", false)]
    [InlineData("//a/b", false)]
    [InlineData("C:/a", false)]
    [InlineData("C:a", false)]
    [InlineData("a:b", false)]
    [InlineData("foo:bar/x", false)]
    [InlineData(@"\a", false)]
    [InlineData(@"\\a\b", false)]
    [InlineData(@"a\..\b", false)]
    [InlineData(@"a\ \b", false)]
    [InlineData(@"a\\b", false)]
    [InlineData(@"a\", false)]
    [InlineData("a/b/..", false)]
    [InlineData("a/b/../c", false)]
    [InlineData("a/b/. ", false)]
    [InlineData("a/b/...", false)]
    [InlineData("a/b/..  ", false)]
    public void KeyPath_EqualsTheArenaInstallationRule(string path, bool expected)
    {
        Assert.Equal(expected, KeyPath.IsSafe(path));
        Assert.Equal(expected, ArenaInstallation.IsPlainRelativePath(path.Replace('/', '\\')));
        Assert.Equal(ArenaInstallation.IsPlainRelativePath(path.Replace('/', '\\')), KeyPath.IsSafe(path));
    }

    [Theory]
    [InlineData("a/b\u0001c")]
    [InlineData("a/b\tc")]
    [InlineData("a\n/b")]
    [InlineData("a\0b")]
    public void KeyPath_AlsoRefusesControlCharacters(string path)
    {
        Assert.False(KeyPath.IsSafe(path));
        string json = Golden.Replace(ResS, JsonSerializer.Serialize(path));
        Assert.Contains("plain relative path", Assert.Throws<KeyFileException>(() => KeyFile.Parse(json)).Message);
    }
}

/// <summary>
/// Real data, skipped without the developer's Arena installation (and without staged key files): the four maps' key sources on this machine. The
/// environment variable ARENAMAPPORT_SECOND_ARENA names a second, read-only Arena folder of another build (2026-10-20: the user's 0.6.0 install on a
/// network share); when it is set, the staged key files must unlock against it too.
/// </summary>
public sealed class CryptoRealDataTests
{
    readonly ITestOutputHelper _out;

    public CryptoRealDataTests(ITestOutputHelper output) => _out = output;

    // files and listed bytes per map on Arena 0.6.1.0.48206 (the raw resource streams of tools/bundle/key-sources.json)
    static readonly (string Key, int Files, long Bytes)[] Maps =
    {
        ("arena_equator", 3, 218_007_984),
        ("arena_bay5", 8, 358_204_848),
        ("arena_airpit", 7, 603_532_272),
        ("arena_bowl", 8, 754_464_336),
    };

    static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "AGENTS.md")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    /// <summary>The sample and the files of one map, read from tools/bundle/key-sources.json the way the build tool reads them.</summary>
    static (KeySample Sample, string[] Paths) Sources(string key)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tools", "bundle", "key-sources.json"), Encoding.UTF8));
        JsonElement root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("schema").GetInt32());
        JsonElement sample = root.GetProperty("sample");
        var paths = new List<string>();
        foreach (JsonElement scene in root.GetProperty("maps").GetProperty(key).EnumerateArray())
            foreach (JsonElement name in scene.GetProperty("files").EnumerateArray())
                paths.Add("EscapeFromTarkovArena_Data/" + name.GetString());
        return (new KeySample(sample.GetProperty("block_size").GetInt32(), sample.GetProperty("blocks").GetInt32()), paths.ToArray());
    }

    [RealInstallFact]
    public void TheFourMapsDeriveTheirKeyFromTheInstalledResourceStreams()
    {
        string manifest = File.ReadAllText(Path.Combine(RealArena.Folder, "ConsistencyInfo"), Encoding.UTF8);
        using JsonDocument document = JsonDocument.Parse(manifest);
        string installed = document.RootElement.GetProperty("Version").GetString()!;
        var files = new DirectoryArenaFiles(RealArena.Folder);
        ICryptoProvider p = CryptoProviders.Select(null);
        foreach ((string key, int count, long bytes) in Maps)
        {
            (KeySample sample, string[] paths) = Sources(key);
            Assert.Equal(count, paths.Length);
            long total = 0;
            var entries = new List<KeyFileEntry>();
            foreach (string path in paths)
            {
                long length = files.Length(path);
                Assert.True(length > 0, key + ": " + path + " is missing");
                total += length;
                entries.Add(new KeyFileEntry(path, length));
            }
            if (installed == "0.6.1.0.48206")
                Assert.Equal(bytes, total);
            byte[] kMap = Crypt.Random(32, key.Length);
            byte[][] digests = entries.Select(e => KeyResolver.FileDigest(files, e, sample, p, out _)).ToArray();
            KeyFile file = new(key);
            file.Wraps.Add(KeyResolver.Wrap(key, kMap, installed, sample, entries, digests, p));
            KeyResolution r = KeyResolver.Resolve(KeyFile.Parse(file.ToCanonicalJson()), RealArena.Folder, installed, files, p, key);
            Assert.True(r.Ok, key + ": " + r.Detail);
            Assert.Equal(Crypt.ToHex(kMap), Crypt.ToHex(r.MapKey));
            Assert.Equal(paths.Length, r.FilesHashed);
            Assert.Equal(total, r.BytesListed);
            Assert.Equal(entries.Sum(e => sample.BytesRead(e.Size)), r.BytesHashed);
            Assert.True(r.BytesHashed <= paths.Length * sample.BytesPerFile);
            _out.WriteLine($"{key}: {paths.Length} files, {r.BytesHashed:N0} of {total:N0} bytes sampled, key derived in {r.Ms} ms ({r.Provider}), Arena {installed}");
        }
    }

    [RealInstallFact]
    public void StagedKeyFiles_UnlockTheKeyStoreKeys_WhenTheyExist()
    {
        string root = RepoRoot();
        string keysDir = Path.Combine(root, "dist", "BepInEx", "plugins", "maschine-ArenaMapPort", "keys");
        string storeDir = Path.Combine(root, "work", "release-keys");
        var installs = new List<string> { RealArena.Folder };
        string? second = Environment.GetEnvironmentVariable("ARENAMAPPORT_SECOND_ARENA");
        if (!string.IsNullOrWhiteSpace(second))
            installs.Add(second);
        int checkedMaps = 0;
        foreach ((string key, _, _) in Maps)
        {
            string keyFile = Path.Combine(keysDir, key + ".json");
            string store = Path.Combine(storeDir, key + ".key");
            if (!File.Exists(keyFile) || !File.Exists(store))
            {
                _out.WriteLine($"{key}: no staged key file or key store entry yet, skipped");
                continue;
            }
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(store, Encoding.UTF8));
            string kMapHex = doc.RootElement.GetProperty("k_map").GetString()!;
            foreach (string install in installs)
            {
                KeyResolution r = KeyResolver.ResolveFile(keyFile, install, null, new DirectoryArenaFiles(install), CryptoProviders.Select(null), key);
                Assert.True(r.Ok, key + " against " + install + ": " + r.Detail);
                Assert.Equal(kMapHex.ToLowerInvariant(), Crypt.ToHex(r.MapKey));
                _out.WriteLine($"{key}: staged key file unlocks the key store's K_map against {install} in {r.Ms} ms ({r.BytesHashed:N0} of {r.BytesListed:N0} bytes sampled)");
            }
            checkedMaps++;
        }
        _out.WriteLine($"{checkedMaps} of {Maps.Length} maps checked against {installs.Count} installation(s)");
    }
}

/// <summary>
/// Throughput of both providers on 64 MiB (this VM; the numbers are printed so the progress log can quote them). The CNG path must reach 300 MiB/s
/// for encrypting and for decrypting, or the loading budget of docs/PLAN-ENCRYPTION.md 2.4 cannot hold. The managed provider on .NET 10 is itself
/// OS-backed, so its numbers here say nothing about Mono's managed crypto in EFT. Other test classes run in parallel, so each figure is the best of five.
/// </summary>
public sealed class CryptoThroughputTests
{
    const int Size = 64 << 20;
    readonly ITestOutputHelper _out;

    public CryptoThroughputTests(ITestOutputHelper output) => _out = output;

    static double Best(int runs, Action action)
    {
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            action();
            best = Math.Min(best, (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency);
        }
        return Size / (1 << 20) / best;
    }

    [Fact]
    public void Cng_ReachesAtLeast300MiBPerSecond_EncryptingAndDecrypting64MiB()
    {
        if (!OperatingSystem.IsWindows())
            return;
        byte[] plain = Crypt.Random(Size, 1);
        double cngEncrypt = 0, cngDecrypt = 0;
        foreach (ICryptoProvider p in new[] { CryptoProviders.Managed, CryptoProviders.Cng! })
        {
            byte[] container = Crypt.Encrypt(plain, p, 1 << 20);
            double encrypt = Best(5, () =>
            {
                using var dest = new MemoryStream(Size + (2 << 20));
                Ampb.Encrypt(new MemoryStream(plain, false), dest, Crypt.Map, Crypt.KMap, p);
            });
            double decrypt = Best(5, () => Ampb.Decrypt(new MemoryStream(container, false), Stream.Null, Crypt.KMap, p));

            byte[] scratch = new byte[1 << 20];
            byte[] key = Crypt.Random(32, 3);
            byte[] nonce = new byte[8];
            double ctr = Best(5, () =>
            {
                using IAesKey aes = p.NewAes(key);
                for (int off = 0; off < Size; off += 1 << 20)
                    AesCtr.Transform(aes, nonce, (ulong)off / 16, plain, off, scratch, 0, 1 << 20);
            });
            double sha = Best(5, () =>
            {
                for (int off = 0; off < Size; off += 1 << 20)
                    p.Sha256(plain, off, 1 << 20);
            });
            double hmac = Best(5, () =>
            {
                using IHashStream h = p.NewHmacSha256(key);
                for (int off = 0; off < Size; off += 1 << 20)
                    h.Update(plain, off, 1 << 20);
                h.Finish();
            });
            _out.WriteLine($"THROUGHPUT {p.Name,-8} 64 MiB: encrypt {encrypt,7:F0} MiB/s, decrypt {decrypt,7:F0} MiB/s | AES-CTR {ctr,7:F0}, SHA-256 {sha,7:F0}, HMAC stream {hmac,7:F0} MiB/s");
            if (p.Name == "CNG")
            {
                cngEncrypt = encrypt;
                cngDecrypt = decrypt;
            }
        }
        Assert.True(cngDecrypt >= 300, $"CNG decrypts at {cngDecrypt:F0} MiB/s, below 300");
        Assert.True(cngEncrypt >= 300, $"CNG encrypts at {cngEncrypt:F0} MiB/s, below 300");
    }
}
