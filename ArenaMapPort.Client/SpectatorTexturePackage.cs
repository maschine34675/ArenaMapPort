#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace ArenaMapPort.Client
{
    // The map pins its shared manifest, which pins the exact PNG bytes. Mixed core/map updates fail closed.
    internal sealed class SpectatorTexturePackage
    {
        readonly Dictionary<string, Entry> _files = new Dictionary<string, Entry>(StringComparer.Ordinal);
        public SpectatorTexturePackage(byte[] manifest, string expectedHash)
        {
            if (Hash(manifest) != expectedHash) throw new InvalidDataException("spectator map/shared manifest mismatch");
            var data = JsonConvert.DeserializeObject<Manifest>(Encoding.UTF8.GetString(manifest));
            if (data == null || data.schema != 1 || data.files == null || data.files.Length < 1 || data.files.Length > 4096)
                throw new InvalidDataException("unsupported shared spectator textures");
            foreach (var row in data.files)
            {
                if (row == null || row.file == null || !Regex.IsMatch(row.file, @"\A[0-9a-f]{16}(?:-[0-5])?\.png\z") ||
                    row.bytes <= 0 || row.bytes > 128 * 1024 * 1024 || row.sha256 == null ||
                    !Regex.IsMatch(row.sha256, @"\A[0-9a-f]{64}\z") || _files.ContainsKey(row.file))
                    throw new InvalidDataException("invalid shared spectator texture entry");
                _files.Add(row.file, row);
            }
        }

        public byte[] Read(string folder, string file)
        {
            if (file == null || !_files.TryGetValue(file, out var row))
                throw new InvalidDataException("unlisted spectator texture");
            var path = Path.Combine(folder, file);
            if (new FileInfo(path).Length != row.bytes) throw new InvalidDataException("damaged spectator texture: " + file);
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != row.bytes || Hash(bytes) != row.sha256)
                throw new InvalidDataException("damaged spectator texture: " + file);
            return bytes;
        }

        static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        #pragma warning disable CS0649
        sealed class Manifest { public int schema; public Entry[] files; }
        sealed class Entry { public string file, sha256; public int bytes; }
        #pragma warning restore CS0649
    }
}
