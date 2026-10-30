#!/usr/bin/env python3
"""Scan the release DLLs with VirusTotal before a release and record the verdicts.

The release notes carry one line per DLL ("0 malicious, 0 suspicious of N engines") next to the VirusTotal page of
the file's SHA-256. This tool produces exactly those lines: for each file it computes the local SHA-256, asks
VirusTotal whether the hash is known, uploads the file when it is not, waits for the analysis, reads the final
verdict, writes a JSON record and prints a ready-to-paste Markdown block. Stdlib only; nothing is ever deleted or
changed on VirusTotal.

VirusTotal API v3 (https://docs.virustotal.com/reference/overview), HTTPS to www.virustotal.com only, every request
with the header "x-apikey: <key>":

    GET  /api/v3/files/{sha256}    200 {"data": {"attributes": {"last_analysis_stats": {"harmless", "malicious",
                                   "suspicious", "undetected", "timeout", ...}, "last_analysis_date": <unix>, ...}}}
                                   when the file is known; 404 NotFoundError when it was never uploaded
    POST /api/v3/files             multipart/form-data, one field "file" (filename, bytes); up to 32 MiB on this
                                   endpoint; 200 {"data": {"type": "analysis", "id": <analysis id>}}
    GET  /api/v3/analyses/{id}     {"data": {"attributes": {"status": "queued" | "in-progress" | "completed",
                                   "stats": {...}}}, "meta": {"file_info": {"sha256": ...}}}

    page of a file: https://www.virustotal.com/gui/file/{sha256}

The public API allows 4 requests per minute and 500 per day. Every request of a run, whatever it is for, goes through
one pacer that never lets more than 4 requests start within 60 s (plus a 1 s margin): the fifth request waits until the
first of the last four is 61 s old. HTTP 429 (quota exceeded) sleeps 60 s and retries; 401 is final ("API key
rejected", no retry); network errors (URLError, other OSError, timeouts, http.client errors) and 5xx answers are
retried 3 times with a 1, 2, 4 s backoff (four attempts in all); any other 4xx is final. Redirects are never followed.

Flow per file: (1) SHA-256 and size; (2) GET /files/{sha256}; known with a verdict and no --force-upload: that is the
verdict ("already known" in the output) and nothing is uploaded; known but without an analysis yet: the tool waits for
one; (3) 404 (or --force-upload, which skips the lookup): POST the file (refused with a clear error when it is over
32 MiB, because the plain upload endpoint takes no more), then every 20 s GET /analyses/{id} until its status is
"completed", then GET /files/{sha256} once more for the final stats (should that object lack fresh stats, or the lookup
fail for want of quota, --wait time, network or a 5xx answer, the completed analysis' own stats are used: it already
carries the verdict; a 401, another 4xx or an answer for another file still stops the run). The time a file may take (quota pauses, 429 sleeps, polling) is --wait seconds
(default 600), counted from the moment its processing starts; an analysis that is still running then is reported,
and it keeps running on VirusTotal, so a later run finds it by the hash. The verdict is the count of malicious and of
suspicious engines and the total engines (the sum of all values of the stats object).

The key is resolved in this order: --key-file PATH (content stripped, BOM tolerated); else the environment variable
named by --key-env (default VIRUSTOTAL_API_KEY); else, on Windows only, that variable from the registry, User scope
(HKCU\\Environment) and then Machine scope (HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment),
because a shell started before the variable was set does not see it. A missing key is an error that names the three
sources. There is deliberately no option that takes the key itself. The key lives in a Secret wrapper whose repr and
str are '***'; it appears only in the x-apikey header, never in stdout, stderr, log lines, error messages (redacted,
also in text a server or the network stack sends back), the record or the tests. Should a key be typed on the command
line all the same (--key=KEY, --wait=KEY, --version KEY, a bare KEY), no error message repeats it: argparse errors hide
every typed value of 8 or more characters, later errors every key-shaped one (20 or more of A-Z a-z 0-9 _ -).

Files: positional FILE [FILE ...]; without any, the client and the server DLL below --dist (default <repo>/dist),
at the paths make_release_zips.load_spec(...).client_dll / .server_dll give. The version comes from the agreeing
csproj <Version> (--version overrides; a value that is no MAJOR.MINOR.PATCH[-label] is refused without being shown).

Output, one line per file:

    <name>  sha256 <hash>  <malicious> malicious, <suspicious> suspicious of <total> engines  <gui link>  (analysed <ISO UTC>)

followed by a Markdown block between "----- Markdown block (copy from here) -----" and "----- end of Markdown block
-----": one bullet per file, "- [name](gui link): <malicious> malicious, <suspicious> suspicious of <total> engines
(<ISO UTC>)", and a last line VIRUSTOTAL_SCAN_CLEAN <version> or VIRUSTOTAL_SCAN_DETECTIONS <count>.

Record, --record PATH (default <repo>/work/releases/<version>/virustotal.json for the default two-DLL list; work/ is
git-ignored), written atomically (.partial next to it, then os.replace) and replaced by every successful run of the same
list. A scan of explicit FILEs defaults to virustotal-files.json in that directory instead, so that a probe file never
replaces the release record of the two DLLs; pass --record PATH to choose the file deliberately:

    {"schema": 1, "version": "0.2.0", "scanned_utc": "2026-10-16T12:00:00Z",
     "files": [{"name", "size", "sha256", "gui_url", "stats": {...as VirusTotal reported...}, "malicious",
                "suspicious", "total_engines", "analysis_date_utc", "uploaded": bool}, ...]}

"uploaded" is true when this run sent the file (the verdict is then that of the new analysis). A run that stops with
an error writes no record: a partly scanned set would be a misleading basis for release notes, and the files already
finished are cheap to look up again by their hash.

Modes:
    (default)         scan, record, print
    --force-upload    upload even when VirusTotal already knows the hash (a fresh analysis with today's engines)
    --dry-run         hash the files, show the key source and what a real run would do: no network call, nothing
                      written, no directory created

Exit status: 0 when every file has 0 malicious and 0 suspicious engines; 2 when any file has a detection (the record
is still written; read the engine names on the VirusTotal page, Unity/BepInEx DLLs do attract false positives); 1 on
any error, a usage error included (message on stderr), so that 2 always means a detection.

Nothing here touches dist/ or any install; the only write is the record.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import http.client
import json
import os
import re
import secrets
import ssl
import sys
import time
import urllib.error
import urllib.parse
from collections import deque
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import make_release_zips as release  # noqa: E402

HOST = "www.virustotal.com"
SITE = f"https://{HOST}"
API = f"{SITE}/api/v3"
GUI_FILE = f"{SITE}/gui/file/"
DEFAULT_KEY_ENV = "VIRUSTOTAL_API_KEY"
RECORD_NAME = "virustotal.json"
FILES_RECORD_NAME = "virustotal-files.json"  # default record of a scan of explicit FILEs, beside the release record
RECORD_SCHEMA = 1
USER_AGENT = "maschine-ArenaMapPort-release-scanner/1"

MAX_UPLOAD_BYTES = 32 * 1024 * 1024  # the plain POST /files endpoint takes no more
MAX_RESPONSE = 16 * 1024 * 1024      # a file object with all engine results can run to a few hundred KiB
QUOTA_REQUESTS = 4                   # public API: 4 requests per minute
QUOTA_WINDOW = 60.0
QUOTA_MARGIN = 1.0                   # added to the window so that clock jitter never makes the fifth request early
QUOTA_BACKOFF = 60.0                 # sleep after HTTP 429
POLL_INTERVAL = 20.0
# GET /analyses/{id} answered 404 NotFoundError right after a successful upload twice on 2026-10-30 (the analysis object registers a
# little later than the upload answer): so many 404 polls in a row are tolerated before the scan gives up.
NOT_FOUND_POLLS = 6
NETWORK_RETRIES = 3                  # after the first attempt: waits of 1, 2, 4 s
ERROR_TEXT_LIMIT = 300
TYPED_MIN_LENGTH = 8                 # no API key is shorter: shorter typed values are not hidden from error messages

KEY_RE = re.compile(r"[\x21-\x7e]+")  # printable ASCII without blanks: all an API key can be, and header-safe
KEYLIKE_RE = re.compile(r"[A-Za-z0-9_-]{20,}")  # what a pasted key looks like; no dot or separator, so no file path
ENV_NAME_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
SHA256_RE = re.compile(r"[0-9a-f]{64}")
ANALYSIS_ID_RE = re.compile(r"[A-Za-z0-9+/=_.-]{1,512}")

# (scope, winreg hive attribute, sub key, text shown to the user)
REGISTRY_SCOPES = (
    ("User", "HKEY_CURRENT_USER", "Environment", "HKCU\\Environment"),
    ("Machine", "HKEY_LOCAL_MACHINE", r"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
     "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment"),
)


class ScanError(Exception):
    """Any refusal: bad input, missing key, VirusTotal rejection, a scan that cannot be finished."""


class UsageError(ScanError):
    pass


class MissingKeyError(ScanError):
    pass


class ApiError(ScanError):
    def __init__(self, message, status=None):
        super().__init__(message)
        self.status = status


class NoAnswerError(ApiError):
    """VirusTotal could not be asked or gave no usable answer: the request quota, the --wait budget, the network or
    a 5xx. Unlike a refusal (401, other 4xx) or an answer for the wrong file, it says nothing about the data, so a
    verdict already in hand can stand without the request that failed."""


def default_log(message: str = "") -> None:
    print(message, flush=True)


def default_sleep(seconds: float) -> None:
    time.sleep(seconds)  # looked up at call time, so a test (or a caller) can patch time.sleep


def default_monotonic() -> float:
    return time.monotonic()


def default_clock() -> datetime:
    return datetime.now(timezone.utc)


# --------------------------------------------------------------------------------------------------------------
# The API key
# --------------------------------------------------------------------------------------------------------------

class Secret:
    """Holds the API key so that an accidental print / f-string / repr shows '***' instead of the value."""
    __slots__ = ("_value",)

    def __init__(self, value: str):
        self._value = value

    def reveal(self) -> str:
        return self._value

    def __repr__(self) -> str:
        return "Secret(***)"

    def __str__(self) -> str:
        return "***"


def redact(text, key) -> str:
    """text with the key (raw, and base64 of it in case a stack echoes an encoded header) replaced by '***'."""
    text = str(text)
    if key is None:
        return text
    raw = key.reveal() if isinstance(key, Secret) else key
    if not raw:
        return text
    for form in (raw, base64.b64encode(raw.encode("ascii", "replace")).decode("ascii")):
        text = text.replace(form, "***")
    return text


def _clean(text, key, limit: int = ERROR_TEXT_LIMIT) -> str:
    """Printable, key-free, length-limited text: server answers and exception messages are data."""
    text = re.sub(r"[\x00-\x1f\x7f]+", " ", redact(text, key)).strip()
    return text if len(text) <= limit else text[:limit] + "..."


def _validated_key(raw: str, source: str) -> Secret:
    raw = raw.strip()
    if not raw:
        raise ScanError(f"the API key from {source} is empty")
    if not KEY_RE.fullmatch(raw):
        raise ScanError(f"the API key from {source} contains whitespace or non-ASCII characters, which no "
                        "VirusTotal key has (the value is not shown); save it as plain ASCII text")
    return Secret(raw)


def read_key_file(path: Path) -> str:
    try:
        return Path(path).read_text(encoding="utf-8-sig")
    except UnicodeDecodeError as exc:
        raise ScanError(f"--key-file {path} is not UTF-8/ASCII text (a UTF-16 file from PowerShell 5 '>' "
                        "redirection?); save it as plain ASCII") from exc
    except OSError as exc:
        raise ScanError(f"cannot read --key-file {path}: {exc.strerror or type(exc).__name__}") from exc


def registry_values(name: str, winreg):
    """(scope, value) for the variable in the User scope, then the Machine scope; unset or non-text entries are
    skipped. A shell started before the variable was set does not see it in os.environ, the registry does."""
    for scope, hive_name, subkey, _shown in REGISTRY_SCOPES:
        try:
            with winreg.OpenKey(getattr(winreg, hive_name), subkey, 0, winreg.KEY_READ) as handle:
                value, _kind = winreg.QueryValueEx(handle, name)
        except OSError:
            continue
        if isinstance(value, str) and value.strip():
            yield scope, value


def missing_key_message(key_env: str, platform: str) -> str:
    if platform == "win32":
        registry = (f"(3) the Windows registry variable {key_env}, "
                    + " and then ".join(f"{scope} scope {shown}" for scope, _hive, _sub, shown in REGISTRY_SCOPES)
                    + " (not set in either)")
    else:
        registry = "(3) the Windows registry (skipped: this is not Windows)"
    return (f"no VirusTotal API key found. Looked in three places, in this order: (1) --key-file PATH (not given); "
            f"(2) the environment variable {key_env} of this process (not set or empty); {registry}. A shell "
            "started before the variable was set does not see it: open a new shell, or pass --key-file.")


def resolve_key(key_file, key_env: str, environ, platform: str, winreg=None) -> tuple:
    """(Secret, description of where it came from). The description never contains the value."""
    if key_file:
        source = f"--key-file {key_file}"
        return _validated_key(read_key_file(Path(key_file)), source), source
    value = (environ.get(key_env) or "").strip()
    if value:
        source = f"environment variable {key_env}"
        return _validated_key(value, source), source
    if platform == "win32":
        if winreg is None:
            try:
                import winreg  # noqa: PLC0415 - exists on Windows only
            except ImportError:
                winreg = None
        if winreg is not None:
            for scope, value in registry_values(key_env, winreg):
                source = f"registry variable {key_env} ({scope} scope)"
                return _validated_key(value, source), source
    raise MissingKeyError(missing_key_message(key_env, platform))


# --------------------------------------------------------------------------------------------------------------
# HTTP transport
# --------------------------------------------------------------------------------------------------------------

def _parse_body(raw: bytes):
    text = raw.decode("utf-8", errors="replace")
    if not text.strip():
        return None
    try:
        return json.loads(text)
    except ValueError:
        return text


def http_request(method: str, url: str, headers: dict, body, timeout: float):
    """The one place that touches the network: returns (status, parsed JSON | text | None). `body` is None or
    bytes. Only https://www.virustotal.com is ever contacted, so the x-apikey header cannot be sent anywhere else."""
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "https" or parts.hostname != HOST:
        raise ScanError(f"refusing to contact {parts.scheme}://{parts.hostname}: only https://{HOST} is allowed")
    connection = http.client.HTTPSConnection(parts.hostname, parts.port or 443, timeout=timeout,
                                             context=ssl.create_default_context())
    target = parts.path + (f"?{parts.query}" if parts.query else "")
    try:
        connection.request(method, target, body=body, headers=headers)
        response = connection.getresponse()
        raw = response.read(MAX_RESPONSE + 1)
        if len(raw) > MAX_RESPONSE:
            raise ScanError(f"VirusTotal's answer is larger than {MAX_RESPONSE // 2 ** 20} MiB")
        return response.status, _parse_body(raw)
    finally:
        connection.close()


