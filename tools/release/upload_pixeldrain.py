#!/usr/bin/env python3
"""Publish a finished release (tools/release/make_release_zips.py output) to Pixeldrain and record the links.

The release directory work/releases/<version>/ holds the single ZIP maschine-ArenaMapPort-<version>.zip and its
maschine-ArenaMapPort-<version>.sha256. This tool verifies that pair (make_release_zips.verify_set), uploads both
files, checks what Pixeldrain stored against the local SHA-256 and size, groups them in a Pixeldrain list (album),
writes the upload record and prints a ready-to-paste Markdown block with the download links and the VirusTotal
links of the two DLLs. Stdlib only; nothing is ever deleted on Pixeldrain (a bad upload is reported with its id
for removal in the web interface).

Pixeldrain API (https://pixeldrain.com/api), everything over HTTPS to pixeldrain.com:

    GET  /api/user               key check before the first upload (see below); 401 = key rejected
    PUT  /api/file/{name}        raw file bytes as the request body (name URL-encoded, at most 255 characters,
                                 Content-Length set, streamed from disk in 1 MiB blocks); 201 {"success": true,
                                 "id": ...}. There is no resumable upload: a failed upload restarts at byte 0.
    GET  /api/file/{id}/info     {"id", "name", "size", "hash_sha256", ...}; the stored size and SHA-256 must equal
                                 the local ones.
    POST /api/list               {"title": <at most 300 characters>, "anonymous": false, "files": [{"id",
                                 "description"}, ...]}; 201 {"success": true, "id": <list id>}.

    page https://pixeldrain.com/u/{id}   direct https://pixeldrain.com/api/file/{id}?download
    list https://pixeldrain.com/l/{id}

Errors are JSON {"success": false, "value": <code>, "message": ...}. 401 means the key was rejected, 413
file_too_large. Network errors (URLError, other OSError, timeouts, http.client errors) and 5xx answers are retried
--retries times (default 3, so up to four attempts) with a doubling backoff of 1, 2, 4 ... s; any 4xx is final.
Two exceptions protect against stored duplicates: a file upload whose body was sent completely and then got no
answer within --timeout is NOT retried (the file may be stored; the message says so), and an upload is written to
the record the moment Pixeldrain returns its id, before the verification below, so a failing verification never
orphans a complete 2 GiB upload.

Key check. A server that refuses an upload (401, 413) may answer right after the headers and hang up while the file
is still being sent; Windows then resets the connection and discards the answer, so the refusal would surface as a
network error and be retried. Before the first PUT of a run the tool therefore sends one small GET /api/user:
401 stops the run with "Pixeldrain rejected the API key", any other 4xx is noted and ignored (the call is only a
convenience), and a per-file size limit that the answer reports (subscription.file_size_limit, when present) is
compared with the files with a warning. If a PUT still ends in a reset or hang-up, the final error says that this
can hide a 401 or 413.

Authentication is HTTP Basic with an EMPTY user name and the API key as the password. The key is resolved in this
order: --key-file PATH (content stripped, BOM tolerated); else the environment variable named by --key-env
(default PIXELDRAIN_API_KEY); else, on Windows only, that variable from the registry, User scope
(HKCU\\Environment) and then Machine scope (HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment),
because a shell started before the variable was set does not see it. A missing key is an error that names the
three sources. The key lives in a Secret wrapper whose repr and str are '***'; it appears only in the Authorization
header, never in stdout, stderr, log lines, error messages (redacted, also in text a server or the network stack
sends back), the record or the tests.

Flow for a release: (1) make_release_zips.verify_set unless --skip-verify (every ZIP to upload must belong to the
verified set); (2) SHA-256 and size of each file; a file whose name+size+SHA-256 the record already lists (under
"files" or "other_files") is reused when GET info confirms the same hash (unless --force); (3) key check, then PUT;
(4) GET info and compare; (5) POST the list, or reuse a recorded list that covers exactly these file ids under the
same title (--no-list creates none, and keeps a recorded list that still covers exactly these files); (6) write the
record; (7) print the Markdown block. --dry-run runs the local steps (verification, hashing, key lookup) and prints
what would happen: no network, no writes. --test-file PATH uploads one arbitrary file for a connectivity test: no
release checks, no list unless --title is given, no VirusTotal block. Its record is the same pixeldrain.json in the
same directory (version from --version or the csproj, "test" directory when neither exists), marked "test": true;
a test run refuses to replace a release record (one without that mark): give it another --release-dir.

Record, work/releases/<version>/pixeldrain.json (written atomically via .partial; work/ is git-ignored):

    {"schema": 1, "version": "0.1.0", "source_commit": "<40 hex, or '<commit>+dirty', or null>",
     "uploaded_utc": "2026-10-15T12:00:00Z",
     "files": [{"name", "size", "sha256", "id", "url", "direct_url"}, ...],
     "list": {"id", "url", "title"} | null,
     "other_files": [<file entries>],                              (only when there are any)
     "other_lists": [{"id", "url", "title", "file_ids": [...]}],  (only when there are any)
     "test": true}                                                (only for --test-file)

"files" and "list" describe exactly the latest run: its files in the order given and the one list that covers
exactly them (null when there is none). Nothing a run stops tracking is forgotten: "other_files" keeps entries of
files the run did not mention (a --files subset), earlier versions of a changed file, forced re-uploads, and uploads
that completed but are not verified yet; "other_lists" keeps every list that no longer matches (another title or
selection, re-uploaded files), with the file ids it covers so that it can be reused later. A later run reuses a
matching entry after GET info confirms its hash. source_commit is read from the manifest inside the ZIP (null for
--test-file); uploaded_utc is the time of the last actual upload and does not move when everything was reused. A
record that cannot be read is an error, never overwritten silently.

VirusTotal pages https://www.virustotal.com/gui/file/<sha256> are built from the two DLLs in dist/ (client and
server paths from make_release_zips.load_spec). They exist only after somebody has uploaded each DLL to
VirusTotal once; the tool says so, and warns if a dist DLL differs from the one inside the ZIP.

Exit status 0 on success, 1 on any error (message on stderr), 2 on a usage error.
"""
from __future__ import annotations

