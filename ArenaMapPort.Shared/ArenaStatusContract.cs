#nullable disable
using System;
using System.Collections.Generic;

namespace ArenaMapPort.Shared
{
    /// <summary>
    /// Why the Arena installation was not verified, as a machine-readable code (one per failure site of
    /// <see cref="ArenaInstallation"/>). The text in <see cref="ArenaInstallationResult.Reason"/> stays the human sentence for the
    /// logs; this is what the status route sends and what the client's in-game notice chooses its text by. Never reorder or
    /// renumber: the wire carries the codes of <see cref="ArenaStatusContract.CauseCode"/>, not the numbers, but the tests pin both.
    /// </summary>
    public enum ArenaInstallCause
    {
        /// <summary>The installation was verified (or no check has failed yet).</summary>
        None = 0,

        /// <summary>The Windows registry cannot be read on this system (not Windows, type missing).</summary>
        RegistryUnavailable,

        /// <summary>No launcher entry for Arena in the registry and no override path was given.</summary>
        NotRegistered,

        /// <summary>The folder does not exist, is a file, was not given or is not a valid path.</summary>
        FolderMissing,

        /// <summary>The folder exists but does not contain EscapeFromTarkovArena.exe.</summary>
        NotArenaFolder,

        /// <summary>The launcher manifest ConsistencyInfo is not in the folder.</summary>
        ManifestMissing,

        /// <summary>The manifest is too large, unreadable, unparseable, has no version or no entries, contradicts itself or fails a plausibility floor.</summary>
        ManifestInvalid,

        /// <summary>Listed files are missing or have a different size: incomplete, still updating or damaged.</summary>
        FilesMismatch,

        /// <summary>The check itself failed unexpectedly (an exception).</summary>
        CheckFailed,
    }

    /// <summary>
    /// The wire contract of the server's read-only status route <c>GET /arenamapport/status</c> (schema 1), compiled into the
    /// server mod, the client plugin and (through the server assembly) the tests, so both sides name the same route and the same codes.
    ///
    /// Vanilla SPT has no way to tell a client why a mod did not register a location; the Arena gate (user decision 2026-10-19)
    /// makes "no maps" a normal state of an install without Arena, and the player used to see nothing at all. The server records
    /// its gate verdict and the outcome of every map registration (ArenaServerStatus) and answers this route with the codes below;
    /// the client reads it only when maps are missing and turns the codes into a message with the fix.
    ///
    /// Privacy: the route needs no session, so the payload carries codes, counts and the Arena version only: no reason text, no
    /// install path, no server path, no hash. Host evidence: docs/progress/map-notices.md.
    /// </summary>
    public static class ArenaStatusContract
    {
        /// <summary>The route the status router answers (GET, no body, no session needed).</summary>
        public const string StatusRoute = "/arenamapport/status";

        /// <summary>The JSON <c>schema</c> field. A reader accepts exactly this value and treats a missing or higher one as unavailable.</summary>
        public const int StatusSchema = 1;

        // ---- gate.state ----
        /// <summary>The server mod has not run its gate yet (nothing recorded).</summary>
        public const string GateNotRun = "not_run";
        public const string GateVerified = "verified";
        public const string GateNotVerified = "not_verified";
        /// <summary>The gate threw (fails closed, the maps stay unregistered).</summary>
        public const string GateError = "error";

        // ---- gate.overrideFile: the optional config\arena-install.json next to the server DLL ----
        public const string OverrideAbsent = "absent";
        /// <summary>The file exists and gave an install path.</summary>
        public const string OverrideUsed = "used";
        /// <summary>The file exists but was unusable and ignored (not valid JSON, no installPath, unreadable).</summary>
        public const string OverrideIgnored = "ignored";

        // ---- maps[].state ----
        public const string MapNotRegistered = "not_registered";
        public const string MapRegistered = "registered";
        public const string MapFailed = "failed";

        // ---- maps[].cause ----
        public const string MapCauseNone = "none";
        /// <summary>The map was not registered because the gate did not verify the installation.</summary>
        public const string MapCauseGate = "gate";
        public const string MapCauseBaseJsonParse = "base_json_parse";
        public const string MapCauseBaseJsonMissing = "base_json_missing";
        /// <summary>Registration threw (key or identity conflict, a config step).</summary>
        public const string MapCauseRegistration = "registration";

        /// <summary>Every <c>gate.state</c> value, for parity checks.</summary>
        public static readonly IReadOnlyList<string> GateStates = new[] { GateNotRun, GateVerified, GateNotVerified, GateError };

        /// <summary>Every <c>gate.overrideFile</c> value.</summary>
        public static readonly IReadOnlyList<string> OverrideFileStates = new[] { OverrideAbsent, OverrideUsed, OverrideIgnored };

        /// <summary>Every <c>maps[].state</c> value.</summary>
        public static readonly IReadOnlyList<string> MapStates = new[] { MapNotRegistered, MapRegistered, MapFailed };

        /// <summary>Every <c>maps[].cause</c> value.</summary>
        public static readonly IReadOnlyList<string> MapCauses = new[]
        {
            MapCauseNone, MapCauseGate, MapCauseBaseJsonParse, MapCauseBaseJsonMissing, MapCauseRegistration,
        };

        /// <summary>The wire code of a cause: none, registry_unreadable, not_registered, folder_missing, not_arena_folder, manifest_missing, manifest_invalid, files_mismatch, check_failed.</summary>
        public static string CauseCode(ArenaInstallCause cause)
        {
            switch (cause)
            {
                case ArenaInstallCause.None: return "none";
                case ArenaInstallCause.RegistryUnavailable: return "registry_unreadable";
                case ArenaInstallCause.NotRegistered: return "not_registered";
                case ArenaInstallCause.FolderMissing: return "folder_missing";
                case ArenaInstallCause.NotArenaFolder: return "not_arena_folder";
                case ArenaInstallCause.ManifestMissing: return "manifest_missing";
                case ArenaInstallCause.ManifestInvalid: return "manifest_invalid";
                case ArenaInstallCause.FilesMismatch: return "files_mismatch";
                case ArenaInstallCause.CheckFailed: return "check_failed";
                default: return "check_failed"; // an out-of-range value is a bug of the caller, never "none"
            }
        }

        /// <summary>The cause of a wire code (ordinal match). False, with <see cref="ArenaInstallCause.None"/>, for an unknown or null code.</summary>
        public static bool TryParseCause(string code, out ArenaInstallCause cause)
        {
            switch (code)
            {
                case "none": cause = ArenaInstallCause.None; return true;
                case "registry_unreadable": cause = ArenaInstallCause.RegistryUnavailable; return true;
                case "not_registered": cause = ArenaInstallCause.NotRegistered; return true;
                case "folder_missing": cause = ArenaInstallCause.FolderMissing; return true;
                case "not_arena_folder": cause = ArenaInstallCause.NotArenaFolder; return true;
                case "manifest_missing": cause = ArenaInstallCause.ManifestMissing; return true;
                case "manifest_invalid": cause = ArenaInstallCause.ManifestInvalid; return true;
                case "files_mismatch": cause = ArenaInstallCause.FilesMismatch; return true;
                case "check_failed": cause = ArenaInstallCause.CheckFailed; return true;
                default: cause = ArenaInstallCause.None; return false;
            }
        }

        /// <summary>Whether <paramref name="value"/> is one of <paramref name="allowed"/> (ordinal).</summary>
        public static bool IsOneOf(string value, IReadOnlyList<string> allowed)
        {
            if (value == null || allowed == null)
                return false;
            for (int i = 0; i < allowed.Count; i++)
                if (string.Equals(allowed[i], value, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
