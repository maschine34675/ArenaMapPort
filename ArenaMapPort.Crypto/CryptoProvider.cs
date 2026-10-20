#nullable disable
using System;
using System.Text;

namespace ArenaMapPort.Crypto
{
    /// <summary>Streaming SHA-256 or HMAC-SHA-256 state: <see cref="Update"/> any number of times, then <see cref="Finish"/> once (it releases the state).</summary>
    public interface IHashStream : IDisposable
    {
        void Update(byte[] data, int offset, int count);
        /// <summary>The 32-byte digest. The stream cannot be used afterwards.</summary>
        byte[] Finish();
    }

    /// <summary>Streaming SHA-256 (a key file's Arena files are hashed block by block, never through one large buffer).</summary>
    public interface ISha256Stream : IHashStream { }

    /// <summary>
    /// One AES-256 key for ECB encryption of counter blocks (CTR is built on top in <see cref="AesCtr"/>). On Windows this owns
    /// one CNG key handle that is reused for every call. An instance is NOT thread-safe: <see cref="AmpbReadStream"/> serialises its calls.
    /// </summary>
    public interface IAesKey : IDisposable
    {
        /// <summary>Encrypts <paramref name="count"/> bytes (a multiple of 16) block by block (ECB, no padding). Source and destination may be the same range.</summary>
        void EncryptEcb(byte[] src, int srcOffset, byte[] dst, int dstOffset, int count);
    }

    /// <summary>
    /// The three primitives the container and the key derivation need: SHA-256, HMAC-SHA-256 and AES-256 (ECB blocks). Two
    /// implementations exist: Windows CNG through P/Invoke (fast, EFT's Mono has no hardware crypto in managed code) and
    /// System.Security.Cryptography (the fallback and the reference). Both must produce identical bytes; BundleCryptoTests pins it.
    /// </summary>
    public interface ICryptoProvider
    {
        /// <summary>"CNG" or "managed" (used in the Debug lines).</summary>
        string Name { get; }
        byte[] Sha256(byte[] data, int offset, int count);
        /// <summary>HMAC-SHA-256; the key may be empty or longer than the 64-byte block (RFC 2104 rules).</summary>
        byte[] HmacSha256(byte[] key, byte[] data, int offset, int count);
        IAesKey NewAes(byte[] key32);
        ISha256Stream NewSha256();
        IHashStream NewHmacSha256(byte[] key);
    }

    /// <summary>
    /// Picks the crypto provider once per process: Windows CNG when it can be created and passes its known-answer self-test, otherwise the
    /// managed provider with one warning. The warning text is handed to the caller (this folder never logs: every log line of the feature
    /// comes from the game thread). <see cref="Managed"/> is always available; <see cref="Cng"/> is null when it is not.
    /// Proved by BundleCryptoTests (both providers run through every container test; the fallback paths run on a ProviderSelector with a failing probe).
    /// </summary>
    public static class CryptoProviders
    {
        static readonly ICryptoProvider ManagedInstance = new ManagedProvider();
        static readonly ProviderSelector Default = new ProviderSelector(ManagedInstance, CreateCng);

        /// <summary>Test seam: when set, <see cref="Select"/> returns it. Internal, so only code compiled into the same assembly (the tests) can use it.</summary>
        internal static ICryptoProvider Override { get; set; }

        /// <summary>System.Security.Cryptography (Aes ECB, SHA256, HMACSHA256). Always available.</summary>
        public static ICryptoProvider Managed
        {
            get { return ManagedInstance; }
        }

        /// <summary>The Windows CNG provider, or null when it is unavailable (not Windows, no bcrypt.dll, self-test failed).</summary>
        public static ICryptoProvider Cng
        {
            get { return Default.Cng; }
        }

        /// <summary>Why <see cref="Cng"/> is null (null when it is available).</summary>
        public static string CngUnavailableReason
        {
            get { return Default.CngUnavailableReason; }
        }

        /// <summary>
        /// The provider to use: CNG when available, else the managed one. When CNG is unavailable <paramref name="warn"/> receives the
        /// one-line warning (W1 of docs/PLAN-ENCRYPTION.md, without the log tag) the first time a caller passes a non-null delegate.
        /// </summary>
        public static ICryptoProvider Select(Action<string> warn)
        {
            ICryptoProvider forced = Override;
            if (forced != null)
                return forced;
            return Default.Select(warn);
        }