import argparse
import base64
import http.client
import json
import os
import re
import socket
import ssl
import sys
import time
import urllib.error
import urllib.parse
import zipfile
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import make_release_zips as release  # noqa: E402

HOST = "pixeldrain.com"
SITE = f"https://{HOST}"
API = f"{SITE}/api"
VIRUSTOTAL_FILE = "https://www.virustotal.com/gui/file/"
DEFAULT_KEY_ENV = "PIXELDRAIN_API_KEY"
RECORD_NAME = "pixeldrain.json"
RECORD_SCHEMA = 1
USER_AGENT = "maschine-ArenaMapPort-release-uploader/1"

BLOCK = 1024 * 1024                # bytes per read of the upload body (and http.client's send block size)
PROGRESS_BYTES = 64 * 1024 * 1024  # one progress line per this many bytes sent
MAX_NAME = 255                     # Pixeldrain's file name limit
MAX_TITLE = 300                    # Pixeldrain's list title limit
HASH_POLLS = 3                     # extra GET info calls while a fresh upload does not report hash_sha256 yet
HASH_POLL_DELAY = 2.0
MAX_BACKOFF = 8.0
ERROR_TEXT_LIMIT = 300
HUNG_UP_HINT = ("; a server that refuses an upload (HTTP 401 for a rejected key, HTTP 413 for a file over the "
                "account's size limit) may hang up while the file is still being sent, and this system then loses "
                "its answer: check the key and the account's per-file size limit")

ID_RE = re.compile(r"[A-Za-z0-9_-]{1,64}")
KEY_RE = re.compile(r"[\x21-\x7e]+")  # printable ASCII without blanks: all an API key can be, and header-safe
ENV_NAME_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")

# (scope, winreg hive attribute, sub key, text shown to the user)
REGISTRY_SCOPES = (
    ("User", "HKEY_CURRENT_USER", "Environment", "HKCU\\Environment"),
    ("Machine", "HKEY_LOCAL_MACHINE", r"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
     "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment"),
)


class UploadError(Exception):
    """Any refusal: bad input, missing key, Pixeldrain rejection, failed verification."""


class MissingKeyError(UploadError):
    pass


class ApiError(UploadError):
    def __init__(self, message, status=None):
        super().__init__(message)
        self.status = status


class AnswerTimeout(OSError):
    """The request was sent completely, but Pixeldrain's answer did not arrive within the socket timeout. Raised by
    the transport so that Api.call can tell it from a failure while sending: for a file upload it is not retried,
    because the file may well be stored already."""


def default_log(message: str = "") -> None:
    print(message, flush=True)


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


def _b64(text: str) -> str:
    return base64.b64encode(text.encode("ascii")).decode("ascii")


def auth_header(key) -> str:
    """'Basic ' + base64(':' + key): Pixeldrain wants an empty user name and the key as the password."""
    raw = key.reveal() if isinstance(key, Secret) else key
    return "Basic " + _b64(":" + raw)


def redact(text, key) -> str:
    """text with every form of the key (raw, base64 of key or ':key', the whole header value) replaced by '***'."""
    text = str(text)
    if key is None:
        return text
    raw = key.reveal() if isinstance(key, Secret) else key
    if not raw:
        return text
    for form in (auth_header(raw), _b64(":" + raw), _b64(raw), raw):
        text = text.replace(form, "***")
    return text


def _clean(text, key, limit: int = ERROR_TEXT_LIMIT) -> str:
    """Printable, key-free, length-limited text: server answers and exception messages are data."""
    text = re.sub(r"[\x00-\x1f\x7f]+", " ", redact(text, key)).strip()
    return text if len(text) <= limit else text[:limit] + "..."


def _validated_key(raw: str, source: str) -> Secret:
    raw = raw.strip()
    if not raw:
        raise UploadError(f"the API key from {source} is empty")
    if not KEY_RE.fullmatch(raw):
        raise UploadError(f"the API key from {source} contains whitespace or non-ASCII characters, which no "
                          "Pixeldrain key has (the value is not shown); save it as plain ASCII text")
    return Secret(raw)


def read_key_file(path: Path) -> str:
    try:
        return Path(path).read_text(encoding="utf-8-sig")
    except UnicodeDecodeError as exc:
        raise UploadError(f"--key-file {path} is not UTF-8/ASCII text (a UTF-16 file from PowerShell 5 '>' "
                          "redirection?); save it as plain ASCII") from exc
    except OSError as exc:
        raise UploadError(f"cannot read --key-file {path}: {exc.strerror or type(exc).__name__}") from exc


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
    return (f"no Pixeldrain API key found. Looked in three places, in this order: (1) --key-file PATH (not given); "
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
# HTTP transport and API client
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
    """The one place that touches the network: returns (status, parsed JSON | text | None). `body` is None, bytes
    or a file-like object (read(n)); Content-Length is in `headers` whenever there is a body. Only
    https://pixeldrain.com is ever contacted, so the Authorization header cannot be sent anywhere else."""
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "https" or parts.hostname != HOST:
        raise UploadError(f"refusing to contact {parts.scheme}://{parts.hostname}: only https://{HOST} is allowed")
    connection = http.client.HTTPSConnection(parts.hostname, parts.port or 443, timeout=timeout,
                                             context=ssl.create_default_context(), blocksize=BLOCK)
    target = parts.path + (f"?{parts.query}" if parts.query else "")
    try:
        try:
            connection.request(method, target, body=body, headers=headers)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError) as exc:
            # The server may have answered (413, 401 ...) and hung up before it read the whole body. On Windows the
            # reset usually discards that answer, so this salvage often fails: Api.check_account finds a bad key
            # before the upload, and Api.call words the final error accordingly.
            try:
                response = connection.getresponse()
                return response.status, _parse_body(response.read(1 << 20))
            except Exception:  # noqa: BLE001 - report the original send failure
                raise exc from None
        try:
            response = connection.getresponse()
            return response.status, _parse_body(response.read(1 << 20))
        except (TimeoutError, socket.timeout) as exc:
            raise AnswerTimeout(f"no answer within {timeout:g} s after the request was sent completely") from exc
    finally:
        connection.close()


