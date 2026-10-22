#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using ArenaMapPort.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArenaMapPort.Client
{
    /// <summary>One entry of <c>maps</c> in the server's status document.</summary>
    internal sealed class ServerMapStatus
    {
        internal ServerMapStatus(string key, string state, string cause)
        {
            Key = key;
            State = state;
            Cause = cause;
        }

        internal string Key { get; }
        /// <summary>One of <see cref="ArenaStatusContract.MapStates"/>, or whatever a newer server sent (kept as text).</summary>
        internal string State { get; }
        /// <summary>One of <see cref="ArenaStatusContract.MapCauses"/>, or whatever a newer server sent.</summary>
        internal string Cause { get; }

        internal bool Failed => string.Equals(State, ArenaStatusContract.MapFailed, StringComparison.Ordinal);
        internal bool Registered => string.Equals(State, ArenaStatusContract.MapRegistered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The server's answer to <c>GET /arenamapport/status</c> (schema 1), read into plain values. Codes are kept as the server sent them:
    /// the selector treats a value it does not know as "the server said something this client cannot read", never as success.
    /// </summary>
    internal sealed class ServerStatusView
    {
        internal ServerStatusView(int schema, string serverVersion, string gateState, string gateCauseCode, string gateSource,
            string manifestVersion, int mismatchCount, string overrideFile, IReadOnlyList<ServerMapStatus> maps)
        {
            Schema = schema;
            ServerVersion = serverVersion;
            GateState = gateState;
            GateCauseCode = gateCauseCode;
            GateSource = gateSource;
            ManifestVersion = manifestVersion;
            MismatchCount = mismatchCount;
            OverrideFile = overrideFile;
            Maps = maps ?? new ServerMapStatus[0];
            GateCauseKnown = ArenaStatusContract.TryParseCause(gateCauseCode, out ArenaInstallCause cause);
            GateCause = cause;
        }

        internal int Schema { get; }
        /// <summary>The server mod's version (<c>serverVersion</c>), or null.</summary>
        internal string ServerVersion { get; }
        /// <summary>One of <see cref="ArenaStatusContract.GateStates"/> (or an unknown text).</summary>
        internal string GateState { get; }
        /// <summary>The wire code of the gate's cause (<c>none</c>, <c>not_registered</c>, ...).</summary>
        internal string GateCauseCode { get; }
        /// <summary>The cause as an enum; <see cref="ArenaInstallCause.None"/> when <see cref="GateCauseKnown"/> is false.</summary>
        internal ArenaInstallCause GateCause { get; }
        /// <summary>False when the server sent a cause code this client does not know.</summary>
        internal bool GateCauseKnown { get; }
        /// <summary>The gate's source: <c>registry</c>, <c>override</c> or <c>none</c>.</summary>
        internal string GateSource { get; }
        internal string ManifestVersion { get; }
        internal int MismatchCount { get; }
        /// <summary>One of <see cref="ArenaStatusContract.OverrideFileStates"/> (or an unknown text).</summary>
        internal string OverrideFile { get; }
        internal IReadOnlyList<ServerMapStatus> Maps { get; }

        internal bool GateVerified => string.Equals(GateState, ArenaStatusContract.GateVerified, StringComparison.Ordinal);
        internal bool GateNotRun => string.Equals(GateState, ArenaStatusContract.GateNotRun, StringComparison.Ordinal);

        /// <summary>The status of <paramref name="key"/>, or null when the server's list does not mention it.</summary>
        internal ServerMapStatus FindMap(string key)
        {
            foreach (ServerMapStatus map in Maps)
                if (string.Equals(map.Key, key, StringComparison.Ordinal))
                    return map;
            return null;
        }
    }

    /// <summary>
    /// Reads the body of the server's status route (schema 1) for the in-game map notices (docs/progress/map-notices.md).
    ///
    /// What vanilla does. Nothing: SPT has no way for a mod to say why it registered no location, and its client cannot ask. This mod's
    /// server (<c>ArenaServerStatus</c>) records the gate verdict and each map's registration outcome and answers
    /// <see cref="ArenaStatusContract.StatusRoute"/> with a small JSON document of codes.
    ///
    /// What this class does. <see cref="DecodeBody"/> turns the HTTP body into text (SPT's server zlib-compresses its responses unless the
    /// request says otherwise: a body that starts with a zlib header is inflated, a gzip body too, anything else is taken as UTF-8) and
    /// <see cref="TryParse"/> reads the text with Newtonsoft.Json, which the client already ships against (EFT's own copy, used by the
    /// other repairs; no new assembly). Schema 1 is required, unknown fields are ignored, a missing or higher schema, a missing gate or a
    /// document that is not an object read as unavailable, and nothing here throws. Codes are kept as sent and cut to 64 characters, since they
    /// end up in a log line. The client sends no session and reads no path or text from the server: the document holds codes and counts only.
    ///
    /// Host test: docs/progress/map-notices.md (V3, a real 404 from the 0.2.3 server and the live answer of the new server).
    /// </summary>
    internal static class ServerStatusParser
    {
        /// <summary>The largest body or inflated text that is read; a status document is a few hundred bytes.</summary>
        internal const int MaxBytes = 256 * 1024;

        const int MaxCodeLength = 64;
        const int MaxMaps = 64;

        /// <summary>Reads the status text. <paramref name="view"/> is null and <paramref name="error"/> says why when the document is unavailable.</summary>
        internal static bool TryParse(string json, out ServerStatusView view, out string error)
        {
            view = null;
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(json))
                {
                    error = "empty answer";
                    return false;
                }
                if (json.Length > MaxBytes)
                {
                    error = "answer too large";
                    return false;
                }

                JToken root;
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                    root = JToken.ReadFrom(reader);
                var document = root as JObject;
                if (document == null)
                {
                    error = "not a JSON object";
                    return false;
                }

                if (!TryInt(document["schema"], out int schema))
                {
                    error = "no schema";
                    return false;
                }
                if (schema != ArenaStatusContract.StatusSchema)
                {
                    error = "schema " + schema + " is not supported";
                    return false;
                }

                var gate = document["gate"] as JObject;
                if (gate == null)
                {
                    error = "no gate";
                    return false;
                }

                string gateState = Text(gate["state"]);
                if (gateState == null)
                {
                    error = "no gate state";
                    return false;
                }

                var maps = new List<ServerMapStatus>();
                if (document["maps"] is JArray list)
                    foreach (JToken item in list)
                    {
                        if (maps.Count >= MaxMaps)
                            break;
                        var entry = item as JObject;
                        string key = entry == null ? null : Text(entry["key"]);
                        if (key == null)
                            continue;
                        maps.Add(new ServerMapStatus(key, Text(entry["state"]) ?? "", Text(entry["cause"]) ?? ""));
                    }

                view = new ServerStatusView(
                    schema,
                    Text(document["serverVersion"]),
                    gateState,
                    Text(gate["cause"]) ?? "",
                    Text(gate["source"]) ?? ArenaInstallation.SourceNone,
                    Text(gate["manifestVersion"]),
                    TryInt(gate["mismatchCount"], out int mismatches) ? Math.Max(0, mismatches) : 0,
                    Text(gate["overrideFile"]) ?? ArenaStatusContract.OverrideAbsent,
                    maps);
                return true;
            }
            catch (Exception e)
            {
                view = null;
                error = e is JsonException ? "not valid JSON" : e.GetType().Name;
                return false;
            }
        }

        /// <summary>The status document of <paramref name="json"/>, or null when it is unavailable (<see cref="TryParse"/> with the reason dropped).</summary>
        internal static ServerStatusView Parse(string json) => TryParse(json, out ServerStatusView view, out _) ? view : null;

        /// <summary><see cref="DecodeBody"/> then <see cref="TryParse"/>.</summary>
        internal static bool TryParseBody(byte[] body, out ServerStatusView view, out string error)
        {
            string text = DecodeBody(body);
            if (text == null)
            {
                view = null;
                error = "the body could not be decoded";
                return false;
            }
            return TryParse(text, out view, out error);
        }

        /// <summary>
        /// The text of an HTTP body. A zlib stream (first byte 0x78 and the first two bytes a multiple of 31, RFC 1950) is inflated after its two-byte
        /// header, a gzip stream (1F 8B) is unzipped, anything else is UTF-8 (a byte order mark is dropped). Null for an empty body, one larger than
        /// <see cref="MaxBytes"/> or inflated beyond it, damaged compressed data, or a body that decodes to no text. Never throws.
        /// </summary>
        internal static string DecodeBody(byte[] body)
        {
            try
            {
                if (body == null || body.Length == 0 || body.Length > MaxBytes)
                    return null;
                if (body.Length >= 2 && body[0] == 0x78 && ((body[0] << 8) | body[1]) % 31 == 0)
                    using (var input = new MemoryStream(body, 2, body.Length - 2, false))
                    using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
                        return ReadLimited(inflate);
                if (body.Length >= 2 && body[0] == 0x1F && body[1] == 0x8B)
                    using (var input = new MemoryStream(body, false))
                    using (var unzip = new GZipStream(input, CompressionMode.Decompress))
                        return ReadLimited(unzip);
                int start = body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? 3 : 0;
                string text = new UTF8Encoding(false, false).GetString(body, start, body.Length - start);
                return text.Length == 0 ? null : text;
            }
            catch
            {
                return null;
            }
        }

        static string ReadLimited(Stream source)
        {
            var buffer = new byte[4096];
            using (var output = new MemoryStream())
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > MaxBytes)
                        return null;
                    output.Write(buffer, 0, read);
                }
                int start = output.Length >= 3 && output.GetBuffer()[0] == 0xEF && output.GetBuffer()[1] == 0xBB && output.GetBuffer()[2] == 0xBF ? 3 : 0;
                string text = new UTF8Encoding(false, false).GetString(output.GetBuffer(), start, (int)output.Length - start);
                return text.Length == 0 ? null : text;
            }
        }

        /// <summary>A string token, trimmed and cut to <see cref="MaxCodeLength"/>; null for a missing, null or non-string token.</summary>
        static string Text(JToken token)
        {
            if (token == null || token.Type != JTokenType.String)
                return null;
            string value = (string)token;
            if (value == null)
                return null;
            value = value.Trim();
            return value.Length <= MaxCodeLength ? value : value.Substring(0, MaxCodeLength);
        }

        static bool TryInt(JToken token, out int value)
        {
            value = 0;
            if (token == null || token.Type != JTokenType.Integer)
                return false;
            try
            {
                value = (int)token;
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }
}
