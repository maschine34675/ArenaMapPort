#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ArenaMapPort.Crypto
{
    /// <summary>A key file that cannot be used: unreadable JSON, wrong schema, a value outside its rules. The message is the reason shown to the player (E2).</summary>
    public sealed class KeyFileException : Exception
    {
        public KeyFileException(string message) : base(message) { }
    }

    /// <summary>One Arena level file a key wrap depends on: a plain relative path ('/' separated) inside the installation and the file's size in bytes. Never a hash.</summary>
    public sealed class KeyFileEntry
    {
        public KeyFileEntry(string path, long size)
        {
            Path = path;
            Size = size;
        }

        public string Path { get; private set; }
        public long Size { get; private set; }
    }

    /// <summary>
    /// The content key of a map wrapped for one Arena build: which level files (paths and sizes only) unlock it, the salt of the key-derivation
    /// step, the wrapped key and its check value (docs/PLAN-ENCRYPTION.md 0.2/0.4). <see cref="BuiltFor"/> is informational (the launcher
    /// manifest version) and never gates anything.
    /// </summary>
    public sealed class KeyWrap
    {
        public KeyWrap(string builtFor, IList<KeyFileEntry> files, byte[] salt, byte[] wrappedKey, byte[] check)
        {
            BuiltFor = builtFor;
            Files = files;
            Salt = salt;
            WrappedKey = wrappedKey;
            Check = check;
        }

        public string BuiltFor { get; private set; }
        public IList<KeyFileEntry> Files { get; private set; }
        public byte[] Salt { get; private set; }
        public byte[] WrappedKey { get; private set; }
        public byte[] Check { get; private set; }
    }

    /// <summary>
    /// The path rule for the files a key file may name: a plain relative path inside the Arena installation. It is the rule of
    /// ArenaInstallation.IsPlainRelativePath (empty, rooted or UNC paths, any colon, and every segment that is empty or made only of dots and
    /// spaces are refused: legacy path normalisation trims them, so ".. " would leave the installation), applied to both '/' and '\' as
    /// separators because the key file spells paths with '/'. One addition: control characters (below U+0020) are refused. The two rules are
    /// tested equal on a table of paths without control characters. Key file content is untrusted data, so this runs on every path before any file is opened.
    /// </summary>
    public static class KeyPath
    {
        public static bool IsSafe(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf(':') >= 0)
                return false;
            string logical = path.Replace('/', '\\');
            if (logical[0] == '\\')
                return false;
            for (int i = 0; i < logical.Length; i++)
                if (logical[i] < ' ')
                    return false;
            foreach (string segment in logical.Split('\\'))
                if (segment.TrimEnd(' ', '.').Length == 0)
                    return false;
            return true;
        }
    }

    /// <summary>
    /// The shipped key file <c>keys/&lt;map_key&gt;.json</c>: one object per Arena build in <see cref="Wraps"/> (several entries let later Arena builds be
    /// added without touching the bundles). Parsed by a hand-written minimal JSON reader (no JSON package in the client), as untrusted
    /// input: schema must be 1, at most 16 wraps, at most 32 files per wrap, a file of at most 64 KiB, safe relative paths, exact hex lengths;
    /// unknown keys are ignored, duplicate keys are refused. <see cref="ToCanonicalJson"/> writes the one canonical byte form (fixed key order,
    /// 2-space indent, lowercase hex, LF, UTF-8 without BOM, trailing newline) so a rebuild with the same inputs gives an identical file.
    /// Proved by BundleCryptoTests (golden string, round trip, malformed inputs, caps).
    /// </summary>
    public sealed class KeyFile
    {
        public const int SchemaVersion = 1;
        public const int MaxBytes = 64 * 1024;
        public const int MaxWraps = 16;
        public const int MaxFilesPerWrap = 32;
        public const int MaxBuiltForLength = 64;

        readonly List<KeyWrap> _wraps = new List<KeyWrap>();

        public KeyFile(string mapKey)
        {
            MapKey = mapKey;
        }

        public string MapKey { get; private set; }

        /// <summary>The wraps in file order (the resolver tries them in this order). Mutable so a builder can append.</summary>
        public IList<KeyWrap> Wraps
        {
            get { return _wraps; }
        }

        /// <summary>Parses key file text (a leading byte order mark is tolerated). Throws <see cref="KeyFileException"/> with the reason.</summary>
        public static KeyFile Parse(string json)
        {
            if (json == null)
                throw new KeyFileException("the key file is empty");
            if (json.Length > 0 && json[0] == '﻿')
                json = json.Substring(1);
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
                throw new KeyFileException("the key file is larger than 64 KiB");
            object root = MiniJson.Parse(json);
            Dictionary<string, object> obj = root as Dictionary<string, object>;
            if (obj == null)
                throw new KeyFileException("the key file is not a JSON object");

            long schema = RequireInteger(obj, "schema", "the key file");
            if (schema != SchemaVersion)
                throw new KeyFileException("unsupported key file schema " + schema.ToString(CultureInfo.InvariantCulture));
            KeyFile file = new KeyFile(RequireString(obj, "map_key", "the key file"));
            List<object> wraps = RequireArray(obj, "wraps", "the key file");
            if (wraps.Count > MaxWraps)
                throw new KeyFileException("the key file lists more than " + MaxWraps + " wraps");
            for (int w = 0; w < wraps.Count; w++)
            {
                Dictionary<string, object> wrap = wraps[w] as Dictionary<string, object>;
                string where = "wrap " + w;
                if (wrap == null)
                    throw new KeyFileException(where + " is not an object");
                string builtFor = RequireString(wrap, "built_for", where);
                List<object> files = RequireArray(wrap, "files", where);
                if (files.Count > MaxFilesPerWrap)
                    throw new KeyFileException(where + " lists more than " + MaxFilesPerWrap + " files");
                List<KeyFileEntry> entries = new List<KeyFileEntry>();
                for (int f = 0; f < files.Count; f++)
                {
                    Dictionary<string, object> entry = files[f] as Dictionary<string, object>;
                    if (entry == null)
                        throw new KeyFileException(where + " file " + f + " is not an object");
                    entries.Add(new KeyFileEntry(RequireString(entry, "path", where + " file " + f), RequireInteger(entry, "size", where + " file " + f)));
                }
                byte[] salt = RequireHex(wrap, "salt", where);
                byte[] wrapped = RequireHex(wrap, "wrapped_key", where);
                byte[] check = RequireHex(wrap, "check", where);
                file._wraps.Add(new KeyWrap(builtFor, entries, salt, wrapped, check));
            }
            file.Validate();
            return file;
        }

        /// <summary>Parses key file bytes (strict UTF-8, optional BOM, at most 64 KiB).</summary>
        public static KeyFile Parse(byte[] bytes)
        {
            if (bytes == null)
                throw new KeyFileException("the key file is empty");
            if (bytes.Length > MaxBytes)
                throw new KeyFileException("the key file is larger than 64 KiB");
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (ArgumentException)
            {
                throw new KeyFileException("the key file is not valid UTF-8");
            }
            return Parse(text);
        }

        /// <summary>Reads and parses a key file. A missing file is a <see cref="FileNotFoundException"/> (the caller reports it separately); a file over 64 KiB is refused before it is read.</summary>
        public static KeyFile Load(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length > MaxBytes)
                    throw new KeyFileException("the key file is larger than 64 KiB");
                byte[] bytes = new byte[(int)fs.Length];
                int total = 0;
                while (total < bytes.Length)
                {
                    int n = fs.Read(bytes, total, bytes.Length - total);
                    if (n <= 0)
                        break;
                    total += n;
                }
                if (total != bytes.Length)
                    throw new KeyFileException("the key file could not be read completely");
                return Parse(bytes);
            }
        }

        /// <summary>Checks every rule of the format (also run before writing, so a file that cannot be parsed back is never produced).</summary>
        public void Validate()
        {
            if (!Ampb.IsValidMapKey(MapKey))
                throw new KeyFileException("map_key is not 1.." + Ampb.MaxMapKeyLength + " characters of [a-z0-9_]");
            if (_wraps.Count > MaxWraps)
                throw new KeyFileException("the key file lists more than " + MaxWraps + " wraps");
            for (int w = 0; w < _wraps.Count; w++)
            {
                KeyWrap wrap = _wraps[w];
                string where = "wrap " + w;
                if (wrap == null || wrap.Files == null)
                    throw new KeyFileException(where + " is incomplete");
                if (wrap.BuiltFor == null || wrap.BuiltFor.Length > MaxBuiltForLength)
                    throw new KeyFileException(where + ": built_for is missing or longer than " + MaxBuiltForLength + " characters");
                foreach (char c in wrap.BuiltFor)
                    if (c < ' ' || c == '\u007f')
                        throw new KeyFileException(where + ": built_for contains a control character");
                if (wrap.Files.Count > MaxFilesPerWrap)
                    throw new KeyFileException(where + " lists more than " + MaxFilesPerWrap + " files");
                foreach (KeyFileEntry e in wrap.Files)
                {
                    if (e == null || !KeyPath.IsSafe(e.Path))
                        throw new KeyFileException(where + ": '" + (e == null ? "" : e.Path) + "' is not a plain relative path");
                    if (e.Size < 0)
                        throw new KeyFileException(where + ": the size of '" + e.Path + "' is negative");
                }
                if (wrap.Salt == null || wrap.Salt.Length != 32) throw new KeyFileException(where + ": salt must be 32 bytes");
                if (wrap.WrappedKey == null || wrap.WrappedKey.Length != 32) throw new KeyFileException(where + ": wrapped_key must be 32 bytes");
                if (wrap.Check == null || wrap.Check.Length != 32) throw new KeyFileException(where + ": check must be 32 bytes");
            }
        }

        /// <summary>The canonical bytes as text: fixed key order, 2-space indent, lowercase hex, LF, trailing newline.</summary>
        public string ToCanonicalJson()
        {
            Validate();
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema\": ").Append(SchemaVersion).Append(",\n");
            sb.Append("  \"map_key\": ").Append(Quote(MapKey)).Append(",\n");
            sb.Append("  \"wraps\": [");
            for (int w = 0; w < _wraps.Count; w++)
            {
                KeyWrap wrap = _wraps[w];
                sb.Append(w == 0 ? "\n" : ",\n");
                sb.Append("    {\n");
                sb.Append("      \"built_for\": ").Append(Quote(wrap.BuiltFor)).Append(",\n");
                sb.Append("      \"files\": [");
                for (int f = 0; f < wrap.Files.Count; f++)
                {
                    sb.Append(f == 0 ? "\n" : ",\n");
                    sb.Append("        { \"path\": ").Append(Quote(wrap.Files[f].Path)).Append(", \"size\": ")
                      .Append(wrap.Files[f].Size.ToString(CultureInfo.InvariantCulture)).Append(" }");
                }
                sb.Append(wrap.Files.Count == 0 ? "],\n" : "\n      ],\n");
                sb.Append("      \"salt\": \"").Append(HexCodec.ToHex(wrap.Salt))
                  .Append("\", \"wrapped_key\": \"").Append(HexCodec.ToHex(wrap.WrappedKey))
                  .Append("\", \"check\": \"").Append(HexCodec.ToHex(wrap.Check)).Append("\"\n");
                sb.Append("    }");
            }
            sb.Append(_wraps.Count == 0 ? "]\n" : "\n  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>The canonical bytes (UTF-8 without a byte order mark).</summary>
        public byte[] ToCanonicalBytes()
        {
            return new UTF8Encoding(false).GetBytes(ToCanonicalJson());
        }

        /// <summary>Writes the canonical bytes to <paramref name="path"/> (overwriting).</summary>
        public void Save(string path)
        {
            File.WriteAllBytes(path, ToCanonicalBytes());
        }

        static string Quote(string text)
        {
            StringBuilder sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string RequireString(Dictionary<string, object> obj, string key, string where)
        {
            object v;
            if (!obj.TryGetValue(key, out v))
                throw new KeyFileException(where + " has no '" + key + "'");
            string s = v as string;
            if (s == null)
                throw new KeyFileException(where + ": '" + key + "' must be a string");
            return s;
        }

        static long RequireInteger(Dictionary<string, object> obj, string key, string where)
        {
            object v;
            if (!obj.TryGetValue(key, out v))
                throw new KeyFileException(where + " has no '" + key + "'");
            if (!(v is long))
                throw new KeyFileException(where + ": '" + key + "' must be an integer");
            return (long)v;
        }

        static List<object> RequireArray(Dictionary<string, object> obj, string key, string where)
        {
            object v;
            if (!obj.TryGetValue(key, out v))
                throw new KeyFileException(where + " has no '" + key + "'");
            List<object> list = v as List<object>;
            if (list == null)
                throw new KeyFileException(where + ": '" + key + "' must be an array");
            return list;
        }

        static byte[] RequireHex(Dictionary<string, object> obj, string key, string where)
        {
            string s = RequireString(obj, key, where);
            byte[] bytes;
            if (!HexCodec.TryParse(s, 32, out bytes))
                throw new KeyFileException(where + ": '" + key + "' must be 64 hex digits");
            return bytes;
        }
    }

    /// <summary>
    /// A small strict JSON reader for key files: objects (as Dictionary), arrays (List), strings with all escapes, integers (long) and other numbers
    /// (double), true/false/null; nesting depth at most 16, duplicate object keys refused, trailing content refused. Hand-written because the client
    /// ships no JSON library (the same approach as the launcher-manifest reader in ArenaInstallation). Errors are <see cref="KeyFileException"/>.
    /// </summary>
    internal sealed class MiniJson
    {
        const int MaxDepth = 16;
        readonly string _s;
        int _i;

        MiniJson(string text)
        {
            _s = text;
        }

        public static object Parse(string text)
        {
            MiniJson p = new MiniJson(text);
            p.SkipWhitespace();
            if (p._i >= text.Length)
                throw p.Fail("the key file is empty");
            object value = p.ParseValue(0);
            p.SkipWhitespace();
            if (p._i != text.Length)
                throw p.Fail("unexpected content after the JSON value");
            return value;
        }

        KeyFileException Fail(string message)
        {
            return new KeyFileException(message + " (JSON offset " + _i.ToString(CultureInfo.InvariantCulture) + ")");
        }

        void SkipWhitespace()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    _i++;
                else
                    break;
            }
        }

        object ParseValue(int depth)
        {
            if (depth > MaxDepth)
                throw Fail("JSON nested too deeply");
            if (_i >= _s.Length)
                throw Fail("unexpected end of JSON");
            char c = _s[_i];
            switch (c)
            {
                case '{': return ParseObject(depth);
                case '[': return ParseArray(depth);
                case '"': return ParseString();
                case 't': ExpectLiteral("true"); return true;
                case 'f': ExpectLiteral("false"); return false;
                case 'n': ExpectLiteral("null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return ParseNumber();
                    throw Fail("unexpected character '" + c + "'");
            }
        }

        void ExpectLiteral(string literal)
        {
            if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                throw Fail("invalid literal");
            _i += literal.Length;
        }

        object ParseObject(int depth)
        {
            _i++;
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            SkipWhitespace();
            if (_i < _s.Length && _s[_i] == '}')
            {
                _i++;
                return result;
            }
            while (true)
            {
                SkipWhitespace();
                if (_i >= _s.Length || _s[_i] != '"')
                    throw Fail("an object key must be a string");
                string key = ParseString();
                SkipWhitespace();
                if (_i >= _s.Length || _s[_i] != ':')
                    throw Fail("expected ':' after an object key");
                _i++;
                SkipWhitespace();
                object value = ParseValue(depth + 1);
                if (result.ContainsKey(key))
                    throw Fail("duplicate key '" + key + "'");
                result[key] = value;
                SkipWhitespace();
                if (_i >= _s.Length)
                    throw Fail("unterminated object");
                if (_s[_i] == ',')
                {
                    _i++;
                    continue;
                }
                if (_s[_i] == '}')
                {
                    _i++;
                    return result;
                }
                throw Fail("expected ',' or '}'");
            }
        }

        object ParseArray(int depth)
        {
            _i++;
            List<object> result = new List<object>();
            SkipWhitespace();
            if (_i < _s.Length && _s[_i] == ']')
            {
                _i++;
                return result;
            }
            while (true)
            {
                SkipWhitespace();
                result.Add(ParseValue(depth + 1));
                SkipWhitespace();
                if (_i >= _s.Length)
                    throw Fail("unterminated array");
                if (_s[_i] == ',')
                {
                    _i++;
                    continue;
                }
                if (_s[_i] == ']')
                {
                    _i++;
                    return result;
                }
                throw Fail("expected ',' or ']'");
            }
        }

        string ParseString()
        {
            _i++;
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (_i >= _s.Length)
                    throw Fail("unterminated string");
                char c = _s[_i++];
                if (c == '"')
                    return sb.ToString();
                if (c < ' ')
                    throw Fail("a control character inside a string");
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (_i >= _s.Length)
                    throw Fail("unterminated escape");
                char e = _s[_i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_i + 4 > _s.Length)
                            throw Fail("truncated \\u escape");
                        int code = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int d = HexValue(_s[_i + k]);
                            if (d < 0)
                                throw Fail("invalid \\u escape");
                            code = (code << 4) | d;
                        }
                        _i += 4;
                        sb.Append((char)code);
                        break;
                    default:
                        throw Fail("invalid escape '\\" + e + "'");
                }
            }
        }

        static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        object ParseNumber()
        {
            int start = _i;
            if (_s[_i] == '-')
                _i++;
            if (_i >= _s.Length || _s[_i] < '0' || _s[_i] > '9')
                throw Fail("invalid number");
            if (_s[_i] == '0')
            {
                _i++;
                if (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9')
                    throw Fail("a number must not have a leading zero");
            }
            else
            {
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9')
                    _i++;
            }
            bool integer = true;
            if (_i < _s.Length && _s[_i] == '.')
            {
                integer = false;
                _i++;
                int digits = 0;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') { _i++; digits++; }
                if (digits == 0)
                    throw Fail("invalid number");
            }
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                integer = false;
                _i++;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-'))
                    _i++;
                int digits = 0;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') { _i++; digits++; }
                if (digits == 0)
                    throw Fail("invalid number");
            }
            string text = _s.Substring(start, _i - start);
            if (integer)
            {
                long l;
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l))
                    return l;
            }
            double d;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw Fail("invalid number");
            return d;
        }
    }
}