class ProgressFile:
    """File-like upload body: reads at most `size` bytes from disk and logs one line per PROGRESS_BYTES sent.
    A new instance per attempt, so a retry restarts at byte 0."""

    def __init__(self, path: Path, size: int, label: str, log):
        self._stream = open(path, "rb")
        self.size = size
        self.sent = 0
        self._label = label
        self._log = log
        self._next_mark = PROGRESS_BYTES

    def read(self, amount: int = -1) -> bytes:
        remaining = self.size - self.sent
        if remaining <= 0:
            return b""
        amount = remaining if amount is None or amount < 0 else min(amount, remaining)
        data = self._stream.read(amount)
        if not data:  # the file shrank after it was hashed: fail now, not after the server's read timeout
            raise UploadError(f"{self._label} became shorter while it was being uploaded "
                              f"({self.sent} of {self.size} bytes); run again once the file is stable")
        self.sent += len(data)
        while self.sent >= self._next_mark:
            self._log(f"  {self._label}: sent {self._next_mark // 2 ** 20:,} MiB of {self.size / 2 ** 20:,.0f} MiB "
                      f"({100 * self.sent // max(self.size, 1)} %)")
            self._next_mark += PROGRESS_BYTES
        return data

    def close(self) -> None:
        self._stream.close()


