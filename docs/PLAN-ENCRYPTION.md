# Plan: Encrypted map bundles — stage 2 of the Arena gate

Source: user decision 2026-10-19 ("Stufe 2"), after stage 1 (release 0.2.3, merged into `main` as `12c3731`; design record
[progress/arena-install-check.md](progress/arena-install-check.md)). Written 2026-10-19 from a read-only survey of the files named in section 1
and of the live Arena 0.6.1.0.48206 install on this VM; nothing is implemented yet. Evidence is quoted as file:line, byte counts or measured
values; inferences are marked **(inferred)**. Status will live in [HANDOFF.md](../HANDOFF.md) and `docs/progress/bundle-encryption.md`
(created by DOCS); this file is the plan, not the record.

Intent: every shipped map bundle (`<key>_scenes|navmesh|preset.bundle`) is encrypted, and the content key is derived from the bytes of the
Arena level files of that map, so the bundles are useless without the player's own Arena files. **It is not copy protection** (0.1). It moves the
bar from "download and play" to "needs the game's own files", and the wording in README, FORGE and the release notes must say exactly that.

## 0. Decisions

### 0.1 Threat model and honest limits

| Actor | Outcome |
|---|---|
| Player without Arena (the audience of stage 1) | Cannot derive the key, cannot read or load a bundle. Stage 1 already stops the plugin first. |
| Arena owner | Works as intended. |
| Arena owner who shares `K_map` (32 bytes), a derived IKM, or decrypted bundles | Nothing prevents it. Any owner can run `BundleCrypt decrypt` (the tool, the format and the client are public source). |
| Someone with Arena's files but no purchase | Works: the key is tied to files, not to a license. |
| Someone who patches the client DLL | Skips the gate, still needs the key. |
| Leak of the build machine's key store (`work/release-keys`) | Every released bundle of that map is readable. Recovery = new `K_map`, re-encrypt, re-release (open decision D9). |

What stays **plaintext** in the ZIP after stage 2 (possible stage 3, not planned): the BSG-derived art and audio (`loadscreens/`, `banners/`,
`map-selection/`, `acoustics/*/*.wav` and the bake, `flares/*/textures/*.bin`, `water/*/textures/*.bin`, `locales/map-descriptions.json`) and the
derived data (`dynamicmaps/` SVGs, `ai/*.ai.json`, `material-shaders.*.json`, `db/*/base.json`). Meshes, materials, textures, scene hierarchy
and the NavMesh are in the bundles, so they are the part this stage covers. The README must not claim more.

### 0.2 Key hierarchy (exact parameters)

> 2026-10-20: the IKM, the HKDF info, the salt and the check below are those of key source v1 (whole level files). Key source v2 (section 6)
> keeps the structure and `K_map`, replaces the files and the per-file hash with a sampled digest of the resource streams, and moves every
> label to `v2`.

- **`K_map`**: 32 random bytes per map, generated once by the tool and kept in `work/release-keys/<map_key>.key` (ignored by git, never shipped).
  It is not derivable from `dist/`. Never regenerated without `--rotate` (D9).
- **IKM** = `SHA-256(file_1) || SHA-256(file_2) || ...` (32 bytes per file, whole-file hashes), files in the order of the key file's `files` list =
  ascending numeric level index. A published hash would let anyone derive the key, so the key file carries paths and sizes only.
- **KEK** (per wrap entry): `PRK = HMAC-SHA256(key = salt, data = IKM)` (HKDF-Extract, RFC 5869), `OKM = HKDF-Expand(PRK, info, 72)`,
  `info = UTF-8("ArenaMapPort bundle key v1 " + map_key)`; `KEK_enc = OKM[0..32)`, `KEK_mac = OKM[32..64)`, `wrap_nonce = OKM[64..72)`.
- **Wrap scheme: AES-256-CTR + HMAC-SHA256 (encrypt-then-MAC)**, not AES-KW (RFC 3394): the managed fallback (`System.Security.Cryptography`) has no key wrap, so it would be a third primitive to implement and test on both providers,
  while CTR + HMAC reuse the two primitives the container needs anyway: the whole feature needs one cipher mode and one MAC. `wrapped_key = AES-256-CTR(KEK_enc,
  nonce = wrap_nonce, counter0 = 0, K_map)` (32 bytes); `check = HMAC-SHA256(KEK_mac, "ArenaMapPort wrap check v1" 00 map_key 00 wrapped_key)`.
  Unwrap verifies `check` first (constant-time compare, own helper: `CryptographicOperations` is not in netstandard2.1), then decrypts.
- **`salt`** (32 bytes, per wrap entry) is deterministic: `HMAC-SHA256(K_map, "ArenaMapPort wrap salt v1" 00 map_key 00 built_for 00 (path 00 size 00)*)`
  with decimal ASCII sizes. A rebuild with the same inputs gives a byte-identical key file; a rotated `K_map` changes every salt (no CTR reuse).
- **Container subkeys** from `K_map` (HKDF-Expand with PRK = `K_map`, one block each, Extract skipped because `K_map` is uniform, RFC 5869 3.3):
  `K_enc = HMAC(K_map, "ArenaMapPort bundle enc v1" 01)`, `K_mac = HMAC(..."mac v1" 01)`, `K_nonce = HMAC(..."nonce v1" 01)`. The brief says
  "HMAC under K_map"; separate subkeys are the refinement (D1).

### 0.3 Which Arena files, per map

> 2026-10-20: superseded by section 6. The level files listed here changed in Arena's 0.6.0 to 0.6.1 hotfix; the keys now come from the
> scenes' raw resource streams (`sharedassets<N>.assets.resS` / `.resource`), table schema 2.

The key files come from the scenes the map is built from. The accepted manifests do **not** all carry them: `airpit-native-v9` and `bowl-native-v2`
have `source.scene_registry` [{index, name, asset_path}] (AirPit includes the partial scenes 37 and 38), but `equator-native-v5` and `bay5-native-v5`
have only scene names (`scenes: [...]`); their indices live in `extract_equator.SCENE_REGISTRY` (extract_equator.py:71) and `extract_bay5.BAY5_SCENE_NAMES`
(extract_bay5.py:78). So the build reads a tracked table, **`tools/bundle/key-sources.json`** (`{schema, arena_build, maps: {key: [{index, scene}]}}`), pinned
by `tools/tests/test_bundle_key_sources.py` against the four sources (manifest registry where present, extractor tables otherwise), D6. File:
`EscapeFromTarkovArena_Data\level<index>` (`ArenaInstallation.LevelPrefix`, ArenaInstallation.cs:126); the key file stores it with `/`.

| Map | Level indices (ascending) | Files | Bytes (Arena 0.6.1.0.48206) | Warm SHA-256 (python, this VM) |
|---|---|---:|---:|---:|
| arena_equator | 51 53 55 57 58 | 5 | 21,817,368 | 32 ms |
| arena_bay5 | 79 80 81 82 83 84 86 88 89 | 9 | 31,784,868 | 52 ms |
| arena_airpit | 28 29 37 38 40 324 325 | 7 | 38,725,724 | 58 ms |
| arena_bowl | 41 43 45 46 47 48 95 | 7 | 41,126,600 | 67 ms |

All 28 files exist on this VM's 0.6.1 install. They are small (a map's scenes, not its sharedassets), so key derivation costs well under a second
even on Mono's managed SHA-256 **(inferred, 100 MB/s: 0.2-0.4 s)**.

### 0.4 Key file `keys/<map_key>.json` (shipped, `BepInEx/plugins/maschine-ArenaMapPort/keys/`)

