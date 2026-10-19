using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ArenaMapPort.BundleCrypt
{
    /// <summary>Raised by <see cref="CompareStream"/> at the first byte that differs from the existing file (used to stop an encryption pass early).</summary>
    internal sealed class CompareMismatchException : IOException
    {
        public long Position { get; private set; }

        public CompareMismatchException(long position, string message) : base(message)
        {
            Position = position;
        }
    }

    /// <summary>
    /// A write-only, seekable stream that does not write: every Write is compared with the bytes an existing file holds at the same position, and the
    /// first difference throws <see cref="CompareMismatchException"/>. Handed to Ampb.Encrypt as its destination it answers "would this encryption
    /// reproduce that file byte for byte?" without a temporary copy of a 1 GB bundle, and without rewriting (and re-syncing) a file that is already right.
    /// The container writer touches the payload first, then the header region, then sets the length; all three are checked (a different length is a mismatch).
    /// </summary>
    internal sealed class CompareStream : Stream
    {
        private readonly Stream _existing;
        private byte[] _scratch = new byte[1 << 20];
        private long _pos;
        private long _length;
        private long _comparedBytes;

        public CompareStream(Stream existing)
        {
            _existing = existing;
        }

        /// <summary>How many bytes were compared and found equal so far.</summary>
        public long ComparedBytes
        {
            get { return _comparedBytes; }
        }

        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { return _length; } }

        public override long Position
        {
            get { return _pos; }
            set { _pos = value; }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin: _pos = offset; break;
                case SeekOrigin.Current: _pos += offset; break;
                default: _pos = _length + offset; break;
            }
            return _pos;
        }

        public override void SetLength(long value)
        {
            if (value != _existing.Length)
                throw new CompareMismatchException(Math.Min(value, _existing.Length), "the existing file is " + _existing.Length + " bytes, the encryption is " + value);
            _length = value;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count == 0)
                return;
            if (_pos + count > _existing.Length)
                throw new CompareMismatchException(_existing.Length, "the existing file ends at " + _existing.Length + " bytes, the encryption continues");
            if (_scratch.Length < count)
                _scratch = new byte[count];
            _existing.Position = _pos;
            int total = 0;
            while (total < count)
            {
                int n = _existing.Read(_scratch, total, count - total);
                if (n <= 0)
                    throw new CompareMismatchException(_pos + total, "the existing file ended early at " + (_pos + total));
                total += n;
            }
            ReadOnlySpan<byte> expected = new ReadOnlySpan<byte>(buffer, offset, count);
            int diff = expected.CommonPrefixLength(new ReadOnlySpan<byte>(_scratch, 0, count));
            if (diff < count)
                throw new CompareMismatchException(_pos + diff, "the existing file differs at offset " + (_pos + diff));
            _pos += count;
            _comparedBytes += count;
            if (_pos > _length)
                _length = _pos;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
        }
    }

    internal static class Util
    {
        /// <summary>Writes the bytes to the path through a temporary file, unless the path already holds exactly these bytes (then nothing is touched and its time stays). True when it wrote.</summary>
        public static bool WriteIfChanged(string path, byte[] bytes)
        {
            if (File.Exists(path))
            {
                FileInfo info = new FileInfo(path);
                if (info.Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                    return false;
            }
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, true);
            return true;
        }

        public static FileStream OpenRead(string path)
        {
            if (!File.Exists(path))
                throw new ToolFailure("file not found: " + path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        }

        public static string N(long value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        public static string FirstBytesText(string path, int count)
        {
            using (FileStream fs = OpenRead(path))
            {
                byte[] b = new byte[count];
                int n = fs.Read(b, 0, count);
                char[] chars = new char[n];
                for (int i = 0; i < n; i++)
                    chars[i] = b[i] >= 0x20 && b[i] < 0x7f ? (char)b[i] : '.';
                return new string(chars);
            }
        }

        public static bool StartsWithAmpb(string path)
        {
            using (FileStream fs = OpenRead(path))
            {
                byte[] b = new byte[4];
                return fs.Read(b, 0, 4) == 4 && b[0] == 0x41 && b[1] == 0x4D && b[2] == 0x50 && b[3] == 0x42;
            }
        }

        public static double Ms(long startedTimestamp)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }
}