class Api:
    """Pixeldrain calls over an injected transport: authentication, retries, error mapping. `transport` is
    transport(method, url, headers, body, timeout) -> (status, json | text | None)."""

    def __init__(self, key: Secret, transport, *, timeout: float, retries: int, sleep, log):
        self.key = key
        self.transport = transport
        self.timeout = timeout
        self.retries = retries
        self.sleep = sleep
        self.log = log
        self.checked = False       # check_account ran
        self.size_limit = None     # per-file limit in bytes that GET /api/user reported, if any

    def __repr__(self) -> str:
        return "Api(key=***)"

    def call(self, method: str, url: str, *, payload=None, body_factory=None, length=None, allow=(), label=None):
        """(status, data). Non-2xx raises ApiError unless the status is in `allow`; a 2xx answer with
        success=false raises too. Network errors and 5xx are retried, 4xx is final."""
        headers = {"Authorization": auth_header(self.key), "Accept": "application/json", "User-Agent": USER_AGENT}
        encoded = None
        if payload is not None:
            encoded = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = "application/json"
            headers["Content-Length"] = str(len(encoded))
        elif body_factory is not None:
            headers["Content-Type"] = "application/octet-stream"
            headers["Content-Length"] = str(length)
        label = label or f"{method} {urllib.parse.urlsplit(url).path}"
        attempt = 0
        while True:
            attempt += 1
            body = body_factory() if body_factory is not None else encoded
            problem = None
            hung_up = False
            try:
                status, data = self.transport(method, url, dict(headers), body, self.timeout)
            except urllib.error.HTTPError as exc:  # a urllib-based transport reports 4xx/5xx this way
                status, data = exc.code, None
            except UploadError:
                raise
            except AnswerTimeout as exc:
                if body_factory is None:  # a read or the small list request is safe to repeat
                    problem = f"network error {type(exc).__name__}: {_clean(exc, self.key)}"
                else:
                    raise ApiError(f"{label}: the whole file was sent but Pixeldrain did not answer within "
                                   f"{self.timeout:g} s. It may be stored already, so this is not retried (a retry "
                                   "could store a second copy): look at your file list on pixeldrain.com before "
                                   "running again, and use a larger --timeout when the answer is merely slow") from None
            except (OSError, http.client.HTTPException) as exc:
                problem = f"network error {type(exc).__name__}: {_clean(exc, self.key)}"
                hung_up = isinstance(exc, (BrokenPipeError, ConnectionResetError, ConnectionAbortedError))
            finally:
                if body is not None and hasattr(body, "close"):
                    body.close()
            if problem is None and status >= 500:
                problem = f"HTTP {status}" + self._detail(data)
            if problem is None:
                break
            if attempt > self.retries:
                if hung_up and body_factory is not None:
                    problem += HUNG_UP_HINT
                raise ApiError(f"{label} failed after {attempt} attempt(s): {problem}")
            delay = min(2.0 ** (attempt - 1), MAX_BACKOFF)
            self.log(f"  {label}: {problem}; retry {attempt} of {self.retries} in {delay:g} s")
            self.sleep(delay)
        if 200 <= status < 300:
            if isinstance(data, dict) and data.get("success") is False:
                raise ApiError(f"{label}: Pixeldrain answered HTTP {status} but reported failure"
                               + self._detail(data), status)
            return status, data
        if status in allow:
            return status, data
        raise ApiError(f"{label}: {self._failure(status, data)}", status)

    def _detail(self, data) -> str:
        if isinstance(data, dict):
            pieces = [str(p) for p in (data.get("value"), data.get("message")) if p]
            if pieces:
                return ": " + _clean(" - ".join(pieces), self.key)
        elif isinstance(data, str) and data.strip():
            return ": " + _clean(data, self.key)
        return ""

    def _failure(self, status: int, data) -> str:
        if status == 401:
            return "Pixeldrain rejected the API key (HTTP 401)" + self._detail(data)
        if status == 413:
            return "the file is too large for this account (HTTP 413 file_too_large)" + self._detail(data)
        return f"HTTP {status}" + self._detail(data)

    # -- endpoints ---------------------------------------------------------------------------------------------

    def check_account(self) -> None:
        """One small authenticated GET before the first upload of a run (see the module docstring, "Key check"):
        401 raises, any other 4xx is noted and ignored, a reported per-file size limit is remembered."""
        if self.checked:
            return
        self.checked = True
        try:
            _status, data = self.call("GET", f"{API}/user", label="key check")
        except ApiError as exc:
            if exc.status is None or exc.status == 401:
                raise
            self.log(f"  key check: Pixeldrain answered HTTP {exc.status} to GET /api/user; going on without it")
            return
        plan = data.get("subscription") if isinstance(data, dict) else None
        limit = plan.get("file_size_limit") if isinstance(plan, dict) else None
        if isinstance(limit, int) and not isinstance(limit, bool) and limit > 0:
            self.size_limit = limit
        self.log("  key check: Pixeldrain accepted the API key")

    def put_file(self, local: "LocalFile") -> str:
        """Upload `local` streaming from disk; returns the new file id."""
        self.check_account()
        if self.size_limit is not None and local.size > self.size_limit:
            self.log(f"WARNING: {local.name} is {size_text(local.size)}, more than the {size_text(self.size_limit)} "
                     "per-file limit Pixeldrain reports for this account; expect HTTP 413 file_too_large")
        url = f"{API}/file/{urllib.parse.quote(local.name, safe='')}"
        started = time.monotonic()
        _status, data = self.call(
            "PUT", url, body_factory=lambda: ProgressFile(local.path, local.size, local.name, self.log),
            length=local.size, label=f"upload {local.name}")
        file_id = data.get("id") if isinstance(data, dict) else None
        if not isinstance(file_id, str) or not ID_RE.fullmatch(file_id):
            raise ApiError(f"upload {local.name}: Pixeldrain's answer carries no usable file id")
        seconds = max(time.monotonic() - started, 0.001)
        self.log(f"  {local.name}: upload finished, {local.size / 2 ** 20:,.1f} MiB in {seconds:,.0f} s "
                 f"({local.size / 2 ** 20 / seconds:,.1f} MiB/s), id {file_id}")
        return file_id

    def file_info(self, file_id: str, *, allow_missing: bool = False):
        """The info dict, or None for a 404 when `allow_missing`."""
        status, data = self.call("GET", f"{API}/file/{file_id}/info", allow=(404,) if allow_missing else (),
                                 label=f"info {file_id}")
        if status == 404:
            return None
        if not isinstance(data, dict):
            raise ApiError(f"info {file_id}: Pixeldrain's answer is not a JSON object")
        return data

    def create_list(self, title: str, entries: list) -> str:
        payload = {"title": title, "anonymous": False,
                   "files": [{"id": e["id"], "description": e["name"]} for e in entries]}
        _status, data = self.call("POST", f"{API}/list", payload=payload, label="create list")
        list_id = data.get("id") if isinstance(data, dict) else None
        if not isinstance(list_id, str) or not ID_RE.fullmatch(list_id):
            raise ApiError("create list: Pixeldrain's answer carries no usable list id")
        return list_id


# --------------------------------------------------------------------------------------------------------------
# Files, record, links
# --------------------------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class LocalFile:
    name: str
    path: Path
    size: int
    sha256: str


def file_url(file_id: str) -> str:
    return f"{SITE}/u/{file_id}"


def direct_url(file_id: str) -> str:
    return f"{API}/file/{file_id}?download"


def list_url(list_id: str) -> str:
    return f"{SITE}/l/{list_id}"


def check_upload_name(name: str) -> str:
    if (not name or len(name) > MAX_NAME or "/" in name or "\\" in name or name in (".", "..")
            or name != name.strip() or any(ord(c) < 32 for c in name)):
        raise UploadError(f"unusable file name {name!r} (a plain name of at most {MAX_NAME} characters)")
    return name


def local_file(path: Path) -> LocalFile:
    """Name, size and SHA-256 of a regular file (read-only)."""
    path = Path(path)
    if not path.is_file():
        raise UploadError(f"file not found: {path}")
    try:
        size = path.stat().st_size
        digest = release.sha256_file(path)
    except OSError as exc:
        raise UploadError(f"cannot read {path}: {exc}") from exc
    return LocalFile(check_upload_name(path.name), path, size, digest)


def size_text(size: int) -> str:
    unit = f"{size / 2 ** 30:.2f} GiB" if size >= 2 ** 30 else f"{size / 2 ** 20:.2f} MiB"
    return f"{size:,} bytes ({unit})"