> 2026-10-20: the shipped key files are schema 2 (section 6): each wrap adds `"sample": { "block_size": 65536, "blocks": 16 }` after
> `built_for` and lists resource streams instead of level files; the client refuses schema 1.

The brief's flat fields become one object per Arena build in a `wraps` array (several entries per map are required for Arena updates, 0.8):

```json
{
  "schema": 1,
  "map_key": "arena_bowl",
  "wraps": [
    {
      "built_for": "0.6.1.0.48206",
      "files": [
        { "path": "EscapeFromTarkovArena_Data/level41", "size": 18935904 },
        { "path": "EscapeFromTarkovArena_Data/level43", "size": 10015988 },
        { "path": "EscapeFromTarkovArena_Data/level45", "size": 888240 },
        { "path": "EscapeFromTarkovArena_Data/level46", "size": 6121376 },
        { "path": "EscapeFromTarkovArena_Data/level47", "size": 4658864 },
        { "path": "EscapeFromTarkovArena_Data/level48", "size": 503212 },
        { "path": "EscapeFromTarkovArena_Data/level95", "size": 3016 }
      ],
      "salt": "<64 hex>", "wrapped_key": "<64 hex>", "check": "<64 hex>"
    }
  ]
}
```

Canonical bytes: fixed key order as above, 2-space indent, lowercase hex, LF, UTF-8 without BOM, trailing newline. `built_for` is informational (the launcher
manifest `Version`, `ArenaInstallationResult.ManifestVersion`); it never gates anything. The client parser (own minimal reader in `ArenaMapPort.Crypto`,
no JSON package, as `ArenaManifest` in ArenaInstallation.cs:699) accepts unknown keys, rejects schema != 1, more than 16 wraps, more than 32 files per wrap,
a key file over 64 KiB, a path that is not a plain relative path inside the installation (own rule `KeyPath.IsSafe`, tested equivalent to
`ArenaInstallation.IsPlainRelativePath`, ArenaInstallation.cs:436, on a 40-row table) and malformed hex. Key file content is untrusted data.

### 0.5 Container `AMPB` v1 (same file names as today; all integers little-endian)

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | magic `41 4D 50 42` ("AMPB") |
| 4 | 2 | version = 1 (any other value is refused) |
| 6 | 2 | flags = 0 (reserved, non-zero refused) |
| 8 | 4 | chunk_size = 1,048,576 (multiple of 16, 4 KiB..16 MiB accepted so tests can use small chunks) |
| 12 | 4 | chunk_count = ceil(plaintext_length / chunk_size) |
| 16 | 8 | plaintext_length (> 0) |
| 24 | 8 | payload_offset (absolute) = table_offset + 32 * chunk_count + 32 |
| 32 | 8 | nonce |
| 40 | 2 | key_len n (1..64) |
| 42 | n | map_key, ASCII `[a-z0-9_]` (the client refuses a container whose key differs from the map being served) |
| .. | pad | zero bytes up to `table_offset` = (42 + n + 15) & ~15 |
| table_offset | 32 * chunk_count | SHA-256 of each **ciphertext** chunk (the last chunk is shorter) |
| .. | 32 | HMAC-SHA256(K_mac, every byte before this field: header, key, padding, table) |
| payload_offset | plaintext_length | AES-256-CTR ciphertext (no expansion) |

File length must equal `payload_offset + plaintext_length` (truncation and appended bytes are refused). Overhead for Bowl's 1,006,073,696-byte scenes
bundle: 960 chunks, a 30,720-byte table, about 31 KB in total. The header MAC proves the key (a wrong `K_map` fails here, before any chunk is read);
the table binds the payload, so a flipped ciphertext byte is caught by the chunk hash on first read of that chunk (lazy, 0.7).

**CTR construction.** Counter block `i` = `nonce[8] || uint64_BE(i)`, `i = payload_relative_offset / 16`, keystream block = AES-256-ECB(K_enc, counter
block), plaintext = ciphertext XOR keystream. Standard NIST CTR; a reader decrypts any 16-aligned offset without touching earlier data. Test
vector: SP 800-38A F.5.5 with `nonce = f0..f7`, first counter `f8f9fafbfcfdfeff`.

**Determinism (the build must be repeatable; "stage twice -> identical SHA-256 of all 12 bundles and 4 key files" is a test).**
1. `nonce` = first 8 bytes of `HMAC-SHA256(K_nonce, plaintext)`: a keyed SHA-256 of the whole plaintext (not a bare hash, which would put a plaintext
   fingerprint into every header, D2). The tool therefore reads the source twice (HMAC pass, encrypt pass); two plaintexts share a nonce with probability 2^-64.
2. No timestamps, no random fields, chunk size fixed at 1 MiB, ascending index order, wraps in key-store insertion order, canonical JSON (0.4).
3. `K_map` is created once (random) and then only read; the only random input of the whole feature.
4. The tool writes `<out>.tmp` and renames; an identical existing output is left in place (mtime kept).

### 0.6 Build machine, key store, Arena found how

