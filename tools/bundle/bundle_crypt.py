"""Python access to the ArenaMapPort.BundleCrypt tool (docs/PLAN-ENCRYPTION.md TOOL-4).

The distributed map bundles are AMPB containers (AES-256-CTR, chunked, authenticated). The validators and the
release packer must look inside them without a Python crypto library (none is installed and none is to be added),
so they run the one C# implementation instead: `dotnet build` of tools/bundle/ArenaMapPort.BundleCrypt once per
process (skipped while the executable is newer than every source it is built from), then the executable.

    bundle_crypt.is_encrypted(path)                      the file starts with b"AMPB" (4 bytes read, no tool)
    bundle_crypt.inspect(path, key_store=None, ...)      header facts (dict); with a key store (the default one when
                                                         None; pass False for none) the header MAC is verified, with
                                                         verify_chunks every chunk hash too
    bundle_crypt.read_range(path, offset, length, ...)   plaintext bytes; offset < 0 counts from the plaintext end
    bundle_crypt.plain_sha256(path, key_store)           (hex digest, length) of the decrypted bundle
    bundle_crypt.verify(map_key, plain, enc, key_store)  `enc` is exactly the encryption of `plain`
    bundle_crypt.verify_keys(map_key, keys_file, ...)    the shipped key file unlocks the store's key against the
                                                         Arena installed on this machine (the player's derivation)
    bundle_crypt.decrypt(path, out, key_store)           whole bundle to a file

Every failure is a BundleCryptError carrying the tool's "FAIL ..." line. The environment variable
ARENAMAPPORT_BUNDLECRYPT names an executable to use instead of building (the tests use it to pin a build).
Key material never passes through this module as text; the key store is a folder the tool reads itself
(default work/release-keys, ignored by git).
"""
from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TOOL_DIR = REPO / "tools" / "bundle" / "ArenaMapPort.BundleCrypt"
CSPROJ = TOOL_DIR / "ArenaMapPort.BundleCrypt.csproj"
EXE = TOOL_DIR / "bin" / "Release" / "net10.0" / "ArenaMapPort.BundleCrypt.exe"
DLL = EXE.with_suffix(".dll")
KEY_SOURCES = REPO / "tools" / "bundle" / "key-sources.json"
DEFAULT_KEY_STORE = REPO / "work" / "release-keys"
ENV_OVERRIDE = "ARENAMAPPORT_BUNDLECRYPT"
MAGIC = b"AMPB"

_built = False


class BundleCryptError(RuntimeError):
    """The tool failed (exit 1), was misused (exit 2) or could not be built. `message` is the tool's FAIL/USAGE line."""

    def __init__(self, message: str, returncode: int = 1, stdout: bytes = b"", stderr: bytes = b""):
        super().__init__(message)
        self.message = message
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr


def _sources():
    yield from TOOL_DIR.glob("*.cs")
    yield CSPROJ
    yield from (REPO / "ArenaMapPort.Crypto").glob("*.cs")
    yield REPO / "ArenaMapPort.Shared" / "ArenaInstallation.cs"
    yield REPO / "ArenaMapPort.Shared" / "ArenaStatusContract.cs"


def _up_to_date() -> bool:
    """The executable exists and the compiled assembly is newer than every source (the apphost .exe is not rewritten by
    every compile, so the .dll is the timestamp that moves)."""
    if not EXE.is_file() or not DLL.is_file():
        return False
    built = DLL.stat().st_mtime
    return all(p.stat().st_mtime <= built for p in _sources() if p.is_file())


def ensure_built() -> Path:
    """Build the tool once per process (a no-op when the executable is newer than its sources) and return its path."""
    global _built
    override = os.environ.get(ENV_OVERRIDE)
    if override:
        path = Path(override)
        if not path.is_file():
            raise BundleCryptError(f"{ENV_OVERRIDE} names a missing file: {path}")
        return path
    if not _built:
        if not _up_to_date():
            try:
                proc = subprocess.run(["dotnet", "build", str(CSPROJ), "-c", "Release", "-nologo", "-v:q"],
                                      cwd=REPO, capture_output=True, text=True)
            except FileNotFoundError as exc:
                raise BundleCryptError("the BundleCrypt tool must be built and `dotnet` was not found on PATH "
                                       f"(or set {ENV_OVERRIDE} to a built executable)") from exc
            if proc.returncode != 0 or not EXE.is_file():
                raise BundleCryptError("dotnet build of ArenaMapPort.BundleCrypt failed:\n"
                                       + (proc.stdout + proc.stderr)[-2000:], proc.returncode)
        _built = True
    return EXE