def utc_text(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def file_entry(local: LocalFile, file_id: str) -> dict:
    return {"name": local.name, "size": local.size, "sha256": local.sha256, "id": file_id,
            "url": file_url(file_id), "direct_url": direct_url(file_id)}


def _valid_entry(entry) -> bool:
    return (isinstance(entry, dict) and isinstance(entry.get("name"), str) and isinstance(entry.get("size"), int)
            and isinstance(entry.get("sha256"), str) and isinstance(entry.get("id"), str)
            and ID_RE.fullmatch(entry["id"]) is not None)


def _valid_list(item, with_ids: bool) -> bool:
    return (isinstance(item, dict) and isinstance(item.get("id"), str) and isinstance(item.get("title"), str)
            and ID_RE.fullmatch(item["id"]) is not None
            and (not with_ids or (isinstance(item.get("file_ids"), list)
                                  and all(isinstance(i, str) and ID_RE.fullmatch(i) for i in item["file_ids"]))))


def load_record(path: Path):
    """The existing record, or None when there is none. An unreadable or foreign record is an error: it holds ids
    that exist nowhere else, so it is never overwritten silently."""
    if not path.exists():
        return None
    try:
        record = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise UploadError(f"cannot read the upload record {path}: {exc}; move it away to start a new one") from exc
    problem = None
    if not isinstance(record, dict) or record.get("schema") != RECORD_SCHEMA:
        problem = "not a schema-1 record"
    elif not isinstance(record.get("files"), list) or not all(_valid_entry(e) for e in record["files"]):
        problem = "malformed files list"
    elif not isinstance(record.get("other_files", []), list) or not all(_valid_entry(e)
                                                                        for e in record.get("other_files", [])):
        problem = "malformed other_files list"
    elif record.get("list") is not None and not _valid_list(record["list"], with_ids=False):
        problem = "malformed list entry"
    elif not isinstance(record.get("other_lists", []), list) or not all(_valid_list(i, with_ids=True)
                                                                        for i in record.get("other_lists", [])):
        problem = "malformed other_lists list"
    if problem:
        raise UploadError(f"the upload record {path} is unusable ({problem}); move it away to start a new one")
    return record


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


def normalized(entry: dict) -> dict:
    """A recorded file entry in the canonical shape (links recomputed from the id)."""
    return {"name": entry["name"], "size": entry["size"], "sha256": entry["sha256"], "id": entry["id"],
            "url": file_url(entry["id"]), "direct_url": direct_url(entry["id"])}


def recorded_files(previous) -> list:
    """Every file entry the record knows: the latest run's first, then `other_files`."""
    return list((previous or {}).get("files", [])) + list((previous or {}).get("other_files", []))


def find_recorded(previous, local: LocalFile):
    for entry in recorded_files(previous):
        if entry["name"] == local.name and entry["sha256"] == local.sha256 and entry["size"] == local.size:
            return entry
    return None


def recorded_lists(previous) -> list:
    """Every list the record knows as {id, url, title, file_ids}: `list` (which covers exactly the record's
    `files`) first, then `other_lists`."""
    found = []
    current = (previous or {}).get("list")
    if current:
        found.append({"id": current["id"], "url": list_url(current["id"]), "title": current["title"],
                      "file_ids": sorted(e["id"] for e in previous["files"])})
    for item in (previous or {}).get("other_lists", []):
        found.append({"id": item["id"], "url": list_url(item["id"]), "title": item["title"],
                      "file_ids": sorted(item["file_ids"])})
    return found


def settle_lists(previous, files: list, title: str, make_list: bool) -> tuple:
    """(the recorded list that covers exactly `files`, or None; every other recorded list). When a list is wanted
    (`make_list`) the match must also carry `title`, since a list cannot be renamed; with --no-list any list that
    covers exactly these files stays the record's list. No recorded list is ever dropped."""
    wanted = sorted(e["id"] for e in files)
    match, others, seen = None, [], set()
    for item in recorded_lists(previous):
        if item["id"] in seen:
            continue
        seen.add(item["id"])
        if match is None and item["file_ids"] == wanted and (not make_list or item["title"] == title):
            match = {"id": item["id"], "url": item["url"], "title": item["title"]}
        else:
            others.append(item)
    return match, others


def assemble_record(previous, *, version, source_commit, stamp, files: list, pending=(), listed=None, others=(),
                    testing: bool = False) -> dict:
    """The record for `files` (the latest run) with everything else the old record knew moved to other_files /
    other_lists. `pending` are uploads that completed but are not verified yet."""
    taken = {e["id"] for e in files}
    spare, seen = [], set()
    for entry in [*pending, *recorded_files(previous)]:
        if entry["id"] not in taken and entry["id"] not in seen:
            seen.add(entry["id"])
            spare.append(normalized(entry))
    record = {"schema": RECORD_SCHEMA, "version": version, "source_commit": source_commit, "uploaded_utc": stamp,
              "files": [normalized(e) for e in files], "list": listed}
    if spare:
        record["other_files"] = spare
    if others:
        record["other_lists"] = list(others)
    if testing:
        record["test"] = True
    return record


NO_HASH = "no hash_sha256 reported"


def info_problems(info: dict, local: LocalFile) -> list:
    problems = []
    if info.get("size") != local.size:
        problems.append(f"size {info.get('size')!r} instead of {local.size}")
    digest = info.get("hash_sha256")
    if not isinstance(digest, str) or not digest:
        problems.append(NO_HASH)
    elif digest.lower() != local.sha256:
        problems.append(f"SHA-256 {digest.lower()} instead of {local.sha256}")
    return problems


def fetch_info(api: Api, file_id: str, *, allow_missing: bool = False):
    """GET info; a missing hash may only mean it is not computed yet, so that case alone is polled a few times.
    None for a 404 when `allow_missing`."""
    info = None
    for poll in range(HASH_POLLS + 1):
        info = api.file_info(file_id, allow_missing=allow_missing)
        if info is None or (isinstance(info.get("hash_sha256"), str) and info["hash_sha256"]) or poll == HASH_POLLS:
            break
        api.sleep(HASH_POLL_DELAY)
    return info


def verify_remote(api: Api, file_id: str, local: LocalFile) -> None:
    """GET info of a fresh upload and require size and SHA-256 to match. Raises with the id; nothing is deleted."""
    problems = info_problems(fetch_info(api, file_id), local)
    if problems == [NO_HASH]:
        raise UploadError(f"{local.name} was stored as {file_id} ({file_url(file_id)}) but Pixeldrain reports no "
                          "hash_sha256 for it, so the upload cannot be confirmed yet. It is in the upload record: "
                          "run again later and it is verified and reused instead of being uploaded a second time.")
    if problems:
        raise UploadError(f"{local.name} was stored as {file_id} ({file_url(file_id)}) but Pixeldrain reports "
                          f"{'; '.join(problems)}. The file stays on Pixeldrain (this tool never deletes anything): "
                          "remove it in the web interface, then run again.")


# --------------------------------------------------------------------------------------------------------------
# Upload flow
# --------------------------------------------------------------------------------------------------------------

def run_upload(api: Api, files: list, *, record_path: Path, previous, version, source_commit, title,
               make_list: bool, force: bool, clock, log, testing: bool = False) -> dict:
    """Upload (or reuse) every file, verify it remotely, group the files in a list and write the record."""
    entries = []
    names = [f.name for f in files]
    stamp = (previous or {}).get("uploaded_utc") or utc_text(clock())

    def checkpoint(pending=()) -> None:
        """Record the files finished so far plus the old entries of those still to come, and the `pending`
        upload that is not verified yet, so that a run which dies later neither repeats an upload nor loses
        a link."""
        waiting = [e for e in (previous or {}).get("files", []) if e["name"] in names[len(entries):]]
        combined = entries + waiting
        matched, others = settle_lists(previous, combined, title, make_list)
        write_record(record_path, assemble_record(previous, version=version, source_commit=source_commit,
                                                  stamp=stamp, files=combined, pending=pending, listed=matched,
                                                  others=others, testing=testing))

    for local in files:
        entry = None
        recorded = None if force else find_recorded(previous, local)
        if recorded is not None:
            info = fetch_info(api, recorded["id"], allow_missing=True)
            problems = [] if info is None else info_problems(info, local)
            if info is None:
                log(f"  {local.name}: the recorded file {recorded['id']} is gone from Pixeldrain; uploading again")
            elif problems == [NO_HASH]:
                raise UploadError(f"{local.name}: Pixeldrain reports no hash_sha256 for the recorded file "
                                  f"{recorded['id']} yet, so it cannot be confirmed. Run again in a few minutes, "
                                  "or use --force to upload it again.")
            elif problems:
                log(f"  {local.name}: the recorded file {recorded['id']} differs ({'; '.join(problems)}); "
                    "uploading again")
            else:
                entry = file_entry(local, recorded["id"])
                log(f"  {local.name}: already on Pixeldrain as {recorded['id']} with the same SHA-256; reusing")
        uploaded = entry is None
        if uploaded:
            log(f"uploading {local.name} - {size_text(local.size)}")
            entry = file_entry(local, api.put_file(local))
            checkpoint(pending=[entry])  # recorded before it is verified: a failing check must not orphan it
            try:
                verify_remote(api, entry["id"], local)
            except ApiError as exc:  # the check itself failed (5xx, network): the upload is fine as far as we know
                raise ApiError(f"{exc} - the upload itself is stored as {entry['id']} ({file_url(entry['id'])}) and "
                               f"listed in {record_path.name}: run again and it is verified and reused instead of "
                               "uploaded a second time", exc.status) from exc
            log(f"  {local.name}: Pixeldrain's size and SHA-256 match the local file")
            stamp = utc_text(clock())
        entries.append(entry)
        if uploaded:
            checkpoint()

    matched, others = settle_lists(previous, entries, title, make_list)
    listed = matched
    if matched is not None:
        log(f"list {matched['id']} already covers exactly these files; reusing")
    elif make_list:
        list_id = api.create_list(title, entries)
        stamp = utc_text(clock())
        log(f"created list {list_id}")
        listed = {"id": list_id, "url": list_url(list_id), "title": title}
    record = assemble_record(previous, version=version, source_commit=source_commit, stamp=stamp, files=entries,
                             listed=listed, others=others, testing=testing)
    if record == previous:
        log(f"upload record unchanged: {record_path}")
    else:
        write_record(record_path, record)
        log(f"wrote {record_path}")
    return record


# --------------------------------------------------------------------------------------------------------------
# VirusTotal links and the Markdown block
# --------------------------------------------------------------------------------------------------------------

def virustotal_links(dist: Path, spec) -> list:
    """For the client and server DLL below dist: name, path, SHA-256 (None when the file is missing) and the
    VirusTotal file page of that hash."""
    links = []
    for role, rel in (("client", spec.client_dll), ("server", spec.server_dll)):
        path = Path(dist).joinpath(*rel.split("/"))
        try:
            digest = release.sha256_file(path) if path.is_file() else None
        except OSError:
            digest = None
        links.append({"role": role, "name": rel.rsplit("/", 1)[-1], "path": rel, "sha256": digest,
                      "url": VIRUSTOTAL_FILE + digest if digest else None})
    return links


def manifest_hashes(manifests) -> dict:
    """Path -> SHA-256 of every file entry the ZIP manifests list."""
    hashes = {}
    for manifest in manifests:
        for entry in manifest.get("entries", []):
            if isinstance(entry, dict) and "path" in entry and "sha256" in entry:
                hashes[entry["path"]] = entry["sha256"]
    return hashes


def read_manifest(zip_path: Path):
    """The part manifest inside a release ZIP without reading the other entries, or None."""
    try:
        with zipfile.ZipFile(zip_path) as archive:
            found = [n for n in archive.namelist() if release.MANIFEST_PATH_RE.fullmatch(n)]
            if len(found) != 1:
                return None
            manifest = json.loads(archive.read(found[0]).decode("utf-8"))
    except (OSError, ValueError, zipfile.BadZipFile, KeyError):
        return None
    return manifest if isinstance(manifest, dict) else None


def markdown_block(record: dict, title: str, links) -> list:
    lines = [f"### {title}", ""]
    listed = record.get("list")
    if listed:
        lines += [f"Download everything: [{listed['title']}]({listed['url']})", ""]
    for entry in record["files"]:
        lines += [f"- `{entry['name']}` - {size_text(entry['size'])}",
                  f"  - [Download page]({entry['url']})",
                  f"  - [Direct download]({entry['direct_url']})",
                  f"  - SHA-256: `{entry['sha256']}`"]
    if links:
        lines += ["", "VirusTotal (plugin and server DLL):"]
        for link in links:
            lines.append(f"- [{link['name']}]({link['url']})" if link["url"]
                         else f"- {link['name']}: not found in dist ({link['path']})")
    return lines


def print_summary(record: dict, title: str, links, log, warnings=()) -> None:
    log("")
    log("----- Markdown block (copy from here) -----")
    for line in markdown_block(record, title, links):
        log(line)
    log("----- end of Markdown block -----")
    if links:
        log("")
        log("Note: the VirusTotal pages above exist only after somebody has uploaded each DLL to virustotal.com "
            "once; until then they show 'not found'. Upload the two DLLs from dist there, then paste the block.")
    for message in warnings:
        log(f"WARNING: {message}")


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
    sleep: object = time.sleep
    clock: object = lambda: datetime.now(timezone.utc)  # noqa: E731
    log: object = default_log


class Session:
    """What main() needs to scrub error text: the key, once it is known."""
    key: Secret | None = None


@dataclass
class Job:
    testing: bool
    version: str | None
    spec: object
    release_dir: Path
    record_path: Path
    names: list
    title: str | None
    make_list: bool


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0],
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--release-dir", help="directory with the release files (default "
                                              "<repo>/work/releases/<version>)")
    parser.add_argument("--version", help="release version (default: the agreeing csproj <Version>)")
    parser.add_argument("--dist", help="distribution tree for the VirusTotal links (default <repo>/dist)")
    parser.add_argument("--files", nargs="+", metavar="NAME",
                        help="file names inside the release directory (default: the single ZIP and its .sha256)")
    parser.add_argument("--title", help="list title (default 'ArenaMapPort <version>')")
    parser.add_argument("--no-list", action="store_true", help="do not create a Pixeldrain list")
    parser.add_argument("--dry-run", action="store_true",
                        help="print what would be uploaded; no network, nothing written")
    parser.add_argument("--force", action="store_true",
                        help="upload again even when the record says the same file is already up")
    parser.add_argument("--skip-verify", action="store_true", help="skip make_release_zips.verify_set")
    parser.add_argument("--test-file", metavar="PATH",
                        help="upload one arbitrary file as a connectivity test (no release checks; its record is "
                             "marked \"test\" and never replaces a release record)")
    parser.add_argument("--key-env", default=DEFAULT_KEY_ENV, metavar="NAME",
                        help=f"environment variable holding the API key (default {DEFAULT_KEY_ENV})")
    parser.add_argument("--key-file", metavar="PATH", help="file holding the API key (wins over the environment)")
    parser.add_argument("--timeout", type=float, default=120.0, metavar="SECONDS",
                        help="socket timeout, also the wait for Pixeldrain's answer after the last byte (default 120). "
                             "A stall while sending restarts the upload; no answer after the last byte stops the "
                             "run instead (the file may be stored), so raise this for big files on a slow link")
    parser.add_argument("--retries", type=int, default=3, metavar="N",
                        help="retries after a network error or 5xx answer (default 3)")
    args = parser.parse_args(argv)
    if args.test_file and args.files:
        parser.error("--test-file and --files exclude each other")
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    if args.retries < 0:
        parser.error("--retries must not be negative")
    if not ENV_NAME_RE.fullmatch(args.key_env):
        parser.error("--key-env must be an environment variable name")
    return args