def multipart_body(filename: str, data: bytes) -> tuple:
    """(Content-Type header value, body) of a multipart/form-data request with the one field "file". The boundary
    is random and is redrawn should it occur in the data. In the filename `"`, CR and LF are percent-escaped (the
    rule browsers follow); the callers allow no other control characters."""
    while True:
        boundary = "----ArenaMapPortScan" + secrets.token_hex(16)
        if boundary.encode("ascii") not in data:
            break
    shown = filename.replace('"', "%22").replace("\r", "%0D").replace("\n", "%0A")
    head = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{shown}"\r\n'
            "Content-Type: application/octet-stream\r\n\r\n").encode("utf-8")
    tail = f"\r\n--{boundary}--\r\n".encode("ascii")
    return f"multipart/form-data; boundary={boundary}", head + data + tail


# --------------------------------------------------------------------------------------------------------------
# Files, verdicts, dates
# --------------------------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class LocalFile:
    name: str
    path: Path
    size: int
    sha256: str


def check_file_name(name: str) -> str:
    if not name or len(name) > 255 or any(ord(c) < 32 or c == "\x7f" for c in name):
        raise ScanError(f"unusable file name {name!r}")
    return name


def local_file(path: Path) -> LocalFile:
    """Name, size and SHA-256 of a regular file (read-only)."""
    path = Path(path)
    if not path.is_file():
        raise ScanError(f"file not found: {path}")
    try:
        size = path.stat().st_size
        digest = release.sha256_file(path)
    except OSError as exc:
        raise ScanError(f"cannot read {path}: {exc}") from exc
    return LocalFile(check_file_name(path.name), path, size, digest)


