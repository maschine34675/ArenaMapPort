#nullable disable
using System;
using System.Security.Cryptography;

namespace ArenaMapPort.Crypto
{
    /// <summary>
    /// The crypto provider built on System.Security.Cryptography (Aes in ECB mode, SHA256, HMACSHA256): the fallback when Windows
    /// CNG is unavailable and the reference every CNG result is compared against. On EFT's Mono these classes are managed code
    /// without hardware support (tens of MB/s), which is why the client prefers <see cref="CngProvider"/> and warns when it has to use
    /// this one; on .NET 10 (the tests and the build tool) the same classes are backed by the OS and are fast, so the numbers
    /// measured there say nothing about Mono. Streaming hashes go through TransformBlock/TransformFinalBlock (IncrementalHash is
    /// not relied on). Proved by BundleCryptoTests (known answers, equality with CNG, the golden container).
    /// </summary>
    internal sealed class ManagedProvider : ICryptoProvider
    {
        static readonly byte[] Empty = new byte[0];

        public string Name { get { return "managed"; } }

        public byte[] Sha256(byte[] data, int offset, int count)
        {
            ArgCheck.Range(data, offset, count, "data");
            using (SHA256 h = SHA256.Create())
                return h.ComputeHash(data ?? Empty, offset, count);
        }

        public byte[] HmacSha256(byte[] key, byte[] data, int offset, int count)
        {
            if (key == null)
                throw new ArgumentNullException("key");
            ArgCheck.Range(data, offset, count, "data");
            using (HMACSHA256 h = new HMACSHA256(key))
                return h.ComputeHash(data ?? Empty, offset, count);
        }

        public IAesKey NewAes(byte[] key32)
        {
            if (key32 == null || key32.Length != 32)
                throw new ArgumentException("an AES-256 key is 32 bytes", "key32");
            return new ManagedAesKey(key32);
        }

        public ISha256Stream NewSha256()
        {
            return new ManagedHashStream(SHA256.Create());
        }

        public IHashStream NewHmacSha256(byte[] key)
        {
            if (key == null)
                throw new ArgumentNullException("key");
            return new ManagedHashStream(new HMACSHA256(key));
        }

        sealed class ManagedAesKey : IAesKey
        {
            Aes _aes;
            ICryptoTransform _enc;

            public ManagedAesKey(byte[] key)
            {
                _aes = Aes.Create();
                _aes.Mode = CipherMode.ECB;
                _aes.Padding = PaddingMode.None;
                _aes.Key = (byte[])key.Clone();
                _enc = _aes.CreateEncryptor();
            }

            public void EncryptEcb(byte[] src, int srcOffset, byte[] dst, int dstOffset, int count)
            {
                if (_enc == null)
                    throw new ObjectDisposedException("IAesKey");
                ArgCheck.Range(src, srcOffset, count, "src");
                ArgCheck.Range(dst, dstOffset, count, "dst");
                if ((count & 15) != 0)
                    throw new ArgumentException("count must be a multiple of 16", "count");
                if (count == 0)
                    return;
                int done = _enc.TransformBlock(src, srcOffset, count, dst, dstOffset);
                if (done != count)
                    throw new CryptographicException("AES transformed " + done + " of " + count + " bytes");
            }

            public void Dispose()
            {
                if (_enc != null)
                {
                    _enc.Dispose();
                    _enc = null;
                }
                if (_aes != null)
                {
                    _aes.Dispose();
                    _aes = null;
                }
            }
        }

        sealed class ManagedHashStream : ISha256Stream
        {
            HashAlgorithm _h;

            public ManagedHashStream(HashAlgorithm h)
            {
                _h = h;
            }

            public void Update(byte[] data, int offset, int count)
            {
                if (_h == null)
                    throw new ObjectDisposedException("IHashStream");
                ArgCheck.Range(data, offset, count, "data");
                if (count > 0)
                    _h.TransformBlock(data, offset, count, null, 0);
            }

            public byte[] Finish()
            {
                if (_h == null)
                    throw new ObjectDisposedException("IHashStream");
                _h.TransformFinalBlock(Empty, 0, 0);
                byte[] r = (byte[])_h.Hash.Clone();
                Dispose();
                return r;
            }

            public void Dispose()
            {
                if (_h != null)
                {
                    _h.Dispose();
                    _h = null;
                }
            }
        }
    }
}
