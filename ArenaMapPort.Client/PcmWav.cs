#nullable disable
using System;
using System.IO;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// A reader for the PCM16 RIFF WAV files the gate sound package ships (tools/extract/extract_gate_sounds.py clips writes them
    /// with Python's <c>wave</c> module: a 44-byte header, then the samples). No Unity type, so the tests link it. It exists next to
    /// NativeSpatialAudio's own room-tone reader instead of replacing it: that one belongs to another feature and reads
    /// the package under its own hash rules. Fails closed (<see cref="InvalidDataException"/>) on anything but 16-bit PCM in one
    /// <c>fmt </c> and one <c>data</c> chunk of a consistent size.
    /// </summary>
    internal static class PcmWav
    {
        internal sealed class Wav
        {
            internal int Channels, SampleRate, Frames;
            /// <summary>Interleaved samples, <see cref="Frames"/> * <see cref="Channels"/> of them.</summary>
            internal short[] Samples;
        }

        /// <summary>Parses a WAV file image. The chunk walk follows the RIFF rules (a chunk starts on an even offset); a data chunk longer
        /// than the file, a format other than PCM16 and a sample count that is no whole number of frames are errors.</summary>
        internal static Wav Read(byte[] data)
        {
            if (data == null || data.Length < 12) throw new InvalidDataException("gate sound WAV is too short for a RIFF header");
            if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F' || data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E')
                throw new InvalidDataException("gate sound WAV has no RIFF/WAVE header");
            int channels = 0, rate = 0, dataAt = -1, dataSize = 0;
            long position = 12;
            while (position + 8 <= data.Length)
            {
                int at = (int)position;
                long size = BitConverter.ToUInt32(data, at + 4);
                if (size > data.Length - (position + 8)) throw new InvalidDataException("gate sound WAV chunk runs past the end of the file");
                if (Tag(data, at, 'f', 'm', 't', ' '))
                {
                    if (size < 16) throw new InvalidDataException("gate sound WAV fmt chunk is too short");
                    int format = BitConverter.ToUInt16(data, at + 8);
                    channels = BitConverter.ToUInt16(data, at + 10);
                    rate = BitConverter.ToInt32(data, at + 12);
                    int bits = BitConverter.ToUInt16(data, at + 22);
                    if (format != 1 || bits != 16) throw new InvalidDataException("gate sound WAV is not 16-bit PCM");
                }
                else if (Tag(data, at, 'd', 'a', 't', 'a'))
                {
                    dataAt = at + 8;
                    dataSize = (int)size;
                }
                position += 8 + size + (size & 1);
            }
            if (channels < 1 || channels > 2 || rate < 1 || dataAt < 0) throw new InvalidDataException("gate sound WAV lacks a usable fmt or data chunk");
            int frameBytes = 2 * channels;
            if (dataSize % frameBytes != 0) throw new InvalidDataException("gate sound WAV data is no whole number of frames");
            var samples = new short[dataSize / 2];
            Buffer.BlockCopy(data, dataAt, samples, 0, dataSize);
            if (!BitConverter.IsLittleEndian)
                for (int i = 0; i < samples.Length; i++) samples[i] = (short)(((ushort)samples[i] >> 8) | (samples[i] << 8));
            return new Wav { Channels = channels, SampleRate = rate, Frames = dataSize / frameBytes, Samples = samples };
        }

        static bool Tag(byte[] data, int at, char a, char b, char c, char d) =>
            data[at] == a && data[at + 1] == b && data[at + 2] == c && data[at + 3] == d;

        /// <summary>PCM16 to the -1..1 floats an AudioClip takes (value / 32768, the extractor's own conversion).</summary>
        internal static float[] ToFloats(short[] samples)
        {
            var result = new float[samples.Length];
            for (int i = 0; i < samples.Length; i++) result[i] = samples[i] / 32768f;
            return result;
        }
    }
}
