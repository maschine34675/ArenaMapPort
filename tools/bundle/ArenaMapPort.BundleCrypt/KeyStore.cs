using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArenaMapPort.Crypto;

namespace ArenaMapPort.BundleCrypt
{
    /// <summary>
    /// One wrap of the key store. A current wrap (<see cref="Wrap"/> set) carries the shipped fields plus the digest every file contributed to the key
    /// (<see cref="FileDigests"/>, never shipped, so the next Arena update shows which files changed). A retired wrap (<see cref="Retired"/> set) is a wrap of
    /// the level-file scheme of 2026-10-19 (schema 1 stores, docs/PLAN-ENCRYPTION.md section 6): kept verbatim because the store is append-only, never shipped.
    /// </summary>
    internal sealed class StoreWrap
    {
        public const string CurrentScheme = "sample-v2";
        public const string RetiredScheme = "level-files-v1";

        public KeyWrap Wrap;
        public string[] FileDigests;
        public JsonElement? Retired;
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

        public int CurrentWraps
        {
            get { return Wraps.FindAll(w => w.Wrap != null).Count; }
        }

        public int RetiredWraps
        {
            get { return Wraps.Count - CurrentWraps; }
        }
    }

    /// <summary>
    /// The key store of the build machine, <c>work/release-keys/&lt;map_key&gt;.key</c> (ignored by git, never shipped, back it up: docs/PLAN-ENCRYPTION.md 0.6).
    /// One JSON file per map: <c>{schema: 2, map_key, k_map (64 hex), created_utc, wraps: [...]}</c>, each wrap
    /// <c>{scheme: "sample-v2", built_for, sample: {block_size, blocks}, files: [{path, size, sample_sha256}], salt, wrapped_key, check}</c> or a retired
    /// <c>{scheme: "level-files-v1", ...}</c> wrap of the whole-level-file scheme, kept as it was written. A schema 1 store (2026-10-19, level-file wraps only) is
    /// read as retired wraps and written back as schema 2 the first time a wrap is added; its <c>k_map</c> and <c>created_utc</c> are kept.
    /// <c>k_map</c> is the random 32-byte content key, generated once and then only read, so a rebuild reproduces every bundle byte for byte; <c>wraps</c>
    /// is the append-only list the shipped key file <c>keys/&lt;map_key&gt;.json</c> is written from (current wraps only), so wraps for Arena builds that are
    /// no longer installed survive. A key is created only when the caller says so (<c>--create-key</c>): an accidentally lost store must never quietly turn
    /// into a new key, because the bundles already released would stop unlocking. Rotation is a separate command that needs <c>--yes</c>.
    /// Proved by test_bundle_crypt_tool.py.
    /// </summary>
    internal static class KeyStore
    {
        private const int Schema = 2;
        private const int RetiredStoreSchema = 1;

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
                long schema = Int(root, "schema", path);
                if (schema != Schema && schema != RetiredStoreSchema)
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
                    if (w.ValueKind != JsonValueKind.Object)
                        throw new ToolFailure("a wrap in " + path + " is not an object");
                    string scheme = schema == RetiredStoreSchema ? StoreWrap.RetiredScheme : Str(w, "scheme", path);
                    if (scheme == StoreWrap.RetiredScheme)
                    {
                        e.Wraps.Add(new StoreWrap { Retired = w.Clone() });
                        continue;
                    }
                    if (scheme != StoreWrap.CurrentScheme)
                        throw new ToolFailure("a wrap in " + path + " has the unknown scheme '" + scheme + "'");
                    e.Wraps.Add(LoadCurrent(w, path));
                }
                return e;
            }
        }

        private static StoreWrap LoadCurrent(JsonElement w, string path)
        {
            JsonElement sample;
            if (!w.TryGetProperty("sample", out sample) || sample.ValueKind != JsonValueKind.Object)
                throw new ToolFailure("a wrap in " + path + " has no 'sample' object");
            KeySample spec = new KeySample(checked((int)Int(sample, "block_size", path)), checked((int)Int(sample, "blocks", path)));
            if (spec.Problem() != null)
                throw new ToolFailure("a wrap in " + path + ": sample " + spec.Problem());
            List<KeyFileEntry> files = new List<KeyFileEntry>();
            List<string> digests = new List<string>();
            JsonElement fileArray;
            if (!w.TryGetProperty("files", out fileArray) || fileArray.ValueKind != JsonValueKind.Array)
                throw new ToolFailure("a wrap in " + path + " has no 'files' array");
            foreach (JsonElement f in fileArray.EnumerateArray())
            {
                files.Add(new KeyFileEntry(Str(f, "path", path), Int(f, "size", path)));
                digests.Add(Str(f, "sample_sha256", path));
            }
            StoreWrap sw = new StoreWrap();
            sw.FileDigests = digests.ToArray();
            sw.Wrap = new KeyWrap(Str(w, "built_for", path), spec, files,
                Hex(Str(w, "salt", path), 32, "salt", path),
                Hex(Str(w, "wrapped_key", path), 32, "wrapped_key", path),
                Hex(Str(w, "check", path), 32, "check", path));
            return sw;
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
                        if (sw.Retired.HasValue)
                        {
                            // kept as it was written; only the scheme name is added (or kept) in front
                            w.WriteString("scheme", StoreWrap.RetiredScheme);
                            foreach (JsonProperty property in sw.Retired.Value.EnumerateObject())
                                if (property.Name != "scheme")
                                    property.WriteTo(w);
                            w.WriteEndObject();
                            continue;
                        }
                        w.WriteString("scheme", StoreWrap.CurrentScheme);
                        w.WriteString("built_for", sw.Wrap.BuiltFor);
                        w.WriteStartObject("sample");
                        w.WriteNumber("block_size", sw.Wrap.Sample.BlockSize);
                        w.WriteNumber("blocks", sw.Wrap.Sample.Blocks);
                        w.WriteEndObject();
                        w.WriteStartArray("files");
                        for (int i = 0; i < sw.Wrap.Files.Count; i++)
                        {
                            w.WriteStartObject();
                            w.WriteString("path", sw.Wrap.Files[i].Path);
                            w.WriteNumber("size", sw.Wrap.Files[i].Size);
                            w.WriteString("sample_sha256", sw.FileDigests[i]);
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
        /// Appends the wrap unless an equal current one is already there. The wrap is a pure function of K_map, the built-for text, the sample parameters, the
        /// files and their digests, so an equal salt, wrapped key and check means "same inputs": nothing to add. Different inputs (new files, new sizes, the
        /// same sizes with other content, as a hotfix with an unchanged version would, or other sample parameters) append. Returns true when the entry changed.
        /// </summary>
        public static bool AddWrap(KeyStoreEntry e, KeyWrap wrap, string[] fileDigests)
        {
            foreach (StoreWrap existing in e.Wraps)
                if (existing.Wrap != null && ConstantTime.AreEqual(existing.Wrap.Salt, wrap.Salt) && ConstantTime.AreEqual(existing.Wrap.WrappedKey, wrap.WrappedKey)
                    && ConstantTime.AreEqual(existing.Wrap.Check, wrap.Check))
                    return false;
            StoreWrap sw = new StoreWrap();
            sw.Wrap = wrap;
            sw.FileDigests = fileDigests;
            e.Wraps.Add(sw);
            return true;
        }

        /// <summary>The shipped key file: every current wrap in insertion order (the player's resolver tries them in this order and reports against the last); retired wraps are never shipped.</summary>
        public static KeyFile ToKeyFile(KeyStoreEntry e)
        {
            KeyFile f = new KeyFile(e.MapKey);
            foreach (StoreWrap sw in e.Wraps)
                if (sw.Wrap != null)
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

    /// <summary>The Arena files one map's key depends on, in key-file order, and how each is sampled.</summary>
    internal sealed class KeySourceSet
    {
        public KeySample Sample;
        public List<string> Paths = new List<string>();
        public List<int> Indices = new List<int>();
    }

    /// <summary>
    /// Which Arena files a map's key depends on (docs/PLAN-ENCRYPTION.md section 6): the table <c>tools/bundle/key-sources.json</c>,
    /// <c>{schema: 2, arena_build, sample: {block_size, blocks}, maps: {map_key: [{index, scene, files: [name, ...]}]}}</c>. Each entry is one scene of the
    /// map (its BuildSettings index) and names the raw resource streams of that scene that exist in the Arena build, <c>sharedassets&lt;index&gt;.assets.resS</c>
    /// and <c>sharedassets&lt;index&gt;.resource</c> in that order (a scene may have neither). These files hold texture, mesh and audio data; Arena's 0.6.0 to
    /// 0.6.1 hotfix rewrote every level and sharedassets file of the four maps and left all of them byte-identical, which is why they replaced the level
    /// files. The key file lists them as <c>EscapeFromTarkovArena_Data/&lt;name&gt;</c> in ascending index order. An extraction manifest with
    /// <c>source.scene_registry</c> (AirPit, Bowl) may be given as well; its scene indices must equal the table's. The table is pinned against the manifests,
    /// the extractor tables and the installed Arena by test_bundle_crypt_tool.py.
    /// </summary>
    internal static class KeySources
    {
        public const string DataFolder = "EscapeFromTarkovArena_Data";
        public const int TableSchema = 2;
        private static readonly Regex FileRule = new Regex(@"^sharedassets([0-9]+)\.(assets\.resS|resource)$", RegexOptions.CultureInvariant);

        public static KeySourceSet Load(string mapKey, string sourcesFile, string manifestFile)
        {
            if (sourcesFile == null)
                throw new UsageException("--sources FILE (tools/bundle/key-sources.json) is required" + (manifestFile == null ? "" : "; --manifest only cross-checks its scene indices"));
            KeySourceSet set = FromTable(mapKey, sourcesFile);
            if (manifestFile != null)
            {
                List<int> fromManifest = FromManifest(manifestFile);
                if (string.Join(",", fromManifest) != string.Join(",", set.Indices))
                    throw new ToolFailure("the key sources table and the manifest list different scenes for '" + mapKey + "' ("
                        + string.Join(",", set.Indices) + " against " + string.Join(",", fromManifest) + ")");
            }
            return set;
        }

        private static KeySourceSet FromTable(string mapKey, string file)
        {
            JsonDocument doc = ParseFile(file);
            using (doc)
            {
                JsonElement root = doc.RootElement;
                JsonElement schema, sample, maps;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new ToolFailure("the key sources table " + file + " is not a JSON object");
                long schemaValue;
                if (!root.TryGetProperty("schema", out schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt64(out schemaValue) || schemaValue != TableSchema)
                    throw new ToolFailure("the key sources table " + file + " must have schema " + TableSchema + " (sample parameters and resource files per scene)");
                if (!root.TryGetProperty("sample", out sample) || sample.ValueKind != JsonValueKind.Object)
                    throw new ToolFailure("the key sources table " + file + " has no 'sample' object");
                KeySourceSet set = new KeySourceSet();
                set.Sample = new KeySample(IntOf(sample, "block_size", file), IntOf(sample, "blocks", file));
                if (set.Sample.Problem() != null)
                    throw new ToolFailure("the key sources table " + file + ": sample " + set.Sample.Problem());
                if (!root.TryGetProperty("maps", out maps) || maps.ValueKind != JsonValueKind.Object)
                    throw new ToolFailure("the key sources table " + file + " has no 'maps' object");
                JsonElement list;
                if (!maps.TryGetProperty(mapKey, out list) || list.ValueKind != JsonValueKind.Array)
                    throw new ToolFailure("the key sources table " + file + " has no entry for '" + mapKey + "'");
                int previous = -1;
                foreach (JsonElement e in list.EnumerateArray())
                {
                    int index = IntOf(e, "index", file);
                    if (index < 0)
                        throw new ToolFailure("an entry of " + file + " has no valid 'index'");
                    if (index == previous)
                        throw new ToolFailure("scene " + index + " is listed twice in " + file);
                    if (index < previous)
                        throw new ToolFailure("the scenes of '" + mapKey + "' in " + file + " are not in ascending index order (" + previous + " before " + index + ")");
                    previous = index;
                    set.Indices.Add(index);
                    JsonElement names;
                    if (!e.TryGetProperty("files", out names) || names.ValueKind != JsonValueKind.Array)
                        throw new ToolFailure("scene " + index + " of '" + mapKey + "' in " + file + " has no 'files' array");
                    string previousKind = null;
                    foreach (JsonElement name in names.EnumerateArray())
                    {
                        string text = name.ValueKind == JsonValueKind.String ? name.GetString() : null;
                        Match m = text == null ? Match.Empty : FileRule.Match(text);
                        if (!m.Success || m.Groups[1].Value != index.ToString(CultureInfo.InvariantCulture))
                            throw new ToolFailure("scene " + index + " of '" + mapKey + "' in " + file + " lists '" + text + "'; only sharedassets"
                                + index + ".assets.resS and sharedassets" + index + ".resource belong to it");
                        string kind = m.Groups[2].Value;
                        if (previousKind != null && (previousKind == kind || kind == "assets.resS"))
                            throw new ToolFailure("scene " + index + " of '" + mapKey + "' in " + file + " must list .assets.resS before .resource, each once");
                        previousKind = kind;
                        set.Paths.Add(DataFolder + "/" + text);
                    }
                }
                if (set.Paths.Count == 0)
                    throw new ToolFailure(file + " lists no files for '" + mapKey + "'");
                if (set.Paths.Count > KeyFile.MaxFilesPerWrap)
                    throw new ToolFailure(file + " lists more than " + KeyFile.MaxFilesPerWrap + " files for '" + mapKey + "' (the key file cap)");
                return set;
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
                    throw new ToolFailure("the manifest " + file + " has no source.scene_registry (Equator's and Bay5's packages do not; give --sources alone)");
                SortedSet<int> set = new SortedSet<int>();
                foreach (JsonElement e in registry.EnumerateArray())
                {
                    int n = IntOf(e, "index", file);
                    if (n < 0 || !set.Add(n))
                        throw new ToolFailure("the manifest " + file + " lists scene " + n + " twice or with a negative index");
                }
                return new List<int>(set);
            }
        }

        private static int IntOf(JsonElement obj, string field, string file)
        {
            JsonElement v;
            int n;
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(field, out v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out n))
                throw new ToolFailure("an entry of " + file + " has no valid '" + field + "'");
            return n;
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