        static ICryptoProvider CreateCng()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("not running on Windows");
            return new CngProvider();
        }
    }

    /// <summary>
    /// The selection rule behind <see cref="CryptoProviders"/>, as an object: probe the native provider once (an exception or a failed known-answer
    /// self-test means "unavailable"), remember the reason, return the managed provider and raise the warning text once when it is unavailable.
    /// Instantiable so the tests can run the fallback paths without touching the process-wide provider.
    /// </summary>
    internal sealed class ProviderSelector
    {
        readonly object _gate = new object();
        readonly ICryptoProvider _managed;
        readonly Func<ICryptoProvider> _probe;
        bool _probed;
        ICryptoProvider _native;
        string _failure;
        bool _warned;

        public ProviderSelector(ICryptoProvider managed, Func<ICryptoProvider> probe)
        {
            _managed = managed;
            _probe = probe;
        }

        public ICryptoProvider Cng
        {
            get { Probe(); return _native; }
        }

        public string CngUnavailableReason
        {
            get { Probe(); return _failure; }
        }

        public ICryptoProvider Select(Action<string> warn)
        {
            Probe();
            if (_native != null)
                return _native;
            bool raise = false;
            lock (_gate)
            {
                if (!_warned && warn != null)
                {
                    _warned = true;
                    raise = true;
                }
            }
            if (raise)
                warn("Windows CNG unavailable (" + _failure + "); using managed crypto, which is slow: map loading will take noticeably longer");
            return _managed;
        }

        void Probe()
        {
            lock (_gate)
            {
                if (_probed)
                    return;
                _probed = true;
                try
                {
                    ICryptoProvider candidate = _probe();
                    ProviderSelfTest.Run(candidate);
                    _native = candidate;
                }
                catch (Exception ex)
                {
                    _native = null;
                    _failure = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }
    }

    /// <summary>
    /// Known-answer checks run on a provider before it is trusted (SHA-256 "abc", HMAC RFC 4231 case 1, AES-256 FIPS-197 C.3, in
    /// pieces and at odd offsets). A marshalling mistake in the CNG path usually produces WRONG OUTPUT WITHOUT AN EXCEPTION, so
    /// the CNG provider runs this on creation and the tests run it on both providers.
    /// </summary>
    internal static class ProviderSelfTest
    {
        public static void Run(ICryptoProvider p)
        {
            byte[] abc = Encoding.ASCII.GetBytes("abc");
            Expect(p.Name + " SHA-256", p.Sha256(abc, 0, 3), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
            byte[] padded = new byte[9];
            Buffer.BlockCopy(abc, 0, padded, 4, 3);
            Expect(p.Name + " SHA-256 at an offset", p.Sha256(padded, 4, 3), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
            Expect(p.Name + " SHA-256 empty", p.Sha256(new byte[0], 0, 0), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

            using (ISha256Stream s = p.NewSha256())
            {
                s.Update(abc, 0, 1);
                s.Update(abc, 1, 0);
                s.Update(abc, 1, 2);
                Expect(p.Name + " SHA-256 stream", s.Finish(), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
            }

            byte[] key = new byte[20];
            for (int i = 0; i < key.Length; i++)
                key[i] = 0x0b;
            byte[] hiThere = Encoding.ASCII.GetBytes("Hi There");
            const string rfc4231Case1 = "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7";
            Expect(p.Name + " HMAC-SHA-256", p.HmacSha256(key, hiThere, 0, hiThere.Length), rfc4231Case1);
            using (IHashStream h = p.NewHmacSha256(key))
            {
                h.Update(hiThere, 0, 3);
                h.Update(hiThere, 3, 5);
                Expect(p.Name + " HMAC-SHA-256 stream", h.Finish(), rfc4231Case1);
            }

            byte[] aesKey = new byte[32];
            for (int i = 0; i < 32; i++)
                aesKey[i] = (byte)i;
            byte[] block = new byte[16];
            for (int i = 0; i < 16; i++)
                block[i] = (byte)(i * 0x11);
            byte[] twoBlocks = new byte[3 + 32];
            Buffer.BlockCopy(block, 0, twoBlocks, 3, 16);
            Buffer.BlockCopy(block, 0, twoBlocks, 19, 16);
            byte[] output = new byte[5 + 32];
            using (IAesKey k = p.NewAes(aesKey))
                k.EncryptEcb(twoBlocks, 3, output, 5, 32);
            byte[] produced = new byte[32];
            Buffer.BlockCopy(output, 5, produced, 0, 32);
            Expect(p.Name + " AES-256", produced, "8ea2b7ca516745bfeafc49904b4960898ea2b7ca516745bfeafc49904b496089");
        }

        static void Expect(string what, byte[] actual, string expectedHex)
        {
            string hex = HexCodec.ToHex(actual);
            if (hex != expectedHex)
                throw new InvalidOperationException("known-answer test failed for " + what + ": got " + hex);
        }
    }

    internal static class ArgCheck
    {
        public static void Range(byte[] data, int offset, int count, string name)
        {
            if (count < 0 || offset < 0)
                throw new ArgumentOutOfRangeException(name);
            if (data == null)
            {
                if (count != 0 || offset != 0)
                    throw new ArgumentNullException(name);
                return;
            }
            if (data.Length - offset < count)
                throw new ArgumentException("offset and count exceed the length of " + name);
        }
    }
}
