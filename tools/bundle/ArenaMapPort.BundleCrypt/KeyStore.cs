using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArenaMapPort.Crypto;

namespace ArenaMapPort.BundleCrypt
{
    /// <summary>One wrap of the key store: the shipped fields plus the SHA-256 of every level file (never shipped, so the first Arena update shows which files changed).</summary>
    internal sealed class StoreWrap
    {
        public KeyWrap Wrap;
        public string[] FileHashes;
    }

    /// <summary>The release key of one map as the build machine keeps it.</summary>
    internal sealed class KeyStoreEntry
    {
        public string Path;
        public string MapKey;
        public byte[] KMap;
        public string CreatedUtc;
        public bool JustCreated;
        public List<StoreWrap> Wraps = new List<StoreWrap>();
    }

    /// <summary>
    /// The key store of the build machine, <c>work/release-keys/&lt;map_key&gt;.key</c> (ignored by git, never shipped, back it up: docs/PLAN-ENCRYPTION.md 0.6).
    /// One JSON file per map: <c>{schema, map_key, k_map (64 hex), created_utc, wraps: [{built_for, files: [{path, size, sha256}], salt, wrapped_key, check}]}</c>.
    /// <c>k_map</c> is the random 32-byte content key, generated once and then only read, so a rebuild reproduces every bundle byte for byte; <c>wraps</c>
    /// is the append-only list the shipped key file <c>keys/&lt;map_key&gt;.json</c> is written from, so wraps for Arena builds that are no longer installed
    /// survive. A key is created only when the caller says so (<c>--create-key</c>): an accidentally lost store must never quietly turn into a new key,
    /// because the bundles already released would stop unlocking. Rotation is a separate command that needs <c>--yes</c>. Proved by test_bundle_crypt_tool.py.
    /// </summary>
    internal static class KeyStore
    {
        private const int Schema = 1;

        public static string PathFor(string directory, string mapKey)
        {
            if (!Ampb.IsValidMapKey(mapKey))
                throw new UsageException("the map key must be 1.." + Ampb.MaxMapKeyLength + " characters of [a-z0-9_]");
            return System.IO.Path.Combine(directory, mapKey + ".key");
        }

        /// <summary>Loads the entry of a map. A missing file is an error unless <paramref name="create"/>, which generates and saves a new content key first.</summary>
        public static KeyStoreEntry Open(string directory, string mapKey, bool create)
        {
            string path = PathFor(directory, mapKey);
            if (File.Exists(path))
                return Load(path, mapKey);
            if (!create)
                throw new ToolFailure("there is no content key for '" + mapKey + "' in " + directory
                    + " (the first build of a map passes --create-key; a key store that has gone missing must be restored from its backup, not recreated)");
            KeyStoreEntry entry = new KeyStoreEntry();
            entry.Path = path;
            entry.MapKey = mapKey;
            entry.KMap = RandomNumberGenerator.GetBytes(32);
            entry.CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            entry.JustCreated = true;
            Directory.CreateDirectory(directory);
            Save(entry);
            return entry;
        }

