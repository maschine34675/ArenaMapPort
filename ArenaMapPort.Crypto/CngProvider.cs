#nullable disable
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ArenaMapPort.Crypto
{
    /// <summary>
    /// The crypto provider built on Windows CNG (bcrypt.dll) through P/Invoke. Why it exists: EFT's Mono has no hardware crypto in
    /// managed code (managed AES runs at tens of MB/s, SHA256Managed around 100 MB/s), so decrypting a 1 GB scene bundle with
    /// System.Security.Cryptography would add tens of seconds to a raid load; bcrypt.dll uses AES-NI and the SHA extensions.
    /// AES goes through BCryptEncrypt in ECB mode over counter blocks (CTR is built in <see cref="AesCtr"/>), hashes through
    /// BCryptHash (one shot) and BCryptCreateHash/HashData/FinishHash (streams), HMAC through the same calls on an algorithm handle
    /// opened with BCRYPT_ALG_HANDLE_HMAC_FLAG. Every buffer is an IntPtr into a pinned array (GCHandle), so no unsafe code and no
    /// marshalling copies; NTSTATUS values below zero are errors. A wrong marshalling signature typically yields wrong output without
    /// any exception, so ProviderSelector runs <see cref="ProviderSelfTest"/> on a new instance and trusts it only when the known answers
    /// match (otherwise it falls back to the managed provider). The algorithm handles live for the process (a few hundred bytes).
    /// BCryptHash needs Windows 10 / Server 2016; an older system throws EntryPointNotFoundException here and takes the managed path.
    /// Proved by BundleCryptoTests (known answers, equality with the managed provider, throughput) and the GPU-2 Debug line "crypto: CNG".
    /// </summary>
    internal sealed class CngProvider : ICryptoProvider
    {
        readonly IntPtr _aes;
        readonly IntPtr _sha;
        readonly IntPtr _hmac;

        public string Name { get { return "CNG"; } }

        internal CngProvider()
        {
            IntPtr aes = IntPtr.Zero, sha = IntPtr.Zero, hmac = IntPtr.Zero;
            try
            {
                sha = Open("SHA256", 0);
                hmac = Open("SHA256", Native.AlgHandleHmacFlag);
                aes = Open("AES", 0);
                byte[] mode = Encoding.Unicode.GetBytes("ChainingModeECB\0");
                Native.Check(Native.BCryptSetProperty(aes, "ChainingMode", mode, mode.Length, 0), "BCryptSetProperty(ChainingMode)");
                _aes = aes;
                _sha = sha;
                _hmac = hmac;
            }
            catch (Exception)
            {
                if (aes != IntPtr.Zero) Native.BCryptCloseAlgorithmProvider(aes, 0);
                if (sha != IntPtr.Zero) Native.BCryptCloseAlgorithmProvider(sha, 0);
                if (hmac != IntPtr.Zero) Native.BCryptCloseAlgorithmProvider(hmac, 0);
                throw;
            }
        }

        static IntPtr Open(string algorithm, uint flags)
        {
            IntPtr h;
            Native.Check(Native.BCryptOpenAlgorithmProvider(out h, algorithm, null, flags), "BCryptOpenAlgorithmProvider(" + algorithm + ")");
            return h;
        }

        public byte[] Sha256(byte[] data, int offset, int count)
        {
            ArgCheck.Range(data, offset, count, "data");
            byte[] result = new byte[32];
            using (Pinned pin = new Pinned(data))
                Native.Check(Native.BCryptHash(_sha, null, 0, pin.At(offset), count, result, 32), "BCryptHash(SHA256)");
            return result;
        }

        public byte[] HmacSha256(byte[] key, byte[] data, int offset, int count)
        {
            if (key == null)
                throw new ArgumentNullException("key");
            ArgCheck.Range(data, offset, count, "data");
            byte[] secret = key.Length == 0 ? new byte[1] : key;   // HMAC zero-extends the key: the empty key equals a single zero byte
            byte[] result = new byte[32];
            using (Pinned pin = new Pinned(data))
                Native.Check(Native.BCryptHash(_hmac, secret, secret.Length, pin.At(offset), count, result, 32), "BCryptHash(HMAC-SHA256)");
            return result;
        }

        public IAesKey NewAes(byte[] key32)
        {
            if (key32 == null || key32.Length != 32)
                throw new ArgumentException("an AES-256 key is 32 bytes", "key32");
            IntPtr key;
            Native.Check(Native.BCryptGenerateSymmetricKey(_aes, out key, IntPtr.Zero, 0, key32, 32, 0), "BCryptGenerateSymmetricKey");
            return new CngAesKey(key);
        }

        public ISha256Stream NewSha256()
        {
            return new CngHashStream(_sha, null);
        }

        public IHashStream NewHmacSha256(byte[] key)
        {
            if (key == null)
                throw new ArgumentNullException("key");
            return new CngHashStream(_hmac, key.Length == 0 ? new byte[1] : key);
        }

        /// <summary>A pinned array and the address of any offset in it. An empty or null array pins a one-byte dummy (never read: count is 0).</summary>
        struct Pinned : IDisposable
        {
            static readonly byte[] Dummy = new byte[1];
            GCHandle _handle;
            readonly IntPtr _base;

            public Pinned(byte[] array)
            {
                if (array == null || array.Length == 0)
                    array = Dummy;
                _handle = GCHandle.Alloc(array, GCHandleType.Pinned);
                _base = _handle.AddrOfPinnedObject();
            }

            public IntPtr At(int offset)
            {
                return IntPtr.Add(_base, offset);
            }

            public void Dispose()
            {
                if (_handle.IsAllocated)
                    _handle.Free();
            }
        }

        sealed class CngAesKey : IAesKey
        {
            IntPtr _key;

            public CngAesKey(IntPtr key)
            {
                _key = key;
            }

            ~CngAesKey()
            {
                Release();
            }

            public void EncryptEcb(byte[] src, int srcOffset, byte[] dst, int dstOffset, int count)
            {
                if (_key == IntPtr.Zero)
                    throw new ObjectDisposedException("IAesKey");
                ArgCheck.Range(src, srcOffset, count, "src");
                ArgCheck.Range(dst, dstOffset, count, "dst");
                if ((count & 15) != 0)
                    throw new ArgumentException("count must be a multiple of 16", "count");
                if (count == 0)
                    return;
                int written;
                using (Pinned ps = new Pinned(src))
                using (Pinned pd = new Pinned(dst))
                    Native.Check(Native.BCryptEncrypt(_key, ps.At(srcOffset), count, IntPtr.Zero, IntPtr.Zero, 0, pd.At(dstOffset), count, out written, 0), "BCryptEncrypt");
                if (written != count)
                    throw new CryptographicException("BCryptEncrypt produced " + written + " of " + count + " bytes");
            }

            public void Dispose()
            {
                Release();
                GC.SuppressFinalize(this);
            }

            void Release()
            {
                IntPtr k = _key;
                _key = IntPtr.Zero;
                if (k != IntPtr.Zero)
                    Native.BCryptDestroyKey(k);
            }
        }

        sealed class CngHashStream : ISha256Stream
        {
            IntPtr _hash;

            public CngHashStream(IntPtr algorithm, byte[] secret)
            {
                Native.Check(Native.BCryptCreateHash(algorithm, out _hash, IntPtr.Zero, 0, secret, secret == null ? 0 : secret.Length, 0), "BCryptCreateHash");
            }

            ~CngHashStream()
            {
                Release();
            }

            public void Update(byte[] data, int offset, int count)
            {
                if (_hash == IntPtr.Zero)
                    throw new ObjectDisposedException("IHashStream");
                ArgCheck.Range(data, offset, count, "data");
                if (count == 0)
                    return;
                using (Pinned pin = new Pinned(data))
                    Native.Check(Native.BCryptHashData(_hash, pin.At(offset), count, 0), "BCryptHashData");
            }

            public byte[] Finish()
            {
                if (_hash == IntPtr.Zero)
                    throw new ObjectDisposedException("IHashStream");
                byte[] result = new byte[32];
                try
                {
                    Native.Check(Native.BCryptFinishHash(_hash, result, 32, 0), "BCryptFinishHash");
                }
                finally
                {
                    Dispose();
                }
                return result;
            }

            public void Dispose()
            {
                Release();
                GC.SuppressFinalize(this);
            }

            void Release()
            {
                IntPtr h = _hash;
                _hash = IntPtr.Zero;
                if (h != IntPtr.Zero)
                    Native.BCryptDestroyHash(h);
            }
        }

        static class Native
        {
            const string Dll = "bcrypt.dll";
            public const uint AlgHandleHmacFlag = 0x00000008;

            public static void Check(int status, string operation)
            {
                if (status < 0)
                    throw new CryptographicException(operation + " failed with NTSTATUS 0x" + status.ToString("x8"));
            }

            [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
            public static extern int BCryptOpenAlgorithmProvider(out IntPtr phAlgorithm, [MarshalAs(UnmanagedType.LPWStr)] string pszAlgId,
                [MarshalAs(UnmanagedType.LPWStr)] string pszImplementation, uint dwFlags);

            [DllImport(Dll, ExactSpelling = true)]
            public static extern int BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, uint dwFlags);

            [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
            public static extern int BCryptSetProperty(IntPtr hObject, [MarshalAs(UnmanagedType.LPWStr)] string pszProperty, byte[] pbInput, int cbInput, uint dwFlags);

            [DllImport(Dll, ExactSpelling = true)]
            public static extern int BCryptGenerateSymmetricKey(IntPtr hAlgorithm, out IntPtr phKey, IntPtr pbKeyObject, int cbKeyObject,
                byte[] pbSecret, int cbSecret, uint dwFlags);

            [DllImport(Dll, ExactSpelling = true)]
            public static extern int BCryptDestroyKey(IntPtr hKey);

            [DllImport(Dll, ExactSpelling = true)]
            public static extern int BCryptEncrypt(IntPtr hKey, IntPtr pbInput, int cbInput, IntPtr pPaddingInfo, IntPtr pbIV, int cbIV,
                IntPtr pbOutput, int cbOutput, out int pcbResult, uint dwFlags);

            [DllImport(Dll, ExactSpelling = true)]
            public static extern int BCryptHash(IntPtr hAlgorithm, byte[] pbSecret, int cbSecret, IntPtr pbInput, int cbInput, byte[] pbOutput, int cbOutput);

            [DllImport(Dll, ExactSpelling = true)]
            public static extern int BCryptCreateHash(IntPtr hAlgorithm, out IntPtr phHash, IntPtr pbHashObject, int cbHashObject,
                byte[] pbSecret, int cbSecret, uint dwFlags);

            [DllImport(Dll, ExactSpelling = true)]
            public static extern int BCryptHashData(IntPtr hHash, IntPtr pbInput, int cbInput, uint dwFlags);

            [DllImport(Dll, ExactSpelling = true)]
            public static extern int BCryptFinishHash(IntPtr hHash, byte[] pbOutput, int cbOutput, uint dwFlags);

            [DllImport(Dll, ExactSpelling = true)]
            public static extern int BCryptDestroyHash(IntPtr hHash);
        }
    }
}