- Arena is found with `ArenaInstallation.Detect(null)` (registry, ArenaInstallation.cs:145) or `--arena <dir>` = `ArenaInstallation.Verify(dir)` (:149). The tool
  links `ArenaInstallation.cs`, requires `Verified` (stage 1's floors and file check) before it reads a level file, and records the manifest version as `built_for`.
  Registry `DisplayVersion` is not used: it lags the manifest on this VM (0.6.0.0.47752 against 0.6.1.0.48206, arena-install-check.md).
- Key store `work/release-keys/<map_key>.key`: `{schema, map_key, k_map (64 hex), created_utc, wraps: [...]}`; `wraps` is the append-only list the shipped key
  file is written from, so wraps for older Arena builds survive although that build is no longer installed. `stage` finds an equal entry (same files and sizes, same
  `wrapped_key`) -> nothing to add; same sizes but another `wrapped_key` (a hotfix with the same version) -> add; new files or sizes -> add. Per-file SHA-256 of the
  level files is also recorded in the key store (never shipped) so the first Arena update shows which files changed (0.8, R6).
- Back up `work/release-keys` outside the repo. Losing a key-store entry means no new wrap can be made for the already released bundles of that map (no keys update);
  recovery is a rotation: new `K_map`, re-encrypt from the plaintext in `work/build`, re-release the bundles (D9).

### 0.7 Client behaviour

- **Detect by magic.** `AmpbBundleOpener.Open(path, map, servedKey)` reads 4 bytes: `AMPB` -> key + `AmpbReadStream` -> `AssetBundle.LoadFromStream(stream, 0, 1 << 20)`
  (1 MiB `managedReadBufferSize`, a multiple of the chunk size, D13); anything else -> `AssetBundle.LoadFromFile` as today (plaintext development bundles,
  Debug line, no gate, D8; the release validator forbids shipping one). The only `LoadFromFile` calls left are inside the opener (a source test asserts it).
- **Key resolution** once per map and session, lazily on the first request (BundleHost.TryServe, line 79; NavMeshInstaller.LoadOnce, line 123), on the calling
  thread, under a per-map lock, success **and** failure cached. Order: `ArenaInstallationGate.EnsureVerified()` (stage 1, unchanged, ArenaInstallationGate.cs:278),
  then `ArenaInstallationGate.Result.InstallPath` / `.ManifestVersion` (:81; no second registry probe), then the key file, then 0.8's selection. The worker thread
  of the gate never logs and neither does any crypto code: every line below comes from the game thread (the property `ClientArenaGateTests` pins for the gate).
- **Stream** = `AmpbReadStream : Stream` (CanRead, CanSeek, not CanWrite, `Length` = plaintext length). One `lock` guards position, cache and the CNG key handle,
  because Unity reads from its loading thread while the game thread may call the same stream. Cache: `min(chunks, 16)` slots of one chunk, LRU, allocated lazily and
  reused (no per-read LOH allocation); a `verified` bit per chunk survives eviction (a chunk is hashed once, decrypted again if re-read). Read path: chunk miss ->
  read ciphertext chunk, SHA-256, compare with the table (`AmpbException` kind `ChunkIntegrity`), AES-ECB the counter blocks, XOR (`unsafe` `ulong*` loop,
  `AllowUnsafeBlocks` in every project that links `ArenaMapPort.Crypto`, no `Span` dependency on Mono) -> slot. Counters (`BytesDecrypted`, `ChunksVerified`, hits,
  misses, crypto milliseconds) feed the Debug lines. Streams stay open for the process (Unity keeps them for the bundle's lifetime and never closes them; 12 handles at most);
  the NavMesh bundle's stream is disposed after `Unload(false)` (NavMeshInstaller.cs:182).
- **Crypto provider.** `CryptoProviders.Select()` once per process: Windows CNG through P/Invoke `bcrypt.dll` (`BCryptOpenAlgorithmProvider` "AES"/"SHA256" with flag
  0x8 for HMAC, `BCryptSetProperty` ChainingModeECB, `BCryptGenerateSymmetricKey`, `BCryptEncrypt` ECB over a counter-block buffer, `BCryptHash` one-shot,
  `BCryptCreateHash/HashData/FinishHash` for the level files; `IntPtr` buffers pinned with `GCHandle` so offsets work without `unsafe`; NTSTATUS `>= 0` is success)
  with a **known-answer self-test** on first use (SHA-256 "abc", RFC 4231 case 1, FIPS-197 C.3): wrong output without an exception is the typical failure of a
  marshalling mistake, so a mismatch or any exception selects the managed provider (`System.Security.Cryptography` Aes ECB/SHA256/HMACSHA256), once, with one Warning.
  `BCryptHash` needs Windows 10; Wine/Proton, older Windows and any failure take the managed path. Throughput **(inferred)**: AES-NI ECB >= 2 GB/s, CNG SHA-256 >= 400 MB/s,
  XOR >= 1 GB/s -> about 250-400 MB/s per decrypted chunk stream; managed Mono 30-60 MB/s (the brief's figures), which is why the fallback warns. A Debug-only
  8 MiB self-benchmark (behind `DeveloperOptions.Enabled`, AGENTS release rules) prints the real Mono numbers on the host.

### 0.8 Arena updates, and what the player sees

> 2026-10-20: the first Arena patch after the plan did exactly what this section expected, on every map: 0.6.0 to 0.6.1 rewrote all 28
> key-source level files. Section 6 moves the keys to files that patch left alone; the maintainer flow below is unchanged for the cases
> section 6 lists as not survived.

Level files change when BSG edits a map: the 0.6.0 audit found Bay5's main scene changed against 0.5.4 (three more decal/render objects, new mesh data;
arena-update-20261003.md), so the first Arena patch after release will likely break at least one map's key until the keys are updated.

- **Maintainer flow.** Install the new Arena through the launcher, run the staging build: `keys` adds one wrap per map for the new files (same `K_map`, so the bundles are
  byte-identical and nothing is re-encrypted) and only `keys/*.json` change (about 1-3 KB each). Ship them as a keys update (a ZIP of the `keys` folder; `make_release_zips`
  `--keys-only`, TOOL-9) or in the next release. If level indices shifted (seven generic plans shifted in 0.6.0), `keys` fails with "level N missing" and
  `tools/bundle/resolve_key_sources.py` (TOOL-9) regenerates the table from the installed `BuildSettings`.
- **Selection.** Wraps are tried in file order; a wrap applies when all its files exist with the listed sizes; the first applicable wrap whose `check` verifies wins. Nothing
  applicable -> the cause is reported against the **last** wrap (the newest build). Applicable but no `check` -> E5.
- **Meanwhile the player gets** E4 once (below), the maps stay listed (the server does not know; D7) and a raid on the affected map fails to load its bundle. Maps whose
  files did not change keep working, because keys are per map and isolated.

### 0.9 Log lines (client; tag `[Bundles]`, `[NavMesh]` for the mesh; all from the game thread)

| Id | Level | Text (template) |
|---|---|---|
| E1 | Error | `[Bundles] key for '<map>' unavailable: key file '<path>' is missing; reinstall ArenaMapPort (the keys folder belongs to the release)` |
| E2 | Error | `... key file '<path>' is not valid (<reason>); reinstall ArenaMapPort` |
| E3 | Error | `... Arena file '<rel path>' cannot be read in <install dir> (<reason>)` |
| E4 | Error | `... the maps were built for Arena <built_for> but <installed> is installed ('<rel path>' is <n> bytes there, <m> expected); a keys update for this Arena build is needed` (equal versions: `... differs in size from the key file (<n> against <m> bytes, Arena <v>)`) |
| E5 | Error | `... the Arena files have the listed sizes but do not unlock the key (another build with equal sizes, or modified files)` |
| E6 | Error | `... key derivation failed (<Exception type>: <message>)` |
| E7 | Error | `[Bundles] '<served key>' not served: '<file>' is not a valid map bundle (<bad version / header authentication failed (not the bundle set of this key file) / truncated>)` |
| E8 | Error | `[Bundles] '<file>' failed its integrity check at chunk <i> (offset <o>); the file is damaged, reinstall ArenaMapPort` (once per stream, from the exception the read raised) |
| E9 | Error | `[Bundles] '<served key>' not served: no key for '<map>' (see the line above)`; the NavMesh twin reads `[NavMesh] navmesh bundle for '<map>' not loaded: no key (see ...)` |
| W1 | Warning | `[Bundles] Windows CNG unavailable (<reason>); using managed crypto, which is slow: map loading will take noticeably longer` |
| D1 | Debug | `[Bundles] key for '<map>' derived in <ms> ms (<n> files, <x> MB hashed, built for Arena <v>, crypto: CNG)` |
| D2 | Debug | `[Bundles] loaded '<served key>' from '<path>' (<bytes> bytes plaintext, encrypted AMPB v1, <n> chunks)` (plaintext: `... plaintext development bundle ...`) |
| D3 | Debug | `[Bundles] stream '<file>' (<raid start / scene unload>): <x> MiB decrypted in <c> chunks, cache hits <h> / misses <m>, crypto <ms> ms` |

There is no new Info line: a healthy session logs nothing new above Debug (AGENTS Info rule), failures are Errors (actionable player information).

## 1. Work items

Four areas work in the same tree at the same time; **each file has one owner**. Order: CORE first (its first deliverable, in hours not days, is the API stub below plus the
known-answer tests, so TOOL and CLIENT code against real signatures), TOOL and CLIENT in parallel, DOCS last. **Milestone M1** is the first encrypted Bowl raid on GPU-2
(TOOL-1 `encrypt`/`keys`/`stage`, TOOL-3, CLIENT-1 to CLIENT-4): it answers R2 (scene bundle through `LoadFromStream`) and R3 (amplification) before validators, packer and texts are polished. dotnet runs are sequential per project (obj/ corruption).

### 1.0 API contract (frozen by CORE; namespace `ArenaMapPort.Crypto`, netstandard2.1, `#nullable disable`, no `Span`, no logging)

```csharp
public interface ICryptoProvider { string Name {get;} byte[] Sha256(byte[] d,int o,int n); byte[] HmacSha256(byte[] key,byte[] d,int o,int n);
  IAesKey NewAes(byte[] key32); ISha256Stream NewSha256(); }   // EncryptEcb: count is a multiple of 16
public static class CryptoProviders { public static ICryptoProvider Select(Action<string> warn); public static ICryptoProvider Managed {get;} public static ICryptoProvider Cng {get;} /* null when unavailable */ }
public interface ISha256Stream : IDisposable { void Update(byte[] d,int o,int n); byte[] Finish(); }            // level files, 1 MiB at a time
public interface IAesKey : IDisposable { void EncryptEcb(byte[] src,int so,byte[] dst,int dso,int count); }     // one CNG key handle, reused
public static class Hkdf { Extract(p,salt,ikm); Expand(p,prk,info,length); }
public static class AesCtr { public static void Transform(IAesKey k, byte[] nonce8, ulong firstBlock, byte[] src,int so,byte[] dst,int dso,int count); }  // the XOR loop lives here
public static class Ampb { public static void Encrypt(Stream plain, Stream dest, string mapKey, byte[] kMap, ICryptoProvider p, int chunkSize = 1 << 20);
  public static AmpbHeader ReadHeader(Stream s, byte[] kMap, ICryptoProvider p); }        // MAC-verified; AmpbException on any defect
public sealed class AmpbReadStream : Stream { AmpbReadStream(Stream src, string expectedMapKey, byte[] kMap, ICryptoProvider p, int cacheSlots = 16, bool leaveOpen = false);
  public AmpbStats Stats {get;} }
public sealed class AmpbException : IOException { public AmpbError Kind {get;} public int Chunk {get;} }   // BadMagic BadVersion HeaderAuth Truncated WrongMap ChunkIntegrity
public sealed class KeyFile { Parse(string json); string ToCanonicalJson(); IList<KeyWrap> Wraps; }      // KeyWrap: BuiltFor, Files, Salt, WrappedKey, Check
public static class KeyResolver { // pure, injectable file access: IArenaFiles { long Length(path); Stream Open(path); }
  public static KeyResolution Resolve(KeyFile f, string installDir, string installedVersion, IArenaFiles fs, ICryptoProvider p);
  public static KeyWrap Wrap(string mapKey, byte[] kMap, string builtFor, IList<KeyFileEntry> files, byte[][] fileHashes, ICryptoProvider p); }
// KeyResolution: bool Ok; byte[] MapKey; KeyFailure Cause (None, KeyFileMissing, KeyFileInvalid, ArenaFileUnreadable, SizeDiffers, ContentDiffers, Internal); string Detail; string BuiltFor; int Ms
```

### 1.1 CORE — `ArenaMapPort.Crypto/*.cs` + tests

Files: `ArenaMapPort.Crypto/{CryptoProvider,CngProvider,ManagedProvider,Hkdf,AesCtr,AmpbFormat,AmpbWriter,AmpbReadStream,KeyFile,KeyResolver,ConstantTime}.cs`;
`ArenaMapPort.Server.Tests/{CryptoPrimitivesTests,AmpbContainerTests,AmpbReadStreamTests,KeyDerivationTests,KeyFileTests}.cs`; `ArenaMapPort.Server.Tests.csproj`
(link rows for the Crypto folder, `AllowUnsafeBlocks`; CLIENT hands its own link rows over as a handoff edit). Every class carries the repo's doc comment (what, why, which test proves it).

Acceptance: all tests green on **both** providers (Theory over `CryptoProviders.Managed` and `.Cng`); no Unity/BepInEx/Shared reference; `dotnet build -c Release` 0 warnings
in the three consuming projects. Tests:
1. Known answers: SHA-256 (empty, "abc", 1 M x "a"), HMAC RFC 4231 cases 1, 2, 6, HKDF RFC 5869 cases 1-3, AES-256 FIPS-197 C.3, CTR SP 800-38A F.5.5; CNG == Managed == `System.Security.Cryptography` on 100 random buffers (sizes 0..5 MiB, odd offsets).
2. Container: round trip at plaintext sizes 1, 15, 16, 17, chunk-1, chunk, chunk+1, 3 chunks + 5 (chunk 4 KiB and the real 1 MiB); empty plaintext refused; a **golden** container
   (fixed `K_map`, fixed plaintext) pinned byte for byte (SHA-256 of the file) so the format cannot drift silently; determinism (twice identical; one plaintext byte changed -> other nonce);
   every one of the first 256 bytes flipped one at a time -> `HeaderAuth`/`BadVersion`/..., never an exception of another type; table byte, MAC byte, payload byte flipped; truncated by 1 / by a chunk; one trailing byte;
   wrong version, flags, chunk size, key_len, header map key against the expected one; wrong `K_map` -> `HeaderAuth` before any payload read.
3. Stream: Length/Position/Seek (all origins, past end), reads across chunk boundaries and at the end (0), 2,000 seeded random (offset, length) reads against the plaintext, cache with 2 slots (eviction), lazy
   verification (a counting inner stream: each chunk hashed once, still once after eviction), a corrupt chunk fails only reads that touch it, `Write`/`SetLength` throw, 8 threads x 500 random reads exact, `leaveOpen`.
4. Keys: HKDF `info` pinned; wrap/unwrap round trip; deterministic salt; one-bit IKM change -> `ContentDiffers`; file order (swapping two files changes the key); missing file -> `ArenaFileUnreadable`; size
   difference -> `SizeDiffers` (both the "other build" and the "same version" wording inputs); several wraps (second applies, first does not); caps (17 wraps, 33 files, 65 KiB refused).
5. Key file: canonical writer pinned by a golden string; parser round trip; BOM, escapes, unknown keys ignored, duplicate keys refused, wrong types, schema 2, bad hex lengths, `KeyPath.IsSafe` against 40 paths and equality with `ArenaInstallation.IsPlainRelativePath`.
6. Real data (skipped without Arena or `work/release-keys`, like `ArenaInstallationTests`): all four maps' IKM derived from this VM's Arena, `Resolve` against the staged `dist` key files returns the key-store `K_map`.

### 1.2 TOOL — console tool, staging, key store, validators, packer

Files: `tools/bundle/ArenaMapPort.BundleCrypt/{ArenaMapPort.BundleCrypt.csproj,Program.cs,Commands.cs,KeyStore.cs}` (net10.0 Exe, links `ArenaMapPort.Crypto/*.cs` and `Shared/ArenaInstallation.cs`, no packages);
`tools/bundle/key-sources.json`; `tools/bundle/bundle_crypt.py` (wrapper); `ArenaMapPort.Client/ArenaMapPort.Client.csproj` (**TOOL is its only owner**: link rows next to the Shared rows at lines 39-45, `AllowUnsafeBlocks`, the staging change);
`tools/validate/{validate_bundle_names,validate_distribution,validate_port}.py`; `tools/release/make_release_zips.py`; `tools/park-unreleased-dist.ps1`; `tools/tests/{test_bundle_crypt,test_bundle_key_sources,test_validate_distribution,test_release_zips,test_park_unreleased_dist}.py`.

- **TOOL-1 commands** (exit 0 ok, 1 failure with one `FAIL <reason>` line, 2 usage; success prints one `OK ...` line; data goes to files or stdout bytes only for `read-range`):
  `encrypt --map K --in P --out E --key-store D [--create-key]`; `decrypt --in E --out P (--key-store D | --keys-file F [--arena DIR|auto])`;
  `inspect --in E [--key-store D] [--verify-chunks] [--json]` (no key: header facts; with key: MAC; `--verify-chunks`: every table hash);
  `read-range --in E --offset N --length L --out FILE|-` (negative `N` counts from the plaintext end; needs a key source: `--key-store D` or `--keys-file F [--arena DIR|auto]`, as `decrypt`);
  `plain-sha256 --in E` (streams decrypt + hash, for the spike check); `keys --map K --arena DIR|auto --key-store D --levels tools/bundle/key-sources.json --out F` (adds the wrap 0.6, writes the key file);
  `verify-keys --map K --keys-file F --arena DIR|auto --key-store D` (a player's derivation must return the store's `K_map`: the full chain);
  `verify --map K --plain P --enc E --key-store D` (re-encrypt and compare byte for byte, for `validate_port --distribution`);
  `stage --map K --bundle P1 --bundle P2 --bundle P3 --out-dir <plugin dir> --key-store D --arena auto` (= 3 x encrypt + keys, writes `bundles/` and `keys/<K>.json`);
  `bench --provider cng|managed|both` (MB/s for the three primitives; the number the budget in 2.4 rests on). Key material is never printed. Test seam: `--arena-unverified DIR` skips the stage-1 verification (a fake Arena folder cannot pass the floors),
  refused unless the environment variable `ARENAMAPPORT_TOOL_TEST=1` is set; the staging target and the validators never pass it.
- **TOOL-2 key store** `KeyStore.cs`: 0.6; `--create-key` only on first stage, printing "CREATED ... back it up"; `--rotate` prints what it invalidates and needs `--yes`.
- **TOOL-3 staging** (client csproj, target `ArenaMapPortStageWpOutputs`, line 290): a new target `ArenaMapPortBuildBundleCrypt` (runs `dotnet build tools/bundle/ArenaMapPort.BundleCrypt -c Release -nologo`; idempotent, own obj/) before it;
  the `<Copy SourceFiles="@(ArenaMapPortBundles)"...>` at line 349 is replaced by four `Exec` `stage` calls (one per map; the 12 `ArenaMapPortBundles` items and their `Exists` Error rows at 309-335 stay as the plaintext inputs).
  `ArenaMapPortDllOnly=true` still skips all of it. Cost: about 3.2 GB read twice and written once, **(inferred)** 20-40 s per full Release build (the tool streams; no 1 GB buffers).
- **TOOL-4 `tools/bundle/bundle_crypt.py`**: `ensure_built()` (one `dotnet build` per process, never touches `bin/`/`obj/` beyond what dotnet does; env `ARENAMAPPORT_BUNDLECRYPT` overrides for tests), `run(*args)`, `is_encrypted(path)`, `read_range(path, offset, length)` (plain files: `seek`/`read`; AMPB: the tool).
- **TOOL-5 validators.**
  `validate_bundle_names.py`: `read_directory_names()` (line 87) reads through a small `Reader` (`read(offset, n)`, offset < 0 from the end): the CAB-name parse needs exactly two ranges, the first ~100 bytes (UnityFS header, lines 95-107) and the blocks-info blob
  (`start .. start+compressed_size`, or the last `compressed_size` bytes when `BlocksInfoAtTheEnd`, lines 120-126; 43,864 bytes at offset 64 for Bowl's scenes bundle), never the data blocks; the fallback to `UnityPy.load()` (line 150) first decrypts to a temp file under `work/tmp` and removes it; 2 tool calls per bundle.
  `validate_distribution.py`: the exact bundle set stays (line 253); **new**: (a) every dist bundle starts with `AMPB`, `inspect --key-store D --verify-chunks` passes, its header map key matches the file name, the decrypted prefix is `UnityFS` (`read-range 0:8`); (b) `keys/` holds exactly `<key>.json` per release map,
  `verify-keys` passes for each against this machine's Arena (mandatory: the build machine has Arena; the Python test injects a stub); (c) no file under `dist/` anywhere begins with `UnityFS\0` (8 bytes per file); (d) `validate_not_spike_build` (line 35) compares the **decrypted** size and `plain-sha256` instead of the on-disk bytes
  (the spike hashes can no longer match encrypted files: `inspect` plaintext length first, hash only on a length match). `DISTRIBUTION_OK` line gains `encrypted`.
  `validate_port.py --distribution` (lines 122-128): the bundle pairs use `verify` instead of `sha256(source) == sha256(target)` (the encrypted copy cannot equal `work/build`).
  `build_all.ps1` is **unchanged**: patch, roundtrip and preset validators run on plaintext under `work/build` and stay valid; encryption happens only when staging into `dist/`.
- **TOOL-6 packer** (`make_release_zips.py`): rule `add(m.stem, "key file", re.escape(f"{PLUGIN_ROOT}/keys/{m.key}.json"))` beside line 336; `required_files()` (362) and `check_partition()` (1107) / `check_single()` (1138) demand it (a map part without its key file is not installable) and `MAP_DATA_RE` (1134) learns
  it, including the "foreign file" rule; a gate in `partition_dist`: every `bundle`-rule file must start with `AMPB` (4 bytes), else `PackagingError: plaintext bundle in dist`, repeated on the finished ZIPs in `verify_set` (a STORED entry, 4 bytes); `readme_text()` (597) / `REQUIREMENT_TEXT` (582) say that the bundles are encrypted with a key derived from Arena's files and carry the 0.1 limits;
  `decide_file()` (457) needs no change: ciphertext fails the 3 % probe and is STORED (expect the ZIP to grow by +5 to +6 % against the old cut's ZIP, which was about 5.5 % smaller than its plaintext dist because the bundles deflated; estimated 2026-10-19, measured at integration, R7). Fixtures: `test_release_zips.py` bundles become `b"AMPB" + random` (line 81), `test_validate_distribution.py` fixtures (lines 59-114) get a stub verifier.
- **TOOL-7 park script**: add `keys/arena_${stem}.json` to the client list (line 51 pattern; absent files are skipped by `Test-Path`); the deploy script needs **no change** (verified: it copies every file under both source roots by SHA-256 compare, `Get-ChildItem -Recurse -File`, deploy-to-spt.ps1:755-776, so `keys/` travels like `bundles/`).
- **TOOL-8 `key-sources.json` + `test_bundle_key_sources.py`**: the table of 0.3, pinned against the four sources (the test skips when `work/extract` is absent, like the existing optional tests) and, when Arena is present, against the installed level files (existence and size recorded in the table's `arena_build`).
- **TOOL-9 (maintenance, after the first cut)**: `tools/bundle/resolve_key_sources.py --arena DIR [--write]` (UnityPy `BuildSettings`, the `extract_bay5.resolve_scene_indices` pattern with the largest-sharedassets rule) and `make_release_zips.py --keys-only` (a ZIP of `keys/` + its `.sha256`).

Acceptance and tests (`test_bundle_crypt.py` runs the real tool through the wrapper, fixtures under a temp dir inside `work/`): encrypt -> inspect -> read-range (positive, negative, spanning chunks) -> decrypt equals the source for the repo's real small bundles (Equator's 6,891-byte preset, Bowl's 185,840-byte navmesh); `stage` against a **fake Arena** (a temp folder with synthetic level files and `--arena-unverified`) writes 3 bundles + key file and a second run is byte-identical; a second Arena build adds a wrap and leaves the bundles unchanged; `verify-keys` with a wrong size / a flipped level byte fails with the right cause; `validate_bundle_names` returns the same CAB names for a bundle and its encrypted copy; `validate_distribution` rejects a plaintext bundle, a missing/extra key file, a UnityFS file elsewhere in dist;
packer rejects a plaintext bundle and a map part without its key file. Existing `unittest discover` stays green.

### 1.3 CLIENT — loaders, key resolution, logs

Files: `ArenaMapPort.Client/{BundleKeys.cs (new: per-map cache, lock, logging; the resolution logic is `ArenaMapPort.Crypto.KeyResolver`), AmpbBundleOpener.cs (new: magic, key, stream, `LoadFromStream`; the only place that calls `AssetBundle.LoadFromFile`/`LoadFromStream`), BundleHost.cs, NavMeshInstaller.cs}`;
`ArenaMapPort.Server.Tests/{ClientBundleKeysTests,ClientBundleOpenerTests}.cs`. No change to `ArenaInstallationGate.cs`, `Plugin.cs`, the server or the descriptors. Link rows for the test project go to CORE as a handoff edit; the client csproj rows to TOOL.

- **CLIENT-1 `BundleKeys`**: `internal static bool TryGet(string mapKey, out byte[] kMap)`: gate first (`EnsureVerified`), then 0.7; one derivation per map under a per-map lock (8 concurrent callers -> 1 derivation); failure cached with its E-line logged once; later callers log E9 only. File access through `IArenaFiles` (real = `FileStream` with `FileShare.ReadWrite | Delete`, 1 MiB blocks).
- **CLIENT-2 `AmpbBundleOpener.Open(string path, ArenaMapDescriptor map, string servedKey)`**: returns the bundle or null, logs D2/E7, keeps the stream alive in a static list, wires E8 through the stream's error callback.
- **CLIENT-3 `BundleHost.TryServe`**: `OurBundles()` (54-69) also remembers the descriptor per served key; line 108 `AssetBundle.LoadFromFile(filePath)` becomes `AmpbBundleOpener.Open(...)`; the stage-1 gate (89-93), `_bundles.ContainsKey` (94), the `File.Exists` check (99) and the `Counter = 0` bookkeeping (152-153) stay as they are; `SafeFileLength` (159) keeps reporting the on-disk size (D2 also prints the plaintext size).
- **CLIENT-4 `NavMeshInstaller.LoadOnce`**: line 153 becomes the opener; the stage-1 gate (134-139) and `_loadTried` (125, 140) stay; the stream is disposed in the `finally` after `Unload(false)` (182). D3 is logged from `OnBotsControllerInit` (52, raid start) and `OnSceneUnloaded` (99) through `BundleHost.LogStreamStats(mapKey, reason)`.
- **CLIENT-5 dev benchmark** (Debug, only with `DeveloperOptions.Enabled`): 8 MiB encrypt/decrypt/hash timing once per session, so GPU-2's Mono numbers are on record.
Acceptance: `grep -n "LoadFromFile" ArenaMapPort.Client/*.cs` finds only the opener; plaintext development bundles still load; every E-line reachable from a unit test with its exact text; no log call on a non-game thread (the test pattern of `ClientArenaGateTests`, `Plugin.Log.Errors`).
Tests: key resolution per failure cause with an in-memory `IArenaFiles` (E1-E6 texts, the older-wrap-applies case, the "built for X, Y installed" wording); concurrency (1 derivation for 8 threads); gate not verified -> no file is read and no key is derived; E9 for the later bundles of a failed map; plaintext path; opener on a real encrypted tiny bundle with a counting stream; source-level guard (no `LoadFromFile` outside the opener, `LoadFromStream` always with the read-buffer argument).

### 1.4 DOCS — texts

Files: `README.md` (Compatibility paragraph: bundles encrypted, key derived from your Arena files, what it is not), `FORGE.md`, `docs/releases/v0.3.0.md` (new section, download size, the keys-update note), `AGENTS.md` (Hard rules: `work/release-keys` is secret, never in git, back it up; the build/test commands; a Traps bullet on Mono crypto and `LoadFromStream`; "Bundles are distributed as built" becomes "distributed encrypted"),
`HANDOFF.md` (an update block), new `docs/progress/bundle-encryption.md` (log with the evidence of 2.4), `docs/progress/arena-install-check.md` (appended pointer: stage 2), `docs/dist-manifest.md` (appended: `keys/`, encrypted bundles, new validator checks), `docs/progress/INDEX.md` (one entry now for this plan, a second when the log exists). `THIRD-PARTY-NOTICES.md`: no entry (own implementation, RFC/NIST vectors are public). Wording rule: "not copy protection" in every text. Acceptance (no code, so a checklist recorded in the progress log): README, FORGE, release notes and the generated package README (TOOL-6) say the same three things (encrypted with a key derived from your Arena files; not copy protection; art and audio stay plaintext);
`git grep -n "as built\|plaintext bundle"` leaves no stale statement; AGENTS, HANDOFF, INDEX and the progress log agree on the commands and on what is verified; no level-file hash appears in any tracked or shipped file (paths and sizes only).

## 2. Integration and host plan

### 2.1 Build and validate (this VM; Arena installed; sequential)

1. CORE: `dotnet test ArenaMapPort.Server.Tests -c Release` (Crypto tests included). 2. TOOL: `dotnet build tools/bundle/ArenaMapPort.BundleCrypt -c Release`; `python -m unittest discover -s tools/tests -p "test_*.py"`.
3. Full client Release build without `DllOnly` (stages, encrypts, writes `keys/`; first run prints `CREATED` for the four key-store entries: **back up `work/release-keys` now**). 4. `python tools/validate/validate_distribution.py` -> `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars encrypted` and `bench` numbers recorded.
5. Second full build: SHA-256 of all 12 bundles and 4 key files identical to the first (determinism). 6. `make_release_zips.py --plan --single`: size estimate against 3,078,043,845 B (the 0.3.0 candidate cut of 2026-10-18, dist 3,256,514,833 B) and 2,149,219,632 B (0.2.3). Server DLL is not touched by stage 2; hash it anyway before the host restart (AGENTS).

### 2.2 Deploy

`pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41'` (dry run: expect 12 bundles overwritten, 4 key files new, the client DLL overwritten, the rest identical), then `-Confirm`. **Close EFT before the first encrypted deploy**: an old DLL meeting an `AMPB` file fails with its own `LoadFromFile returned null` Error, and the new DLL meeting a half-synced mix of plaintext and encrypted files fails with E7 or E1. Syncthing moves about 3.2 GB; wait for the sync, then check the loaded-plugin MVID in `uibridge_status` after the restart.

### 2.3 Baseline (before the deploy)

Three raids per map (Bowl, AirPit) on the **current plaintext build**, NO BOTS, record `uibridge_wait raidStarted=true` wall time (first one after an EFT restart = cold, then two warm), `uibridge_status` `max` frame time, and the `[NavMesh]`/`[Bundles]` lines. Without a baseline the budget is unfalsifiable.

### 2.4 GPU-2 run (host-verified = accepted)

`gpu-run restart --host host2`, then Bowl and Air Pit raids (NO BOTS for timing), a cross-map sequence (Bowl -> menu -> Air Pit -> Equator in one session), one hideout open (AGENTS acceptance), one WITH BOTS raid per new map for the NavMesh path. Expected Debug lines per first request of a map (`uibridge_log` source `maschine-ArenaMapPort`, filter `[Bundles]`):
`key for 'arena_bowl' derived in <= 500 ms (7 files, 41.1 MB hashed, built for Arena 0.6.1.0.48206, crypto: CNG)` then `loaded 'maps/arena_bowl_preset.bundle' ... encrypted AMPB v1, 1 chunks`, `loaded 'arena_bowl/arena_bowl_scenes.bundle' ... 1006073696 bytes plaintext ... 960 chunks`, at raid start `stream 'arena_bowl_scenes.bundle' (raid start): <x> MiB decrypted in <c> chunks, cache hits ... crypto <ms> ms`
and the unchanged `[NavMesh] added navmesh for 'arena_bowl': 12704 vertices, 5890 triangles` (identical counts prove the same data came out), acoustics 11/19, flares 4/28 (HANDOFF 2026-10-19), 0 ArenaMapPort warnings or errors.
**Budget**: raid start to `raidStarted` at most the baseline + 3 s (cold) and + 2 s (warm) per map; key derivation at most 0.5 s per map; `max` frame time unchanged except one frame on the loading screen at most 0.6 s; decrypted bytes per scenes bundle at most 1.5 x its size (amplification, R3); no new `[Bundles]`/`[NavMesh]` Warning or Error. If the budget fails, the stream stats name the cause (crypto ms against cache misses).
Evidence goes into `docs/progress/bundle-encryption.md` with date, time, build id (`uibridge_status` MVID).

### 2.5 Negative tests (a second Arena is not available, so the wrong-key case is built differently)

All tampered copies are made by script under `work/negative/` (never the 1 GB bundle). For a host run the copy temporarily replaces its twin in `dist/` (build output, not an install); the only write path to `D:\SPT41` stays the deploy script. A failed derivation is cached for the session, so every negative step and the restore need an EFT restart. Afterwards a full staging build restores `dist/` and the deploy dry run must report `0 to overwrite`.
- **N1 wrong size** (host): a Bowl key file whose first `size` is +1 -> E4 for Bowl at the first Bowl request; the raid does not load; an Air Pit raid in the same session still works (per-map isolation).
- **N2 content mismatch** (host): same file with one `check` hex digit changed -> E5 (right sizes, key does not verify).
- **N3 tamper** (host and unit test): a copy of `arena_bowl_navmesh.bundle` with one byte flipped inside chunk 0 -> `[NavMesh] ... failed its integrity check at chunk 0` and no NavMesh; the same with a header byte (E7, header authentication). The unit-level twin flips a byte of a copy under `work/` in a real container and expects `ChunkIntegrity` for the touched chunk only.
- E1 (missing key file) and E2 (corrupt key file) are unit-level only: the deploy script never deletes, so the host cannot reproduce them honestly.

### 2.6 GPU-1 (no Arena; used only for this, as in 0.2.3)

Stage 1 stops everything first: the log shows the stage-1 not-found lines, `Maps: none available`, and **no** `[Bundles] key`, E-line or `crypto:` line, no read of `keys/`, no `bcrypt.dll` use; the hideout opens, a vanilla raid starts, no new Error or Warning. Record how the build reached that host in the log.

## 3. Risks

| # | Risk | Mitigation / test |
|---|---|---|
| R1 | Mono P/Invoke marshalling (handle width, `byte[]` pinning, wrong output without an exception) | known-answer self-test before first use, fallback + W1; IntPtr/GCHandle signatures; GPU-2 D1 line says `crypto: CNG`. |
| R2 | `AssetBundle.LoadFromStream` with a **scene** bundle on a ~1 GB LZ4HC bundle (compression flag 3, blocks info at the start; Bowl header `dataflags 0x243`) has never been exercised here; thread semantics (reads from Unity's loading thread, the stream is kept for the bundle's lifetime, no close) | `lock` + seekable + exact `Length`; **first host check before DOCS**: a Bowl raid. Plan B only with the user's decision (D11): decrypt to a temp file under `%TEMP%`, `LoadFromFile`, delete at exit; it weakens the claim (plaintext on disk) and costs ~1 GB and 2-3 s. |
| R3 | Random access amplification: a 1 MiB chunk decrypted for a 64 KB read; cache of 16 slots | the D3 line counts it; budget 1.5 x; raise `cacheSlots` (config-free constant) if exceeded. |
| R4 | Managed fallback on Mono is 5-10x slower (20 s+ for a full bundle) | W1 says so; wrong-answer risk is covered by R1; Wine/Proton players land here. |
| R5 | CNG availability / `BCryptHash` needs Windows 10 | exception -> managed, one Warning. |
| R6 | First Arena patch after release changes a level file of a map | 0.8 flow; per-file hashes in the key store tell which files churn; the key schema allows a later wrap to list **fewer** files with no client change. |
| R7 | ZIP size: ciphertext is incompressible; 0.2.3's ZIP (2,149,219,632 B) is smaller than its dist | measured in the first cut (2.1 step 6); Pixeldrain has no size cap, GitHub's 2 GiB cap is already out. |
| R8 | Chunk table size | 960 x 32 = 30,720 B for 1 GB, read once at open; not a risk. |
| R9 | Startup: key derivation on the game thread at the loading screen | 22-41 MB hashed per map (<= 70 ms with CNG, <= 0.4 s managed **(inferred)**); budget 0.5 s; if the frame time check fails, prefetch from the gate's completion (D5). |
| R10 | `K_map` loss or leak | backup of `work/release-keys`; rotation procedure (D9). |
| R11 | A mid-sync state on the host (Syncthing) | EFT closed during the first deploy (2.2); `DllOnly` unaffected. |
| R12 | Players who edit their Arena (modified level files) | E5; not our problem, stated. |

## 4. Decisions beyond the brief, and the open ones

Taken here (refinements, not reopenings): **D1** container subkeys `K_enc/K_mac/K_nonce` derived from `K_map` instead of one key for CTR and HMAC. **D2** nonce = keyed HMAC-SHA256 of the plaintext (first 8 bytes), counter block `nonce || BE64(i)`.
**D3** wrap = AES-256-CTR + HMAC with HKDF-derived keys (no AES-KW), deterministic salt, `wraps` array. **D4** level files in ascending numeric index order, the key file's list is authoritative. **D6** the level table is a tracked `key-sources.json` because two accepted manifests lack `scene_registry`. **D8** plaintext bundles
still load (development convenience, no switch, validator forbids shipping them). **D12** the tool is not shipped in the release ZIP. **D13** `managedReadBufferSize` 1 MiB. **D14** `unsafe` XOR loop, `AllowUnsafeBlocks` in the three consuming projects.

Open, for the user (recommendation first): **D5** resolve keys lazily on first use (chosen) or prefetch when the stage-1 check completes; revisit only if GPU-2 shows a hitch above 0.6 s.
**D7** the server does not check keys, so after an Arena update a map stays listed and fails at load with E4 (loud, like stage 1's no-bundle case); recommended later increment (about one work item, the Crypto folder links into the net10 server): the server resolves the same key and does not register a map whose key is unavailable, with a log line. **D9** key-store loss/leak policy and who backs it up.
**D10** whether the keys update ships as a separate small ZIP (TOOL-9) or only inside full releases. **D11** Plan B for R2 (decrypted temp file) is acceptable or not. **D15** the plain-text art/audio of 0.1 stays as is, or stage 3 is planned.

## 5. As built (integration, 2026-10-19)

The plan above is kept as written; where the code differs, the code is right and this section is the map. Evidence:
[progress/bundle-encryption.md](progress/bundle-encryption.md), "Integration".

| Plan text | As built |
|---|---|
| 1.1 CORE tests in five files (`CryptoPrimitivesTests`, `AmpbContainerTests`, ...) | one file, `BundleCryptoTests.cs` (234 tests, run on both providers); the client's tests are `BundleKeyResolverTests.cs` |
| 1.3 `BundleKeys.cs` (cache, lock, logging) and `AmpbBundleOpener.cs` (magic, key, stream, the only caller of `LoadFromFile`/`LoadFromStream`); tests `ClientBundleKeysTests`, `ClientBundleOpenerTests` | `BundleKeyResolver.cs` (per-map resolution with a lock, `PreparedBundle`, `BundleStreamRegistry`) and `BundleHost.LoadAssetBundle`, the one loader behind `BundleHost.TryServe` and `NavMeshInstaller.LoadOnce`; `AssetBundle.LoadFromFile` is still called there, only for a bundle without the AMPB magic |
| 0.9, 2.4, 2.6 key lines under `[Bundles]` (`[Bundles] key for ...`) | key resolution lines (D1, E1 to E6) carry `[BundleKeys]`; bundle lines (D2, D3, E7, E9) keep `[Bundles]` and `[NavMesh]` |
| 1.3 CLIENT-2/4: the stream is disposed when the scene unloads (NavMesh: in the `finally`) | the NavMesh stream is closed right after `Unload(false)`; a scene or preset bundle's stream is closed once EFT's own bundle table no longer holds the bundle, because EFT keeps a loaded map bundle cached for the next raid (closing on scene unload would break the second raid) |
| 1.2 TOOL-1 flag `--map`; `stage ... --arena auto` with `--create-key` "only on first stage" | the tool's own usage text: `--map-key` (`--map` is an alias), `--key-store`, `--levels FILE` or `--manifest FILE`, `--arena DIR|auto`; extra commands `rotate-key --yes` and `bench`; the staging target passes `--create-key` only with `-p:ArenaMapPortCreateReleaseKeys=true` (integration review: the first build had it on by default, so a lost key store became a new key without a warning) |
| 1.2 TOOL-6 ZIP growth "0-4 %" | measured by `make_release_zips.py --plan --single` on the 2026-10-19 dist: 3,242,902,036 B, +5.4 % over the 2026-10-18 plaintext cut (3,078,043,845 B) |
| 1.2 TOOL-7 add `keys/arena_${stem}.json` to the park script | no change: staging writes key files only for the four released maps, and the script moves exact parked-map outputs by name (`PARKED_DIST_OK Client 0 files` in every build) |
| 1.2 TOOL-5 `validate_port.py --distribution` uses `verify` | done at integration (it passed for Bowl); the other three maps' packages fail its earlier source-plan checks for reasons older than stage 2 |
| 1.2 TOOL-8 `test_bundle_key_sources.py` | `KeySourcesTests` inside `test_bundle_crypt_tool.py`; `tools/bundle/key-sources.json` carries `arena_build` |
| 2.1 step 4 `bench` | CNG and managed on .NET 10 on the build VM are within noise of each other (both OS-backed); the reason for CNG is Mono's slow managed crypto, which only the GPU-2 run can measure (open) |

## 6. Key source v2: sampled resource streams (2026-10-20)

Decision of the integrating session after a user report (the user's PC on Arena 0.6.0.0.47752 could not open Air Pit, E5 line
`... have the listed sizes but do not unlock the key ...`); record and evidence in
[progress/bundle-encryption.md](progress/bundle-encryption.md), "Key source v2". 0.3.0 was not released, so the key-file schema could change
without a migration for players.

**Finding.** Arena's 0.6.0 to 0.6.1 hotfix rewrote 381 of 388 `level<N>` and 381 of 388 `sharedassets<N>.assets` files with unchanged sizes
(the serialized scenes and assets), so all 28 level files of the four maps differ and every level-file key locked. It left 129 of 130
`sharedassets<N>.assets.resS` and all 40 `sharedassets<N>.resource` byte-identical (the raw texture, mesh and audio data the serialized files
point into), including all 26 that belong to the four maps' scenes. The resource streams are also what a map's look is made of, so they are
a fair stand-in for "the map's own files".

**Key source.** Per scene index of a map (the indices of 0.3): `EscapeFromTarkovArena_Data/sharedassets<index>.assets.resS` and
`sharedassets<index>.resource`, those that exist in the build, in ascending index order, `.assets.resS` first. `tools/bundle/key-sources.json`
schema 2 lists them per scene (`{index, scene, files}`) next to the sample parameters; `KeySourcesTests` pins the scenes against the
extractor tables, plans and manifests as before and the files (exactly the existing streams per scene) and their total sizes against the
installed Arena. Equator 3 files (218.0 MB), Bay5 8 (358.2 MB), Air Pit 7 (603.5 MB), Bowl 8 (754.5 MB) on 0.6.1.0.48206.

**Sample.** Hashing 0.2 to 0.75 GB per map at the loading screen is too slow on a hard disk or with the managed fallback, so each file
contributes a deterministic sample: `blocks` blocks of `block_size` bytes, block k at `floor(k * (size - block_size) / (blocks - 1))` for
k = 0 .. blocks - 1 (offsets depend only on the size; the first block starts at 0, the last ends at the end of the file, blocks never
overlap), or the whole file when `size <= blocks * block_size`. Shipped: 64 KiB x 16 = at most 1 MiB per file, 3.1 to 7.4 MB per map,
at most 16 positioned reads per file. Rules (key file and table): block_size a multiple of 4 KiB from 4 KiB to 1 MiB, 2 to 1024 blocks, at
most 64 MiB per file. The parameters are stored per wrap, so a later wrap can choose others without a client change.

**Derivation (labels `v2`).** Per file `digest = SHA-256("ArenaMapPort file sample v2" 00 path 00 size 00 block_size 00 blocks 00 || sample)`
(path in key-file spelling, numbers decimal ASCII: path, size and sample are bound into the key itself, not only into the salt); IKM = the
digests in key-file order; `info = "ArenaMapPort bundle key v2 " + map_key`; `salt = HMAC(K_map, "ArenaMapPort wrap salt v2" 00 map_key 00
built_for 00 block_size 00 blocks 00 (path 00 size 00)*)`; `check = HMAC(KEK_mac, "ArenaMapPort wrap check v2" 00 map_key 00 wrapped_key)`;
HKDF, AES-256-CTR wrap and the check-before-decrypt order as in 0.2. The container subkeys of `K_map` (0.2, 0.5) are untouched, so the
bundles are byte-identical. Golden vectors from independent implementations (Python `hashlib` for the digest, PowerShell on
`System.Security.Cryptography` for the wrap) pin it in `KeyDerivationTests`.

**Key file schema 2.** `{schema: 2, map_key, wraps: [{built_for, sample: {block_size, blocks}, files: [{path, size}], salt, wrapped_key,
check}]}`, 1 to 32 files per wrap, canonical bytes as in 0.4. The client refuses schema 1: no released package carried a key file (0.2.3
ships plaintext bundles), the only schema-1 files were development deploys, and a second derivation would only ever unlock Arena 0.6.1,
which the v2 wraps cover. The key store keeps its schema-1 wraps verbatim as `"scheme": "level-files-v1"` (append-only, never shipped) and
stores `"sample-v2"` wraps with each file's digest (`sample_sha256`, never shipped).

**Cost.** Measured: 3 to 9 ms of work per map on this VM (`bench --keys-dir`, best of 5), 3 to 55 ms for a first run here (the files were in the
OS cache), 60 to 246 ms
over SMB to the user's 0.6.0 install, and 29 to 55 ms per map on GPU-2's first raid of each map (against 45 to 58 ms for the 22 to 41 MB of
level files). On a hard disk each positioned read costs a seek: up to about 113 reads per map, an estimated 1 to 1.5 s once per map and
session **(inferred, not measured: no test PC has Arena on a hard disk)**.

**Limits.** It survives a hotfix that rebuilds the serialized files but keeps the raw streams (0.6.0 to 0.6.1) and changes between the sampled
blocks. It does not survive a Unity upgrade of Arena (expected to re-serialize the raw streams), re-imported textures, meshes or audio of a
map's scenes, shifted scene indices (as 0.5.4 to 0.6.0 did for seven maps), or a scene gaining or losing a raw stream; each needs a staging
build on the new Arena (a new wrap, and for shifted indices a regenerated `key-sources.json`), shipped as a keys update (D10). Before shipping
keys, `verify-keys --arena <folder>` against every Arena build at hand. Not copy protection, as before: a key needs 3 to 7.4 MB of Arena's
files instead of 22 to 41 MB, which changes nothing about sharing a derived key or decrypted bundles.