def test_version(args, repo: Path):
    """The version a --test-file run files its record under: --version, else the agreeing csproj <Version>, else
    None (no repository around: the record then goes to work/releases/test)."""
    if args.version:
        return release.check_version(args.version)
    try:
        return release.load_spec(repo, None).version
    except release.PackagingError:
        return None


def make_job(args, repo: Path) -> Job:
    if args.test_file:
        version = test_version(args, repo)
        release_dir = Path(args.release_dir) if args.release_dir else repo / "work" / "releases" / (version or "test")
        return Job(True, version, None, release_dir, release_dir / RECORD_NAME,
                   [Path(args.test_file).name], args.title, args.title is not None and not args.no_list)
    spec = release.load_spec(repo, args.version)
    release_dir = Path(args.release_dir) if args.release_dir else repo / "work" / "releases" / spec.version
    names = list(args.files) if args.files else [f"{release.PACKAGE}-{spec.version}.zip", release.sha256_name(spec)]
    if len(set(names)) != len(names):
        raise UploadError("duplicate names in --files")
    for name in names:
        check_upload_name(name)
        if not (release_dir / name).is_file():
            hint = "" if args.files else " (a multi-part release has no single ZIP: name its files with --files)"
            raise UploadError(f"release file not found: {release_dir / name}{hint}")
    return Job(False, spec.version, spec, release_dir, release_dir / RECORD_NAME, names,
               args.title or f"ArenaMapPort {spec.version}", not args.no_list)