        private static KeyStoreEntry Load(string path, string mapKey)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllBytes(path));
            }
            catch (JsonException ex)
            {
                throw new ToolFailure("the key store file " + path + " is not valid JSON (" + ex.Message + ")");
            }
            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new ToolFailure("the key store file " + path + " is not a JSON object");
                if (Int(root, "schema", path) != Schema)
                    throw new ToolFailure("the key store file " + path + " has an unsupported schema");
                KeyStoreEntry e = new KeyStoreEntry();
                e.Path = path;
                e.MapKey = Str(root, "map_key", path);
                if (e.MapKey != mapKey)
                    throw new ToolFailure("the key store file " + path + " belongs to '" + e.MapKey + "', not to '" + mapKey + "'");
                e.KMap = Hex(Str(root, "k_map", path), 32, "k_map", path);
                e.CreatedUtc = Str(root, "created_utc", path);
                JsonElement wraps;
                if (!root.TryGetProperty("wraps", out wraps) || wraps.ValueKind != JsonValueKind.Array)
                    throw new ToolFailure("the key store file " + path + " has no 'wraps' array");
                foreach (JsonElement w in wraps.EnumerateArray())
                {
                    StoreWrap sw = new StoreWrap();
                    List<KeyFileEntry> files = new List<KeyFileEntry>();
                    List<string> hashes = new List<string>();
                    JsonElement fileArray;
                    if (!w.TryGetProperty("files", out fileArray) || fileArray.ValueKind != JsonValueKind.Array)
                        throw new ToolFailure("a wrap in " + path + " has no 'files' array");
                    foreach (JsonElement f in fileArray.EnumerateArray())
                    {
                        files.Add(new KeyFileEntry(Str(f, "path", path), Int(f, "size", path)));
                        hashes.Add(Str(f, "sha256", path));
                    }
                    sw.FileHashes = hashes.ToArray();
                    sw.Wrap = new KeyWrap(Str(w, "built_for", path), files,
                        Hex(Str(w, "salt", path), 32, "salt", path),
                        Hex(Str(w, "wrapped_key", path), 32, "wrapped_key", path),
                        Hex(Str(w, "check", path), 32, "check", path));
                    e.Wraps.Add(sw);
                }
                return e;
            }
        }

        public static void Save(KeyStoreEntry e)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteNumber("schema", Schema);
                    w.WriteString("map_key", e.MapKey);
                    w.WriteString("k_map", ToHex(e.KMap));
                    w.WriteString("created_utc", e.CreatedUtc);
                    w.WriteStartArray("wraps");
                    foreach (StoreWrap sw in e.Wraps)
                    {
                        w.WriteStartObject();
                        w.WriteString("built_for", sw.Wrap.BuiltFor);
                        w.WriteStartArray("files");
                        for (int i = 0; i < sw.Wrap.Files.Count; i++)
                        {
                            w.WriteStartObject();
                            w.WriteString("path", sw.Wrap.Files[i].Path);
                            w.WriteNumber("size", sw.Wrap.Files[i].Size);
                            w.WriteString("sha256", sw.FileHashes[i]);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteString("salt", ToHex(sw.Wrap.Salt));
                        w.WriteString("wrapped_key", ToHex(sw.Wrap.WrappedKey));
                        w.WriteString("check", ToHex(sw.Wrap.Check));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                string tmp = e.Path + ".tmp";
                File.WriteAllBytes(tmp, ms.ToArray());
                File.Move(tmp, e.Path, true);
            }
        }

        /// <summary>
        /// Appends the wrap unless an equal one is already there. The wrap is a pure function of K_map, the built-for text, the files and their hashes,
        /// so an equal salt, wrapped key and check means "same inputs": nothing to add. Different inputs (new files, new sizes, or the same sizes with
        /// other content, as a hotfix with an unchanged version would) append. Returns true when the entry changed.
        /// </summary>
        public static bool AddWrap(KeyStoreEntry e, KeyWrap wrap, string[] fileHashes)
        {
            foreach (StoreWrap existing in e.Wraps)
                if (ConstantTime.AreEqual(existing.Wrap.Salt, wrap.Salt) && ConstantTime.AreEqual(existing.Wrap.WrappedKey, wrap.WrappedKey)
                    && ConstantTime.AreEqual(existing.Wrap.Check, wrap.Check))
                    return false;
            StoreWrap sw = new StoreWrap();
            sw.Wrap = wrap;
            sw.FileHashes = fileHashes;
            e.Wraps.Add(sw);
            return true;
        }

        /// <summary>The shipped key file: every wrap in insertion order (the player's resolver tries them in this order and reports against the last).</summary>
        public static KeyFile ToKeyFile(KeyStoreEntry e)
        {
            KeyFile f = new KeyFile(e.MapKey);
            foreach (StoreWrap sw in e.Wraps)
                f.Wraps.Add(sw.Wrap);
            f.Validate();
            return f;
        }

        /// <summary>Moves the store file aside under a time-stamped name and creates a fresh content key with no wraps.</summary>
        public static KeyStoreEntry Rotate(string directory, string mapKey, out string asidePath)
        {
            string path = PathFor(directory, mapKey);
            asidePath = null;
            if (File.Exists(path))
            {
                asidePath = path + ".rotated-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
                File.Move(path, asidePath);
            }
            return Open(directory, mapKey, true);
        }

        public static string ToHex(byte[] data)
        {
            return HexCodec.ToHex(data);
        }

        private static byte[] Hex(string text, int bytes, string field, string path)
        {
            byte[] r;
            if (!HexCodec.TryParse(text, bytes, out r))
                throw new ToolFailure("'" + field + "' in " + path + " must be " + (bytes * 2) + " hex digits");
            return r;
        }

        private static string Str(JsonElement obj, string name, string path)
        {
            JsonElement v;
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out v) || v.ValueKind != JsonValueKind.String)
                throw new ToolFailure("'" + name + "' is missing or not a string in " + path);
            return v.GetString();
        }

        private static long Int(JsonElement obj, string name, string path)
        {
            JsonElement v;
            long n;
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out n))
                throw new ToolFailure("'" + name + "' is missing or not an integer in " + path);
            return n;
        }
    }

    /// <summary>
    /// Which Arena level files a map's key depends on. The source of truth is the table <c>tools/bundle/key-sources.json</c>
    /// (<c>{schema, arena_build, maps: {map_key: [{index, scene}]}}</c>), because two of the four accepted extraction manifests (Equator, Bay5)
    /// carry only scene names; the manifests that do carry <c>source.scene_registry</c> (AirPit, Bowl) can be given as well and must agree with the table.
    /// The files are <c>EscapeFromTarkovArena_Data/level&lt;index&gt;</c> in ascending numeric index order (the order the IKM is hashed in).
    /// A test pins the table against the manifests and the extractor tables (test_bundle_crypt_tool.py).
    /// </summary>
    internal static class LevelSources
    {
        public const string DataFolder = "EscapeFromTarkovArena_Data";

        public static string RelativePath(int index)
        {
            return DataFolder + "/level" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The ascending level indices of a map, from the table and/or a manifest (both given: they must name the same files).</summary>
        public static List<int> Indices(string mapKey, string levelsFile, string manifestFile)
        {
            if (levelsFile == null && manifestFile == null)
                throw new UsageException("--levels FILE (tools/bundle/key-sources.json) or --manifest FILE (an extract manifest.json) is required");
            List<int> fromTable = levelsFile == null ? null : FromTable(mapKey, levelsFile);
            List<int> fromManifest = manifestFile == null ? null : FromManifest(manifestFile);
            if (fromTable != null && fromManifest != null)
            {
                if (fromTable.Count != fromManifest.Count)
                    throw new ToolFailure("the key sources table and the manifest list different level files for '" + mapKey + "' ("
                        + string.Join(",", fromTable) + " against " + string.Join(",", fromManifest) + ")");
                for (int i = 0; i < fromTable.Count; i++)
                    if (fromTable[i] != fromManifest[i])
                        throw new ToolFailure("the key sources table and the manifest list different level files for '" + mapKey + "' ("
                            + string.Join(",", fromTable) + " against " + string.Join(",", fromManifest) + ")");
            }
            return fromTable ?? fromManifest;
        }

        private static List<int> FromTable(string mapKey, string file)
        {
            JsonDocument doc = ParseFile(file);
            using (doc)
            {
                JsonElement maps;
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("maps", out maps) || maps.ValueKind != JsonValueKind.Object)
                    throw new ToolFailure("the key sources table " + file + " has no 'maps' object");
                JsonElement list;
                if (!maps.TryGetProperty(mapKey, out list) || list.ValueKind != JsonValueKind.Array)
                    throw new ToolFailure("the key sources table " + file + " has no entry for '" + mapKey + "'");
                return Collect(list, "index", file);
            }
        }

        private static List<int> FromManifest(string file)
        {
            JsonDocument doc = ParseFile(file);
            using (doc)
            {
                JsonElement source, registry;
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("source", out source)
                    || source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("scene_registry", out registry) || registry.ValueKind != JsonValueKind.Array)
                    throw new ToolFailure("the manifest " + file + " has no source.scene_registry (Equator's and Bay5's packages do not; use --levels tools/bundle/key-sources.json)");
                return Collect(registry, "index", file);
            }
        }

        private static List<int> Collect(JsonElement array, string field, string file)
        {
            SortedSet<int> set = new SortedSet<int>();
            foreach (JsonElement e in array.EnumerateArray())
            {
                JsonElement v;
                int n;
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(field, out v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out n) || n < 0)
                    throw new ToolFailure("an entry of " + file + " has no valid '" + field + "'");
                if (!set.Add(n))
                    throw new ToolFailure("level " + n + " is listed twice in " + file);
            }
            if (set.Count == 0)
                throw new ToolFailure(file + " lists no level files for this map");
            return new List<int>(set);
        }

        private static JsonDocument ParseFile(string file)
        {
            if (!File.Exists(file))
                throw new ToolFailure("file not found: " + file);
            try
            {
                return JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { MaxDepth = 64 });
            }
            catch (JsonException ex)
            {
                throw new ToolFailure(file + " is not valid JSON (" + ex.Message + ")");
            }
        }
    }
}