def run(*args, check: bool = True, input: bytes | None = None) -> subprocess.CompletedProcess:
    """Run the tool; stdout and stderr come back as bytes (read-range writes binary data to stdout)."""
    exe = ensure_built()
    proc = subprocess.run([str(exe), *[str(a) for a in args]], capture_output=True, input=input)
    if check and proc.returncode != 0:
        text = (proc.stderr or proc.stdout).decode("utf-8", errors="replace").strip()
        raise BundleCryptError(text.splitlines()[-1] if text else f"exit code {proc.returncode}",
                               proc.returncode, proc.stdout, proc.stderr)
    return proc


def _key_args(key_store):
    return ["--key-store", str(DEFAULT_KEY_STORE if key_store is None else key_store)]


def is_encrypted(path) -> bool:
    with open(path, "rb") as stream:
        return stream.read(4) == MAGIC


def inspect(path, key_store=None, verify_chunks: bool = False) -> dict:
    """Header facts of a container (keys of the tool's --json output). With a key store the header MAC is verified
    (`mac_verified`); with `verify_chunks` every ciphertext chunk is hashed against the table (`chunks` is "verified").
    A defective or foreign file is a BundleCryptError."""
    args = ["inspect", "--in", path, "--json"]
    if key_store is not False:
        args += _key_args(key_store)
    if verify_chunks:
        args.append("--verify-chunks")
    return json.loads(run(*args).stdout.decode("utf-8"))


def read_range(path, offset: int, length: int, key_store=None, clip: bool = False) -> bytes:
    """Plaintext bytes [offset, offset + length) of an encrypted bundle; a negative offset counts from the plaintext end.
    A range that reaches past the end is an error unless `clip` (then the bytes up to the end)."""
    args = ["read-range", "--in", path, "--offset", offset, "--length", length, "--out", "-", *_key_args(key_store)]
    if clip:
        args.append("--clip")
    return run(*args).stdout


def plain_sha256(path, key_store=None) -> tuple[str, int]:
    """(SHA-256 hex digest, length) of the decrypted bundle, streamed (nothing is written to disk)."""
    text = run("plain-sha256", "--in", path, *_key_args(key_store)).stdout.decode("utf-8").strip()
    fields = text.split()  # OK plain-sha256 <hex> <n> B <name> in <ms> ms
    return fields[2], int(fields[3])


def decrypt(path, out, key_store=None) -> None:
    run("decrypt", "--in", path, "--out", out, *_key_args(key_store))


def verify(map_key: str, plain, enc, key_store=None) -> str:
    """Passes when `enc` is byte for byte the encryption of `plain` (the build is deterministic); returns the OK line."""
    return run("verify", "--map-key", map_key, "--plain", plain, "--enc", enc, *_key_args(key_store)).stdout.decode("utf-8").strip()


def verify_keys(map_key: str, keys_file, key_store=None, arena: str = "auto", arena_unverified=None, built_for=None) -> str:
    """Passes when the shipped key file, resolved against the Arena installed on this machine exactly as a player's client does,
    unlocks the content key held in the key store. `arena_unverified` (a folder) is the test seam of the tool and needs
    ARENAMAPPORT_TOOL_TEST=1 in the environment. Returns the OK line."""
    args = ["verify-keys", "--map-key", map_key, "--keys-file", keys_file, *_key_args(key_store)]
    if arena_unverified is not None:
        args += ["--arena-unverified", arena_unverified]
        if built_for is not None:
            args += ["--built-for", built_for]
    else:
        args += ["--arena", arena]
    return run(*args).stdout.decode("utf-8").strip()