def find_key(args, runtime: Runtime) -> tuple:
    environ = os.environ if runtime.environ is None else runtime.environ
    platform = sys.platform if runtime.platform is None else runtime.platform
    return resolve_key(args.key_file, args.key_env, environ, platform, runtime.winreg)


def verify_release(args, job: Job, runtime: Runtime) -> tuple:
    """verify_set unless skipped; returns (manifests of the release, source commit or None)."""
    zips = [n for n in job.names if n.endswith(".zip")]
    if args.skip_verify:
        runtime.log("verification skipped (--skip-verify)")
        manifests = [m for m in (read_manifest(job.release_dir / n) for n in zips) if m]
    else:
        verified = release.verify_set(job.release_dir, job.version, log=runtime.log)
        members = {p.zip_name for p in verified.parts}
        foreign = [n for n in zips if n not in members]
        if foreign:
            raise UploadError(f"not part of the verified release in {job.release_dir}: {', '.join(foreign)}")
        manifests = [p.manifest for p in verified.parts]
    commit = next((m["source_commit"] for m in manifests if isinstance(m.get("source_commit"), str)), None)
    return manifests, commit


def dry_run_lines(job: Job, files, previous, key_text: str) -> list:
    lines = [f"DRY RUN: no network, nothing written. The record would be {job.record_path}",
             f"  API key: {key_text}"]
    for f in files:
        recorded = find_recorded(previous, f)
        state = (f"recorded as {recorded['id']}; a real run confirms it with Pixeldrain and reuses it"
                 if recorded else "would be uploaded")
        lines.append(f"  {f.name}  {size_text(f.size)}  SHA-256 {f.sha256}  {state}")
    lines.append(f"  list: {'would be created, title ' + repr(job.title) if job.make_list else 'none'}")
    return lines