def limit_text() -> str:
    return f"{MAX_UPLOAD_BYTES:,} bytes ({MAX_UPLOAD_BYTES / 2 ** 20:g} MiB)"


def gui_url(sha256: str) -> str:
    return GUI_FILE + sha256


def utc_text(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def date_value(stamp):
    """The ISO UTC text of a unix time, or None when VirusTotal gave none (or nonsense)."""
    if not isinstance(stamp, int) or isinstance(stamp, bool):
        return None
    try:
        return utc_text(datetime.fromtimestamp(stamp, timezone.utc))
    except (OverflowError, OSError, ValueError):
        return None


def date_text(stamp) -> str:
    return date_value(stamp) or "unknown"


def clean_stats(stats):
    """The stats object reduced to its non-negative integer counts, or None when it counts no engine at all (an
    analysis that has not produced a verdict yet)."""
    if not isinstance(stats, dict):
        return None
    counts = {key: value for key, value in stats.items()
              if isinstance(key, str) and 0 < len(key) <= 64 and isinstance(value, int)
              and not isinstance(value, bool) and value >= 0}
    return counts if sum(counts.values()) > 0 else None


def verdict_of(attributes: dict, stats_key: str, date_key: str):
    """(stats, unix date or None) of a file or analysis object, or None without a usable verdict."""
    stats = clean_stats(attributes.get(stats_key))
    if stats is None:
        return None
    stamp = attributes.get(date_key)
    return stats, stamp if isinstance(stamp, int) and not isinstance(stamp, bool) else None


@dataclass(frozen=True)
class Result:
    local: LocalFile
    stats: dict
    date: object        # unix time or None
    uploaded: bool

    @property
    def malicious(self) -> int:
        return self.stats.get("malicious", 0)

    @property
    def suspicious(self) -> int:
        return self.stats.get("suspicious", 0)

    @property
    def total(self) -> int:
        return sum(self.stats.values())

    @property
    def detected(self) -> bool:
        return self.malicious > 0 or self.suspicious > 0


def verdict_text(result: Result) -> str:
    return f"{result.malicious} malicious, {result.suspicious} suspicious of {result.total} engines"


def result_line(result: Result) -> str:
    return (f"{result.local.name}  sha256 {result.local.sha256}  {verdict_text(result)}  "
            f"{gui_url(result.local.sha256)}  (analysed {date_text(result.date)})")


def markdown_lines(results) -> list:
    lines = []
    for result in results:
        label = re.sub(r"([\\\[\]])", r"\\\1", result.local.name)
        lines.append(f"- [{label}]({gui_url(result.local.sha256)}): {verdict_text(result)} "
                     f"({date_text(result.date)})")
    return lines


def build_record(version, scanned: datetime, results) -> dict:
    return {"schema": RECORD_SCHEMA, "version": version, "scanned_utc": utc_text(scanned),
            "files": [{"name": r.local.name, "size": r.local.size, "sha256": r.local.sha256,
                       "gui_url": gui_url(r.local.sha256), "stats": dict(r.stats), "malicious": r.malicious,
                       "suspicious": r.suspicious, "total_engines": r.total,
                       "analysis_date_utc": date_value(r.date), "uploaded": r.uploaded} for r in results]}


def write_record(path: Path, record: dict) -> None:
    """Atomic: .partial next to it, then os.replace."""
    partial = path.with_name(path.name + ".partial")
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        partial.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8", newline="\n")
        os.replace(partial, path)
    except BaseException:
        if partial.exists():
            partial.unlink()
        raise


# --------------------------------------------------------------------------------------------------------------
# API client
# --------------------------------------------------------------------------------------------------------------

def attributes_of(body, label: str) -> dict:
    data = body.get("data") if isinstance(body, dict) else None
    attributes = data.get("attributes") if isinstance(data, dict) else None
    if not isinstance(attributes, dict):
        raise ApiError(f"{label}: VirusTotal's answer carries no attributes object")
    return attributes


def file_info_sha(body):
    """meta.file_info.sha256 of an analysis answer (VirusTotal puts meta beside data; inside data is tolerated)."""
    containers = []
    if isinstance(body, dict):
        containers.append(body)
        if isinstance(body.get("data"), dict):
            containers.append(body["data"])
    for container in containers:
        meta = container.get("meta")
        info = meta.get("file_info") if isinstance(meta, dict) else None
        digest = info.get("sha256") if isinstance(info, dict) else None
        if isinstance(digest, str) and digest:
            return digest.lower()
    return None


class Client:
    """VirusTotal calls over an injected transport: the x-apikey header, the shared request pacer, retries and error
    mapping. `transport` is transport(method, url, headers, body, timeout) -> (status, json | text | None).
    `deadline` (monotonic time, set per file by the Scanner) bounds every quota pause and 429 sleep."""

    def __init__(self, key: Secret, transport, *, timeout: float, sleep, monotonic, log):
        self.key = key
        self.transport = transport
        self.timeout = timeout
        self.sleep = sleep
        self.monotonic = monotonic
        self.log = log
        self.deadline = None
        self.started = deque(maxlen=QUOTA_REQUESTS)  # monotonic start times of the last QUOTA_REQUESTS requests

    def __repr__(self) -> str:
        return "Client(key=***)"

    # -- pacing, retries -----------------------------------------------------------------------------------------

    def _pace(self, label: str) -> None:
        """Never start the fifth request within a minute of the first of the last four."""
        now = self.monotonic()
        if len(self.started) == QUOTA_REQUESTS:
            wait = self.started[0] + QUOTA_WINDOW + QUOTA_MARGIN - now
            if wait > 0:
                if self.deadline is not None and now + wait > self.deadline:
                    raise NoAnswerError(f"{label}: --wait leaves no time for the next request: VirusTotal's public "
                                        f"API allows {QUOTA_REQUESTS} requests per minute and this one would have "
                                        f"to wait {wait:.0f} s; raise --wait or run again later")
                self.log(f"  {label}: pausing {wait:.0f} s for the request quota "
                         f"({QUOTA_REQUESTS} requests per minute)")
                self.sleep(wait)
                now = self.monotonic()
        self.started.append(now)

    def _quota_exceeded(self, label: str) -> None:
        if self.deadline is not None and self.monotonic() + QUOTA_BACKOFF > self.deadline:
            raise NoAnswerError(f"{label}: VirusTotal's request quota is used up (HTTP 429; the public API allows "
                                f"{QUOTA_REQUESTS} requests per minute and 500 per day) and --wait leaves no time "
                                "to wait for it; run again later", 429)
        self.log(f"  {label}: request quota exceeded (HTTP 429); waiting {QUOTA_BACKOFF:g} s")
        self.sleep(QUOTA_BACKOFF)

    def request(self, method: str, path: str, *, body=None, content_type=None, allow=(), label: str):
        """(status, data). Non-2xx raises ApiError unless the status is in `allow`. 429 waits and repeats, network
        errors and 5xx are retried NETWORK_RETRIES times with a 1, 2, 4 s backoff, 401 and other 4xx are final."""
        url = API + path
        headers = {"x-apikey": self.key.reveal(), "Accept": "application/json", "User-Agent": USER_AGENT}
        if body is not None:
            headers["Content-Type"] = content_type
            headers["Content-Length"] = str(len(body))
        failures = 0
        while True:
            self._pace(label)
            problem = None
            status = data = None
            try:
                status, data = self.transport(method, url, dict(headers), body, self.timeout)
            except urllib.error.HTTPError as exc:  # a urllib-based transport reports 4xx/5xx this way
                status, data = exc.code, None
            except ScanError:
                raise
            except (OSError, http.client.HTTPException) as exc:
                problem = f"network error {type(exc).__name__}: {_clean(exc, self.key)}"
            if problem is None and status == 429:
                self._quota_exceeded(label)
                continue
            if problem is None and status >= 500:
                problem = f"HTTP {status}" + self._detail(data)
            if problem is None:
                break
            failures += 1
            if failures > NETWORK_RETRIES:
                raise NoAnswerError(f"{label} failed after {failures} attempt(s): {problem}")
            delay = 2.0 ** (failures - 1)
            self.log(f"  {label}: {problem}; retry {failures} of {NETWORK_RETRIES} in {delay:g} s")
            self.sleep(delay)
        if 200 <= status < 300 or status in allow:
            return status, data
        raise ApiError(f"{label}: {self._failure(status, data)}", status)

    def _detail(self, data) -> str:
        error = data.get("error") if isinstance(data, dict) else None
        if isinstance(error, dict):
            pieces = [str(p) for p in (error.get("code"), error.get("message")) if p]
            if pieces:
                return ": " + _clean(" - ".join(pieces), self.key)
        elif isinstance(data, str) and data.strip():
            return ": " + _clean(data, self.key)
        return ""

    def _failure(self, status: int, data) -> str:
        if status == 401:
            return "API key rejected by VirusTotal (HTTP 401)" + self._detail(data)
        return f"HTTP {status}" + self._detail(data)

    # -- endpoints -----------------------------------------------------------------------------------------------

    def get_file(self, local: LocalFile):
        """The attributes of the file object, or None when VirusTotal has never seen the hash (404)."""
        label = f"lookup {local.name}"
        status, data = self.request("GET", f"/files/{local.sha256}", allow=(404,), label=label)
        if status == 404:
            return None
        attributes = attributes_of(data, label)
        digest = attributes.get("sha256")
        if isinstance(digest, str) and digest.lower() != local.sha256:
            raise ApiError(f"{label}: VirusTotal answered for another file ({digest.lower()}, expected "
                           f"{local.sha256})")
        return attributes

    def upload(self, local: LocalFile) -> str:
        """POST the file; returns the analysis id. Refuses files over MAX_UPLOAD_BYTES before reading them."""
        label = f"upload {local.name}"
        if local.size > MAX_UPLOAD_BYTES:
            raise ScanError(f"{local.name} is {local.size:,} bytes, more than the {limit_text()} that VirusTotal "
                            "accepts on its plain upload endpoint, and VirusTotal does not know its hash yet: "
                            "upload it by hand at https://www.virustotal.com (large files need the website's or the "
                            "API's large-file route), then run this tool again; it finds the file by its hash")
        try:
            data = local.path.read_bytes()
        except OSError as exc:
            raise ScanError(f"cannot read {local.path}: {exc}") from exc
        if len(data) != local.size or hashlib.sha256(data).hexdigest() != local.sha256:
            raise ScanError(f"{local.name} changed while it was being scanned; run again once the file is stable")
        content_type, body = multipart_body(local.name, data)
        _status, answer = self.request("POST", "/files", body=body, content_type=content_type, label=label)
        found = answer.get("data") if isinstance(answer, dict) else None
        analysis_id = found.get("id") if isinstance(found, dict) else None
        if not isinstance(analysis_id, str) or not ANALYSIS_ID_RE.fullmatch(analysis_id):
            raise ApiError(f"{label}: VirusTotal's answer carries no usable analysis id")
        return analysis_id

    def get_analysis(self, local: LocalFile, analysis_id: str):
        """The attributes of the analysis, or None when VirusTotal does not know the id (yet): a 404 right after the upload."""
        label = f"analysis {local.name}"
        status, data = self.request("GET", f"/analyses/{urllib.parse.quote(analysis_id, safe='')}", allow=(404,), label=label)
        if status == 404:
            return None
        attributes = attributes_of(data, label)
        digest = file_info_sha(data)
        if digest is not None and digest != local.sha256:
            raise ApiError(f"{label}: the analysis belongs to another file ({digest}, expected {local.sha256})")
        return attributes


# --------------------------------------------------------------------------------------------------------------
# Scan flow
# --------------------------------------------------------------------------------------------------------------

class Scanner:
    def __init__(self, client: Client, *, wait: float, force_upload: bool, sleep, monotonic, log):
        self.client = client
        self.wait = wait
        self.force_upload = force_upload
        self.sleep = sleep
        self.monotonic = monotonic
        self.log = log

    def scan(self, local: LocalFile) -> Result:
        client = self.client
        client.deadline = self.monotonic() + self.wait
        if self.force_upload:
            self.log(f"  {local.name}: --force-upload: uploading {local.size:,} bytes without looking the hash up")
        else:
            attributes = client.get_file(local)
            if attributes is not None:
                verdict = verdict_of(attributes, "last_analysis_stats", "last_analysis_date")
                if verdict is None:
                    self.log(f"  {local.name}: VirusTotal knows the hash but has no analysis of it yet; waiting")
                    verdict = self._await_file_verdict(local)
                else:
                    self.log(f"  {local.name}: already known to VirusTotal (analysed {date_text(verdict[1])}); "
                             "nothing uploaded")
                return Result(local, verdict[0], verdict[1], uploaded=False)
            self.log(f"  {local.name}: not known to VirusTotal yet; uploading {local.size:,} bytes")
        analysis_id = client.upload(local)
        self.log(f"  {local.name}: uploaded, analysis {analysis_id}; waiting for the engines "
                 f"(at most {self.wait:g} s)")
        analysis = self._await_analysis(local, analysis_id)
        by_analysis = verdict_of(analysis, "stats", "date")
        try:
            final = client.get_file(local)
        except NoAnswerError as exc:
            # The completed analysis already carries the verdict; the file object is only the fresher copy of it, so
            # a final lookup that cannot be made (quota, --wait budget, network, 5xx) must not throw the verdict away.
            # Refusals (401, other 4xx) and an answer for another file stay errors.
            if by_analysis is None:
                raise
            self.log(f"  {local.name}: the final lookup of the file object failed ({_clean(exc, client.key)}); "
                     "using the statistics of the completed analysis")
            final = None
        by_file = verdict_of(final, "last_analysis_stats", "last_analysis_date") if final is not None else None
        if by_file is not None and by_analysis is not None and by_file[1] is not None \
                and by_analysis[1] is not None and by_file[1] < by_analysis[1]:
            by_file = None  # the file object still shows the analysis before this one (a forced re-upload)
        verdict = by_file or by_analysis
        if verdict is None:
            raise ApiError(f"{local.name}: the analysis {analysis_id} is completed but carries no engine "
                           "statistics; look at the file's VirusTotal page")
        return Result(local, verdict[0], verdict[1], uploaded=True)

    def _out_of_time(self, local: LocalFile, what: str) -> ScanError:
        return ScanError(f"{local.name}: {what} did not finish within --wait {self.wait:g} s. It keeps running on "
                         "VirusTotal: run this tool again later (the finished analysis is found by the file's "
                         "hash) or raise --wait")

    def _await_analysis(self, local: LocalFile, analysis_id: str) -> dict:
        """Poll every POLL_INTERVAL seconds, the first poll too: a fresh analysis is never done at once and every
        poll costs quota. Returns the attributes of the completed analysis."""
        status, polls, missing = "queued", 0, 0
        while True:
            if self.monotonic() + POLL_INTERVAL > self.client.deadline:
                raise self._out_of_time(local, f"the analysis (last status: {status})")
            self.sleep(POLL_INTERVAL)
            polls += 1
            attributes = self.client.get_analysis(local, analysis_id)
            if attributes is None:
                missing += 1
                self.log(f"  {local.name}: analysis not registered yet, HTTP 404 (poll {polls}; {missing} of at most {NOT_FOUND_POLLS} such answers)")
                if missing >= NOT_FOUND_POLLS:
                    raise ApiError(f"{local.name}: VirusTotal answered 404 for analysis {analysis_id} {missing} times in a row; "
                                   "the upload may not have registered, run the scan again")
                continue
            missing = 0
            status = attributes.get("status") if isinstance(attributes.get("status"), str) else "unknown"
            self.log(f"  {local.name}: analysis {_clean(status, self.client.key, 40)} (poll {polls})")
            if status == "completed":
                return attributes

    def _await_file_verdict(self, local: LocalFile):
        """A known hash whose file object has no stats yet: poll the file object until it has."""
        while True:
            if self.monotonic() + POLL_INTERVAL > self.client.deadline:
                raise self._out_of_time(local, "the analysis VirusTotal holds for this hash")
            self.sleep(POLL_INTERVAL)
            attributes = self.client.get_file(local)
            verdict = None if attributes is None else verdict_of(attributes, "last_analysis_stats",
                                                                 "last_analysis_date")
            if verdict is not None:
                return verdict


# --------------------------------------------------------------------------------------------------------------
# Command line
# --------------------------------------------------------------------------------------------------------------

@dataclass
class Runtime:
    """The outside world, replaceable in tests."""
    transport: object = None       # transport(method, url, headers, body, timeout) -> (status, data); default http
    environ: object = None         # mapping; default os.environ
    platform: str | None = None    # default sys.platform
    winreg: object = None          # module with OpenKey / QueryValueEx; default the real one on Windows
    sleep: object = default_sleep
    monotonic: object = default_monotonic
    clock: object = default_clock
    log: object = default_log


class Session:
    """What main() needs to scrub error text: the key, once it is known, and the key-shaped strings typed on the
    command line (a key pasted where it does not belong: the tool has no option for it, but a slip must not print)."""
    key: Secret | None = None
    typed: tuple = ()


def typed_values(argv) -> list:
    """The strings a person typed as values on the command line: the part after '=' of an --option=value token and
    every token that is not an option, at least TYPED_MIN_LENGTH characters long (no API key is shorter). Option
    names are not values, so an error message can still name them."""
    found = []
    for token in argv or ():
        token = str(token)
        value = token.partition("=")[2] if token.startswith("-") else token
        if len(value) >= TYPED_MIN_LENGTH:
            found.append(value)
    return found


def scrub_typed(text, values) -> str:
    """text with every typed value (raw, and as repr() would escape it) replaced by '***'."""
    text = str(text)
    for value in sorted(set(values), key=len, reverse=True):
        for form in (value, repr(value)[1:-1]):
            text = text.replace(form, "***")
    return text


class _Parser(argparse.ArgumentParser):
    typed: list = []  # typed_values() of the command line being parsed

    def error(self, message):  # argparse would exit with 2, which this tool reserves for "a detection"
        # argparse echoes what it rejects ("invalid float value: 'X'", "ambiguous option: --key=X ..."), and the key
        # is not known yet at parse time, so every typed value is hidden here.
        raise UsageError(f"{scrub_typed(message, self.typed)} (see --help)")


def parse_args(argv=None):
    parser = _Parser(description=__doc__.split("\n\n")[0], formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.typed = typed_values(sys.argv[1:] if argv is None else argv)
    parser.add_argument("files", nargs="*", metavar="FILE",
                        help="files to scan (default: the client and the server DLL below --dist)")
    parser.add_argument("--dist", help="distribution tree for the default DLLs (default <repo>/dist)")
    parser.add_argument("--version", help="release version (default: the agreeing csproj <Version>)")
    parser.add_argument("--record", metavar="PATH",
                        help="JSON record to write (default <repo>/work/releases/<version>/virustotal.json for the "
                             f"two release DLLs, {FILES_RECORD_NAME} next to it when FILEs are given)")
    parser.add_argument("--key-env", default=DEFAULT_KEY_ENV, metavar="NAME",
                        help=f"environment variable holding the API key (default {DEFAULT_KEY_ENV})")
    parser.add_argument("--key-file", metavar="PATH", help="file holding the API key (wins over the environment)")
    parser.add_argument("--timeout", type=float, default=60.0, metavar="SECONDS",
                        help="socket timeout per request (default 60)")
    parser.add_argument("--wait", type=float, default=600.0, metavar="SECONDS",
                        help="most time one file may take, quota pauses and polling included (default 600)")
    parser.add_argument("--force-upload", action="store_true",
                        help="upload even when VirusTotal already knows the hash")
    parser.add_argument("--dry-run", action="store_true",
                        help="hash the files and show the plan; no network call, nothing written")
    args = parser.parse_args(argv)
    if args.timeout <= 0:
        raise UsageError("--timeout must be positive")
    if args.wait < 0:
        raise UsageError("--wait must not be negative")
    if not ENV_NAME_RE.fullmatch(args.key_env):
        raise UsageError("--key-env must be an environment variable name")
    if args.version is not None:
        try:
            release.check_version(args.version)
        except release.PackagingError:  # whose message would echo the value
            raise UsageError("invalid version: --version takes a release version such as 0.2.0 "
                             "(MAJOR.MINOR.PATCH[-label]); the value is not shown") from None
    return args


def choose_files(args, repo: Path) -> tuple:
    """(paths to scan, version or None). The default list is the client and the server DLL below --dist."""
    if args.files:
        version = None
        if args.version:
            version = release.check_version(args.version)
        else:
            try:
                version = release.load_spec(repo, None).version
            except release.PackagingError:
                version = None
        paths = [Path(f) for f in args.files]
    else:
        spec = release.load_spec(repo, args.version)
        dist = Path(args.dist) if args.dist else repo / "dist"
        paths = [dist.joinpath(*rel.split("/")) for rel in (spec.client_dll, spec.server_dll)]
        version = spec.version
    if len({p.resolve() for p in paths}) != len(paths):
        raise ScanError("the same file is listed twice")
    return paths, version


def record_path_for(args, repo: Path, version) -> Path:
    """--record, else the release record of the version for the default two-DLL list. A scan of explicit FILEs
    never defaults to that name: it would replace the release's record with the verdict of a probe file."""
    if args.record:
        return Path(args.record)
    if version is None:
        raise ScanError("cannot choose the default record path without a version (no csproj <Version> found): "
                        "pass --record PATH or --version")
    return repo / "work" / "releases" / version / (FILES_RECORD_NAME if args.files else RECORD_NAME)


def find_key(args, runtime: Runtime) -> tuple:
    environ = os.environ if runtime.environ is None else runtime.environ
    platform = sys.platform if runtime.platform is None else runtime.platform
    return resolve_key(args.key_file, args.key_env, environ, platform, runtime.winreg)


def dry_run_lines(record_path: Path, files, key_text: str, force: bool) -> list:
    lines = [f"DRY RUN: no network call, nothing written. The record would be {record_path}",
             f"  API key: {key_text}"]
    for f in files:
        note = ("would be uploaded (--force-upload)" if force
                else "would be looked up by its hash and uploaded only if VirusTotal does not know it")
        if f.size > MAX_UPLOAD_BYTES:
            note += (f"; larger than the {limit_text()} upload limit, so it can only be scanned if VirusTotal "
                     "already knows it")
        lines.append(f"  {f.name}  {f.size:,} bytes  sha256 {f.sha256}  {gui_url(f.sha256)}  {note}")
    lines.append(f"  request quota: at most {QUOTA_REQUESTS} requests per minute, paced across all files")
    return lines


def run(args, repo: Path, runtime: Runtime, session: Session) -> int:
    log = runtime.log
    paths, version = choose_files(args, repo)
    record_path = record_path_for(args, repo, version)

    key_text = ""
    try:
        session.key, source = find_key(args, runtime)
        key_text = f"found ({source})"
        if not args.dry_run:
            log(f"API key: {source}")
    except ScanError as exc:
        if not args.dry_run:
            raise
        key_text = f"NOT FOUND, a real run would stop: {exc}"

    files = [local_file(path) for path in paths]
    if args.dry_run:
        for line in dry_run_lines(record_path, files, key_text, args.force_upload):
            log(line)
        return 0

    client = Client(session.key, runtime.transport or http_request, timeout=args.timeout, sleep=runtime.sleep,
                    monotonic=runtime.monotonic, log=log)
    scanner = Scanner(client, wait=args.wait, force_upload=args.force_upload, sleep=runtime.sleep,
                      monotonic=runtime.monotonic, log=log)
    results = []
    for local in files:
        log(f"scanning {local.name} - {local.size:,} bytes - sha256 {local.sha256}")
        result = scanner.scan(local)
        results.append(result)
        log(result_line(result))

    write_record(record_path, build_record(version, runtime.clock(), results))
    log(f"wrote {record_path}")
    log("")
    log("----- Markdown block (copy from here) -----")
    for line in markdown_lines(results):
        log(line)
    log("----- end of Markdown block -----")
    detections = [r for r in results if r.detected]
    if detections:
        log("")
        log(f"WARNING: {len(detections)} of {len(results)} file(s) with detections "
            f"({', '.join(r.local.name for r in detections)}). Read the engine names on the VirusTotal page before "
            "deciding: Unity/BepInEx DLLs do attract false positives, but nothing is released on a guess.")
        log(f"VIRUSTOTAL_SCAN_DETECTIONS {len(detections)}")
        return 2
    log(f"VIRUSTOTAL_SCAN_CLEAN {version or 'unversioned'}")
    return 0


def main(argv=None, repo: Path = REPO, runtime: Runtime | None = None) -> int:
    runtime = runtime or Runtime()
    session = Session()
    argv = sys.argv[1:] if argv is None else list(argv)
    session.typed = tuple(v for v in typed_values(argv) if KEYLIKE_RE.fullmatch(v))
    try:
        args = parse_args(argv)
        return run(args, repo, runtime, session)
    except (ScanError, release.PackagingError, OSError) as exc:
        print(f"ERROR: {_clean(scrub_typed(exc, session.typed), session.key, limit=2000)}", file=sys.stderr)
        return 1
    except Exception as exc:  # noqa: BLE001 - never a traceback: it could carry the key; type and message instead
        print(f"ERROR: unexpected {type(exc).__name__}: "
              f"{_clean(scrub_typed(exc, session.typed), session.key, limit=2000)}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(errors="replace")  # a legacy console codepage must not turn a file name into a traceback
        except (AttributeError, ValueError):
            pass
    sys.exit(main())