def run(args, repo: Path, runtime: Runtime, session: Session) -> int:
    log = runtime.log
    job = make_job(args, repo)
    if len(job.title or "") > MAX_TITLE:
        raise UploadError(f"the list title is longer than {MAX_TITLE} characters")
    dist = Path(args.dist) if args.dist else repo / "dist"

    key_text = ""
    try:
        session.key, source = find_key(args, runtime)
        key_text = f"found ({source})"
        if not args.dry_run:
            log(f"API key: {source}")
    except UploadError as exc:
        if not args.dry_run:
            raise
        key_text = f"NOT FOUND, a real run would stop: {exc}"

    manifests, commit = [], None
    if not job.testing:
        manifests, commit = verify_release(args, job, runtime)
    files = ([local_file(Path(args.test_file))] if job.testing
             else [local_file(job.release_dir / name) for name in job.names])
    previous = load_record(job.record_path)
    if (previous is not None and previous.get("version") is not None and job.version is not None
            and previous["version"] != job.version):
        raise UploadError(f"the upload record {job.record_path} belongs to version {previous.get('version')}, "
                          f"not {job.version}")
    if job.testing and previous is not None and previous.get("test") is not True:
        raise UploadError(f"{job.record_path} is a release record and a --test-file run would replace it: give the "
                          "test run another --release-dir")

    if args.dry_run:
        for line in dry_run_lines(job, files, previous, key_text):
            log(line)
        if not job.testing:
            for link in virustotal_links(dist, job.spec):
                log(f"  VirusTotal: {link['name']}  " + (link["url"] or f"missing in dist: {link['path']}"))
        return 0

    api = Api(session.key, runtime.transport or http_request, timeout=args.timeout, retries=args.retries,
              sleep=runtime.sleep, log=log)
    record = run_upload(api, files, record_path=job.record_path, previous=previous, version=job.version,
                        source_commit=commit, title=job.title, make_list=job.make_list, force=args.force,
                        clock=runtime.clock, log=log, testing=job.testing)
    links, warnings = [], []
    if not job.testing:
        links = virustotal_links(dist, job.spec)
        shipped = manifest_hashes(manifests)
        for link in links:
            if not link["sha256"]:
                warnings.append(f"{link['path']} not found in dist; no VirusTotal link for it")
            elif shipped.get(link["path"], link["sha256"]) != link["sha256"]:
                warnings.append(f"dist's {link['name']} differs from the one inside the release ZIP (ZIP "
                                f"{shipped[link['path']]}, dist {link['sha256']}); the VirusTotal link follows "
                                "dist as asked, but rebuild dist or the ZIP before announcing")
    print_summary(record, job.title or f"Pixeldrain test upload ({files[0].name})", links, log, warnings)
    log(f"PIXELDRAIN_UPLOAD_OK {'test' if job.testing else job.version}")
    return 0


def main(argv=None, repo: Path = REPO, runtime: Runtime | None = None) -> int:
    runtime = runtime or Runtime()
    args = parse_args(argv)
    session = Session()
    try:
        return run(args, repo, runtime, session)
    except (UploadError, release.PackagingError, OSError) as exc:
        print(f"ERROR: {_clean(exc, session.key, limit=2000)}", file=sys.stderr)
        return 1
    except Exception as exc:  # noqa: BLE001 - never a traceback: it could carry the key; type and message instead
        print(f"ERROR: unexpected {type(exc).__name__}: {_clean(exc, session.key, limit=2000)}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(errors="replace")  # a legacy console codepage must not turn a file name into a traceback
        except (AttributeError, ValueError):
            pass
    sys.exit(main())
