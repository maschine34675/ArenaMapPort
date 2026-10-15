"""The Pixeldrain release uploader: authentication header, key resolution (argument, environment, registry),
streamed PUT, remote verification, list and record handling, skip / force, retries, dry run, test-file mode,
VirusTotal links and the guarantee that the API key never reaches any output. No test touches the network: every
HTTP call goes through an in-memory FakePixeldrain behind the uploader's transport signature, the registry is a
fake winreg, and make_release_zips.verify_set is replaced where the single-ZIP layout is needed (one test runs it
for real on a multi-part set built from the packager's own fake dist)."""
from contextlib import redirect_stderr, redirect_stdout
from datetime import datetime, timezone
import base64
import hashlib
import http.client
import http.server
import io
import json
import os
from pathlib import Path
import random
import socket
import sys
import tempfile
import threading
import time
import unittest
from types import SimpleNamespace
from unittest.mock import patch
import urllib.error
import urllib.parse
import zipfile

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "release"))
import upload_pixeldrain as upload

try:  # only the real-verify integration test needs the packager's fake dist; it skips itself without it
    import test_release_zips as packager_tests
except ImportError:  # pragma: no cover
    packager_tests = None

KEY = "pd-test-KEY-0123456789abcdef"
KEY_AUTH = "Basic OnBkLXRlc3QtS0VZLTAxMjM0NTY3ODlhYmNkZWY="  # base64(":" + KEY), computed independently
KEY_B64 = "OnBkLXRlc3QtS0VZLTAxMjM0NTY3ODlhYmNkZWY="
KEY_PLAIN_B64 = base64.b64encode(KEY.encode()).decode()
NOW = datetime(2026, 10, 15, 12, 0, 0, tzinfo=timezone.utc)
COMMIT = "b" * 40
PLUGIN = "BepInEx/plugins/maschine-ArenaMapPort"
SERVER = "SPT_Runtime/user/mods/ArenaMapPort"
CLIENT_REL = f"{PLUGIN}/maschine-ArenaMapPort.Client.dll"
SERVER_REL = f"{SERVER}/maschine-ArenaMapPort.Server.dll"
VT = "https://www.virustotal.com/gui/file/"
ZIP_NAME = "maschine-ArenaMapPort-0.1.0.zip"
SHA_NAME = "maschine-ArenaMapPort-0.1.0.sha256"


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def csproj(assembly, version):
    return (f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AssemblyName>{assembly}</AssemblyName>'
            f'<Version>{version}</Version></PropertyGroup></Project>')


# --------------------------------------------------------------------------------------------------------------
# Fakes
# --------------------------------------------------------------------------------------------------------------

class Call:
    def __init__(self, method, url, headers, timeout):
        self.method = method
        self.url = url
        self.headers = headers
        self.timeout = timeout
        self.body_size = None
        self.body_sha = None
        self.json = None

    @property
    def path(self):
        return urllib.parse.urlsplit(self.url).path


class FakePixeldrain:
    """Pixeldrain behind transport(method, url, headers, body, timeout) -> (status, data). `script` holds outcomes
    for the next calls (an exception to raise, or a (status, data) tuple) before the normal behaviour resumes."""

    def __init__(self):
        self.calls = []
        self.files = {}
        self.lists = {}
        self.script = []
        self.counter = 0
        self.corrupt_next = None        # "hash" | "size": the next PUT is stored wrongly
        self.break_next_upload_after = None
        self.hide_hash_polls = 0
        self.user = {"username": "tester", "subscription": {"name": "Pro"}}  # GET /api/user, the key check
        self.user_script = []           # outcomes for the next key checks (kept apart from `script`)

    def __call__(self, method, url, headers, body, timeout):
        call = Call(method, url, dict(headers), timeout)
        self.calls.append(call)
        broken = None
        if body is not None and hasattr(body, "read"):
            digest, count = hashlib.sha256(), 0
            while True:
                block = body.read(7000)
                if not block:
                    break
                digest.update(block)
                count += len(block)
                if self.break_next_upload_after is not None and count >= self.break_next_upload_after:
                    self.break_next_upload_after = None
                    broken = ConnectionResetError("connection reset by peer")
                    break
            call.body_size, call.body_sha = count, digest.hexdigest()
        elif body is not None:
            call.body_size, call.body_sha = len(body), sha(body)
            call.json = json.loads(body.decode("utf-8"))
        if broken is not None:
            raise broken
        if method == "GET" and call.path == "/api/user":
            if self.user_script:
                outcome = self.user_script.pop(0)
                if isinstance(outcome, BaseException):
                    raise outcome
                return outcome
            return 200, json.loads(json.dumps(self.user))
        if self.script:
            outcome = self.script.pop(0)
            if isinstance(outcome, BaseException):
                raise outcome
            return outcome
        path = call.path
        if method == "PUT" and path.startswith("/api/file/"):
            self.counter += 1
            file_id = f"F{self.counter}"
            info = {"id": file_id, "name": urllib.parse.unquote(path[len("/api/file/"):]),
                    "size": call.body_size, "hash_sha256": call.body_sha}
            if self.corrupt_next == "hash":
                info["hash_sha256"] = "0" * 64
            elif self.corrupt_next == "size":
                info["size"] += 1
            self.corrupt_next = None
            self.files[file_id] = info
            return 201, {"success": True, "id": file_id, "name": info["name"], "size": info["size"]}
        if method == "GET" and path.startswith("/api/file/") and path.endswith("/info"):
            file_id = path[len("/api/file/"):-len("/info")]
            if file_id not in self.files:
                return 404, {"success": False, "value": "not_found", "message": "file not found"}
            info = dict(self.files[file_id])
            if self.hide_hash_polls > 0:
                self.hide_hash_polls -= 1
                info.pop("hash_sha256")
            return 200, info
        if method == "POST" and path == "/api/list":
            self.counter += 1
            list_id = f"L{self.counter}"
            self.lists[list_id] = call.json
            return 201, {"success": True, "id": list_id}
        raise AssertionError(f"unexpected call {method} {path}")

    def by(self, method, prefix=""):
        return [c for c in self.calls if c.method == method and c.path.startswith("/api/" + prefix)]


class FakeWinreg:
    HKEY_CURRENT_USER = "HKCU"
    HKEY_LOCAL_MACHINE = "HKLM"
    KEY_READ = 0x20019

    class _Handle:
        def __init__(self, values):
            self.values = values

        def __enter__(self):
            return self

        def __exit__(self, *_exc):
            return False

    def __init__(self, user=None, machine=None):
        self.stores = {
            ("HKCU", "Environment"): user,
            ("HKLM", r"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"): machine}
        self.opened = []

    def OpenKey(self, hive, subkey, reserved=0, access=0):  # noqa: N802 - mirrors winreg
        self.opened.append((hive, subkey, access))
        values = self.stores.get((hive, subkey))
        if values is None:
            raise FileNotFoundError(2, "no such key")
        return self._Handle(values)

    def QueryValueEx(self, handle, name):  # noqa: N802
        if name not in handle.values:
            raise FileNotFoundError(2, "no such value")
        return handle.values[name], 1


# --------------------------------------------------------------------------------------------------------------
# Fixture: a fake repo, dist and single-ZIP release
# --------------------------------------------------------------------------------------------------------------

class UploaderCase(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.tmp = Path(temporary.name)
        self.repo = self.tmp / "repo"
        for project, assembly in (("ArenaMapPort.Client", "maschine-ArenaMapPort.Client"),
                                  ("ArenaMapPort.Server", "maschine-ArenaMapPort.Server")):
            (self.repo / project).mkdir(parents=True)
            (self.repo / project / f"{project}.csproj").write_text(csproj(assembly, "0.1.0"), encoding="utf-8")
        self.dist = self.repo / "dist"
        self.dlls = {CLIENT_REL: b"client dll bytes " * 60, SERVER_REL: b"server dll bytes " * 45}
        for rel, data in self.dlls.items():
            (self.dist / rel).parent.mkdir(parents=True, exist_ok=True)
            (self.dist / rel).write_bytes(data)
        self.release_dir = self.repo / "work" / "releases" / "0.1.0"
        self.release_dir.mkdir(parents=True)
        self.manifest = self.write_single_zip(self.release_dir / ZIP_NAME)
        self.zip_bytes = (self.release_dir / ZIP_NAME).read_bytes()
        (self.release_dir / SHA_NAME).write_text(f"{sha(self.zip_bytes)}  {ZIP_NAME}\n", encoding="ascii")
        self.sha_bytes = (self.release_dir / SHA_NAME).read_bytes()
        self.record_path = self.release_dir / "pixeldrain.json"
        self.fake = FakePixeldrain()
        self.sleeps = []
        self.verify_calls = []
        self.verify_error = None

    def write_single_zip(self, path: Path, dlls=None) -> dict:
        dlls = self.dlls if dlls is None else dlls
        entries = [{"path": rel, "size": len(data), "sha256": sha(data), "origin": "dist"}
                   for rel, data in dlls.items()]
        manifest = {"schema": 1, "version": "0.1.0", "source_commit": COMMIT,
                    "part": {"index": 1, "of": 1, "id": "full", "zip": path.name},
                    "parts": [{"index": 1, "id": "full", "zip": path.name}], "entries": entries}
        with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as archive:
            for rel, data in dlls.items():
                archive.writestr(rel, data)
            archive.writestr(f"{PLUGIN}/bundles/arena_equator_scenes.bundle", random.Random(7).randbytes(5000))
            archive.writestr(f"{PLUGIN}/parts/full.json", json.dumps(manifest))
        return manifest

    def fake_verify(self, directory, version=None, log=print, size_limit=None):
        self.verify_calls.append((Path(directory), version))
        if self.verify_error is not None:
            raise self.verify_error
        part = SimpleNamespace(index=1, id="full", zip_name=ZIP_NAME, path=Path(directory) / ZIP_NAME,
                               sha256=sha(self.zip_bytes), size=len(self.zip_bytes), manifest=self.manifest)
        return SimpleNamespace(version=version, sha256_path=Path(directory) / SHA_NAME, parts=[part])

    def runtime(self, **overrides):
        values = dict(transport=self.fake, environ={"PIXELDRAIN_API_KEY": KEY}, platform="linux", winreg=None,
                      sleep=self.sleeps.append, clock=lambda: NOW)
        values.update(overrides)
        return upload.Runtime(**values)

    def cli(self, *argv, runtime=None, repo=None):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err), patch.object(upload.release, "verify_set",
                                                                      self.fake_verify):
            code = upload.main(list(argv), repo=repo or self.repo, runtime=runtime or self.runtime())
        return code, out.getvalue(), err.getvalue()

    def record(self):
        return json.loads(self.record_path.read_text(encoding="utf-8"))

    def make_file(self, name="probe.bin", size=3 * 2 ** 20 + 17):
        path = self.tmp / name
        path.write_bytes(random.Random(11).randbytes(size))
        return path

    def with_transport(self, wrapper):
        """A runtime whose transport is `wrapper(original, method, url, headers, body, timeout)`; the original is
        the FakePixeldrain."""
        original = self.fake.__call__
        return self.runtime(transport=lambda *call: wrapper(original, *call))

    def snapshot_tree(self, root: Path):
        return sorted(p.relative_to(root).as_posix() for p in root.rglob("*")) if root.exists() else None


# --------------------------------------------------------------------------------------------------------------
# Authentication and key resolution
# --------------------------------------------------------------------------------------------------------------

class AuthenticationTests(unittest.TestCase):
    def test_basic_auth_has_an_empty_user_name(self):
        self.assertEqual(upload.auth_header(KEY), KEY_AUTH)
        self.assertEqual(upload.auth_header(upload.Secret(KEY)), KEY_AUTH)
        self.assertEqual(base64.b64decode(KEY_AUTH.split()[1]), b":" + KEY.encode())

    def test_secret_never_prints_its_value(self):
        secret = upload.Secret(KEY)
        for text in (repr(secret), str(secret), f"{secret}", f"{secret!r}", "%s" % secret):
            self.assertNotIn(KEY, text)
        api = upload.Api(secret, FakePixeldrain(), timeout=1, retries=0, sleep=lambda _s: None, log=print)
        self.assertNotIn(KEY, repr(api))

    def test_redact_removes_every_form_of_the_key(self):
        text = f"raw {KEY} header {KEY_AUTH} b64 {KEY_B64} plain-b64 {KEY_PLAIN_B64}"
        cleaned = upload.redact(text, upload.Secret(KEY))
        for form in (KEY, KEY_B64, KEY_PLAIN_B64):
            self.assertNotIn(form, cleaned)
        self.assertEqual(upload.redact("nothing here", None), "nothing here")


class KeyResolutionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.tmp = Path(temporary.name)

    def resolve(self, key_file=None, environ=None, platform="linux", winreg=None, key_env="PIXELDRAIN_API_KEY"):
        secret, source = upload.resolve_key(key_file, key_env, environ or {}, platform, winreg)
        return secret.reveal(), source

    def test_key_from_the_environment(self):
        value, source = self.resolve(environ={"PIXELDRAIN_API_KEY": f"  {KEY}\n"})
        self.assertEqual(value, KEY)
        self.assertEqual(source, "environment variable PIXELDRAIN_API_KEY")

    def test_key_env_names_another_variable(self):
        value, _source = self.resolve(environ={"PIXELDRAIN_API_KEY": "other", "MY_KEY": KEY}, key_env="MY_KEY")
        self.assertEqual(value, KEY)

    def test_key_file_wins_and_is_stripped(self):
        path = self.tmp / "key.txt"
        path.write_bytes(b"\xef\xbb\xbf" + KEY.encode() + b"\r\n")  # PowerShell 5 style: BOM and CRLF
        value, source = self.resolve(key_file=path, environ={"PIXELDRAIN_API_KEY": "from-env"})
        self.assertEqual(value, KEY)
        self.assertIn("--key-file", source)

    def test_an_unusable_key_file_is_an_error_and_never_falls_back(self):
        env = {"PIXELDRAIN_API_KEY": KEY}
        with self.assertRaises(upload.UploadError) as caught:
            self.resolve(key_file=self.tmp / "missing.txt", environ=env)
        self.assertIn("cannot read --key-file", str(caught.exception))
        empty = self.tmp / "empty.txt"
        empty.write_text("  \n", encoding="ascii")
        with self.assertRaisesRegex(upload.UploadError, "empty"):
            self.resolve(key_file=empty, environ=env)
        spaced = self.tmp / "spaced.txt"
        spaced.write_text("two words", encoding="ascii")
        with self.assertRaises(upload.UploadError) as caught:
            self.resolve(key_file=spaced, environ=env)
        self.assertNotIn("two words", str(caught.exception))
        utf16 = self.tmp / "utf16.txt"
        utf16.write_bytes(KEY.encode("utf-16"))
        with self.assertRaises(upload.UploadError) as caught:
            self.resolve(key_file=utf16, environ=env)
        self.assertNotIn(KEY, str(caught.exception))

    def test_machine_scope_registry_is_used_when_the_shell_does_not_have_the_variable(self):
        winreg = FakeWinreg(machine={"PIXELDRAIN_API_KEY": KEY})
        value, source = self.resolve(environ={}, platform="win32", winreg=winreg)
        self.assertEqual(value, KEY)
        self.assertEqual(source, "registry variable PIXELDRAIN_API_KEY (Machine scope)")
        self.assertEqual([(h, s) for h, s, _a in winreg.opened],
                         [("HKCU", "Environment"),
                          ("HKLM", r"SYSTEM\CurrentControlSet\Control\Session Manager\Environment")])
        self.assertTrue(all(access == FakeWinreg.KEY_READ for _h, _s, access in winreg.opened))

    def test_user_scope_wins_over_machine_scope_and_environment_wins_over_both(self):
        winreg = FakeWinreg(user={"PIXELDRAIN_API_KEY": "user-key"}, machine={"PIXELDRAIN_API_KEY": "machine-key"})
        value, source = self.resolve(environ={}, platform="win32", winreg=winreg)
        self.assertEqual((value, source), ("user-key", "registry variable PIXELDRAIN_API_KEY (User scope)"))
        value, _source = self.resolve(environ={"PIXELDRAIN_API_KEY": "env-key"}, platform="win32", winreg=winreg)
        self.assertEqual(value, "env-key")

    def test_blank_values_are_skipped(self):
        winreg = FakeWinreg(user={"PIXELDRAIN_API_KEY": "  "}, machine={"PIXELDRAIN_API_KEY": KEY})
        value, _source = self.resolve(environ={"PIXELDRAIN_API_KEY": "   "}, platform="win32", winreg=winreg)
        self.assertEqual(value, KEY)

    def test_registry_is_not_read_off_windows(self):
        winreg = FakeWinreg(machine={"PIXELDRAIN_API_KEY": KEY})
        with self.assertRaises(upload.MissingKeyError):
            self.resolve(environ={}, platform="linux", winreg=winreg)
        self.assertEqual(winreg.opened, [])

    def test_missing_key_names_the_three_sources(self):
        for platform, winreg in (("win32", FakeWinreg()), ("linux", None)):
            with self.assertRaises(upload.MissingKeyError) as caught:
                self.resolve(environ={}, platform=platform, winreg=winreg)
            text = str(caught.exception)
            for needle in ("--key-file", "environment variable PIXELDRAIN_API_KEY", "registry"):
                self.assertIn(needle, text)
        with self.assertRaises(upload.MissingKeyError) as caught:
            self.resolve(environ={}, platform="win32", winreg=FakeWinreg())
        self.assertIn(r"HKCU\Environment", str(caught.exception))
        self.assertIn(r"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", str(caught.exception))

    @unittest.skipUnless(sys.platform == "win32", "winreg exists on Windows only")
    def test_the_real_winreg_module_offers_what_the_reader_uses(self):
        import winreg  # only inspected, never queried: the user's real key stays unread
        for name in ("HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE", "KEY_READ", "OpenKey", "QueryValueEx"):
            self.assertTrue(hasattr(winreg, name), name)


# --------------------------------------------------------------------------------------------------------------
# HTTP glue
# --------------------------------------------------------------------------------------------------------------

def connection_class(status=200, payload=b'{"success": true}', send_error=None, record=None):
    record = record if record is not None else {}

    class Response:
        def __init__(self):
            self.status = status

        def read(self, amount=-1):
            return payload

    class Connection:
        def __init__(self, host, port, **kwargs):
            record["init"] = (host, port, kwargs)

        def request(self, method, target, body=None, headers=None):
            record["request"] = (method, target, body, headers)
            if send_error is not None:
                raise send_error

        def getresponse(self):
            return Response()

        def close(self):
            record["closed"] = True

    return Connection


class HttpRequestTests(unittest.TestCase):
    def test_request_goes_to_pixeldrain_over_https_and_parses_json(self):
        seen = {}
        with patch.object(upload.http.client, "HTTPSConnection", connection_class(record=seen)):
            status, data = upload.http_request("GET", "https://pixeldrain.com/api/file/x/info?a=1",
                                               {"Authorization": KEY_AUTH}, None, 33)
        self.assertEqual((status, data), (200, {"success": True}))
        host, port, kwargs = seen["init"]
        self.assertEqual((host, port, kwargs["timeout"], kwargs["blocksize"]), ("pixeldrain.com", 443, 33, 1 << 20))
        self.assertEqual(seen["request"][:2], ("GET", "/api/file/x/info?a=1"))
        self.assertTrue(seen["closed"])

    def test_text_and_empty_answers(self):
        with patch.object(upload.http.client, "HTTPSConnection", connection_class(502, b"<html>bad gateway")):
            self.assertEqual(upload.http_request("GET", "https://pixeldrain.com/api/x", {}, None, 1),
                             (502, "<html>bad gateway"))
        with patch.object(upload.http.client, "HTTPSConnection", connection_class(204, b"")):
            self.assertEqual(upload.http_request("GET", "https://pixeldrain.com/api/x", {}, None, 1), (204, None))

    def test_no_other_host_or_scheme_ever_receives_the_header(self):
        with patch.object(upload.http.client, "HTTPSConnection", connection_class()) as _unused:
            for url in ("http://pixeldrain.com/api/x", "https://pixeldrain.com.evil.example/api/x",
                        "https://example.org/api/x"):
                with self.assertRaises(upload.UploadError):
                    upload.http_request("GET", url, {"Authorization": KEY_AUTH}, None, 1)

    def test_an_early_answer_while_sending_is_reported_instead_of_a_broken_pipe(self):
        cls = connection_class(413, b'{"success": false, "value": "file_too_large"}',
                               send_error=BrokenPipeError(32, "broken pipe"))
        with patch.object(upload.http.client, "HTTPSConnection", cls):
            status, data = upload.http_request("PUT", "https://pixeldrain.com/api/file/x", {}, b"x", 1)
        self.assertEqual((status, data["value"]), (413, "file_too_large"))

    def test_a_send_failure_without_an_answer_stays_a_network_error(self):
        class Silent(connection_class(send_error=ConnectionResetError("reset"))):
            def getresponse(self):
                raise http.client.RemoteDisconnected("closed")

        with patch.object(upload.http.client, "HTTPSConnection", Silent):
            with self.assertRaises(ConnectionResetError):
                upload.http_request("PUT", "https://pixeldrain.com/api/file/x", {}, b"x", 1)


# --------------------------------------------------------------------------------------------------------------
# Upload of one arbitrary file (also the vehicle for the wire-level checks)
# --------------------------------------------------------------------------------------------------------------

class WireTests(UploaderCase):
    def test_put_url_headers_and_complete_streamed_body(self):
        path = self.make_file()
        code, out, err = self.cli("--test-file", str(path))
        self.assertEqual((code, err), (0, ""), out)
        puts = self.fake.by("PUT")
        self.assertEqual(len(puts), 1)
        put = puts[0]
        self.assertEqual(put.url, "https://pixeldrain.com/api/file/probe.bin")
        self.assertEqual(put.headers["Authorization"], KEY_AUTH)
        self.assertEqual(put.headers["Content-Length"], str(path.stat().st_size))
        self.assertEqual(put.headers["Content-Type"], "application/octet-stream")
        self.assertEqual(put.body_size, path.stat().st_size)
        self.assertEqual(put.body_sha, sha(path.read_bytes()))
        self.assertEqual(put.timeout, 120.0)

    def test_the_name_is_url_encoded(self):
        path = self.make_file("my release #1 ä&+%.bin", size=100)
        code, _out, err = self.cli("--test-file", str(path))
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(self.fake.by("PUT")[0].url,
                         "https://pixeldrain.com/api/file/my%20release%20%231%20%C3%A4%26%2B%25.bin")
        self.assertEqual(list(self.fake.files.values())[0]["name"], "my release #1 ä&+%.bin")

    def test_names_over_255_characters_are_refused(self):
        with self.assertRaises(upload.UploadError):
            upload.check_upload_name("x" * 256)
        upload.check_upload_name("x" * 255)

    def test_progress_is_logged_every_interval_and_at_the_end(self):
        path = self.make_file(size=3_000_017)
        with patch.object(upload, "PROGRESS_BYTES", 1_000_000):
            code, out, _err = self.cli("--test-file", str(path))
        self.assertEqual(code, 0)
        self.assertEqual(sum("sent" in line for line in out.splitlines()), 3)
        self.assertIn("upload finished", out)
        self.assertIn("match the local file", out)

    def test_a_file_that_shrinks_during_the_upload_fails_at_once(self):
        path = self.make_file(size=1000)
        body = upload.ProgressFile(path, 5000, "probe.bin", lambda _m: None)  # the size recorded before the shrink
        self.addCleanup(body.close)
        self.assertEqual(len(body.read(700)), 700)
        self.assertEqual(len(body.read(700)), 300)
        with self.assertRaisesRegex(upload.UploadError, "became shorter"):
            body.read(700)

    def test_the_body_never_reads_past_the_announced_length(self):
        path = self.make_file(size=1000)
        body = upload.ProgressFile(path, 600, "probe.bin", lambda _m: None)  # the file grew after it was hashed
        self.addCleanup(body.close)
        self.assertEqual(len(body.read()), 600)
        self.assertEqual(body.read(100), b"")

    def test_timeout_option_reaches_the_transport(self):
        code, _out, _err = self.cli("--test-file", str(self.make_file(size=10)), "--timeout", "7.5")
        self.assertEqual(code, 0)
        self.assertEqual(self.fake.calls[0].timeout, 7.5)

    def test_test_file_uploads_one_file_without_release_checks_or_list(self):
        path = self.make_file(size=500)
        code, out, err = self.cli("--test-file", str(path), "--release-dir", str(self.tmp / "probe-dir"))
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(self.verify_calls, [])
        self.assertEqual(self.fake.by("POST"), [])
        self.assertNotIn("virustotal", out.lower())
        record = json.loads((self.tmp / "probe-dir" / "pixeldrain.json").read_text(encoding="utf-8"))
        self.assertEqual(record["source_commit"], None)
        self.assertEqual([f["name"] for f in record["files"]], ["probe.bin"])
        self.assertIsNone(record["list"])
        self.assertIn("PIXELDRAIN_UPLOAD_OK test", out)
        self.assertEqual([p.name for p in (self.tmp / "probe-dir").iterdir()], ["pixeldrain.json"])

    def test_test_file_with_a_title_creates_a_list(self):
        path = self.make_file(size=500)
        code, _out, _err = self.cli("--test-file", str(path), "--title", "Connectivity test",
                                    "--release-dir", str(self.tmp / "probe-dir"))
        self.assertEqual(code, 0)
        self.assertEqual(self.fake.by("POST")[0].json["title"], "Connectivity test")

    def test_test_file_writes_the_spec_d_record_into_the_version_directory(self):
        path = self.make_file(size=500)
        code, out, err = self.cli("--test-file", str(path))  # no --release-dir: work/releases/<csproj version>
        self.assertEqual((code, err), (0, ""), out)
        self.assertEqual(self.record(), {
            "schema": 1, "version": "0.1.0", "source_commit": None, "uploaded_utc": "2026-10-15T12:00:00Z",
            "files": [{"name": "probe.bin", "size": 500, "sha256": sha(path.read_bytes()), "id": "F1",
                       "url": "https://pixeldrain.com/u/F1",
                       "direct_url": "https://pixeldrain.com/api/file/F1?download"}],
            "list": None, "test": True})
        self.assertEqual([p.name for p in self.release_dir.iterdir() if p.name.startswith("pixeldrain")],
                         ["pixeldrain.json"])

    def test_test_file_never_replaces_a_release_record(self):
        self.cli()  # a real release run: pixeldrain.json is a release record
        before = self.record_path.read_bytes()
        self.fake.calls.clear()
        code, _out, err = self.cli("--test-file", str(self.make_file(size=50)), "--release-dir",
                                   str(self.release_dir))
        self.assertEqual(code, 1)
        self.assertIn("release record", err)
        self.assertIn("another --release-dir", err)
        self.assertEqual(self.record_path.read_bytes(), before)
        self.assertEqual(self.fake.calls, [])  # refused before any network call

    def test_a_test_record_can_be_repeated_and_a_release_run_takes_over_the_file(self):
        probe = self.make_file(size=50)
        self.assertEqual(self.cli("--test-file", str(probe))[0], 0)
        self.assertEqual(self.cli("--test-file", str(probe))[0], 0)  # a test record may be replaced by a test run
        self.assertTrue(self.record()["test"])
        code, out, err = self.cli()  # the release run reads the test record and replaces it
        self.assertEqual((code, err), (0, ""), out)
        record = self.record()
        self.assertNotIn("test", record)
        self.assertEqual(record["source_commit"], COMMIT)
        self.assertEqual([f["name"] for f in record["files"]], [ZIP_NAME, SHA_NAME])
        self.assertEqual([f["name"] for f in record["other_files"]], ["probe.bin"])  # not forgotten
        self.assertEqual(self.cli("--test-file", str(probe))[0], 1)  # and now it is a release record

    def test_test_file_works_outside_a_repository_and_defaults_to_work_releases_test(self):
        bare = self.tmp / "bare"
        bare.mkdir()
        code, _out, err = self.cli("--test-file", str(self.make_file(size=50)), repo=bare)
        self.assertEqual((code, err), (0, ""))
        record = json.loads((bare / "work" / "releases" / "test" / "pixeldrain.json").read_text(encoding="utf-8"))
        self.assertIsNone(record["version"])
        self.assertTrue(record["test"])

    def test_test_file_takes_its_directory_from_the_version_option(self):
        code, _out, err = self.cli("--test-file", str(self.make_file(size=50)), "--version", "2.3.4")
        self.assertEqual((code, err), (0, ""))
        record = json.loads((self.repo / "work" / "releases" / "2.3.4" / "pixeldrain.json").read_text(
            encoding="utf-8"))
        self.assertEqual(record["version"], "2.3.4")

    def test_test_file_and_files_exclude_each_other(self):
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
            upload.parse_args(["--test-file", "a", "--files", "b"])
        self.assertEqual(caught.exception.code, 2)


# --------------------------------------------------------------------------------------------------------------
# Remote verification
# --------------------------------------------------------------------------------------------------------------

class VerificationTests(UploaderCase):
    def test_a_matching_upload_is_confirmed_with_get_info(self):
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""), out)
        infos = self.fake.by("GET", "file/")
        self.assertEqual([c.path for c in infos], ["/api/file/F1/info", "/api/file/F2/info"])

    def test_a_hash_mismatch_is_an_error_naming_the_id_and_deletes_nothing(self):
        self.fake.corrupt_next = "hash"
        code, out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("F1", err)
        self.assertIn("SHA-256", err)
        self.assertIn(ZIP_NAME, err)
        self.assertEqual({c.method for c in self.fake.calls}, {"PUT", "GET"})
        record = self.record()  # the bad upload is tracked so that its id is not lost
        self.assertEqual(record["files"], [])
        self.assertEqual([(f["name"], f["id"]) for f in record["other_files"]], [(ZIP_NAME, "F1")])

    def test_a_size_mismatch_is_an_error_naming_the_id(self):
        self.fake.corrupt_next = "size"
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("F1", err)
        self.assertIn("size", err)

    def test_a_hash_that_is_not_ready_yet_is_polled_a_few_times(self):
        self.fake.hide_hash_polls = 2
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""), out)
        self.assertEqual(self.sleeps, [upload.HASH_POLL_DELAY, upload.HASH_POLL_DELAY])

    def test_a_hash_that_never_appears_fails(self):
        self.fake.hide_hash_polls = 99
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("no hash_sha256", err)


# --------------------------------------------------------------------------------------------------------------
# Release flow: verification, list, record, skip / force
# --------------------------------------------------------------------------------------------------------------

class ReleaseFlowTests(UploaderCase):
    def test_default_run_verifies_uploads_both_files_and_creates_the_list(self):
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""), out)
        self.assertEqual(self.verify_calls, [(self.release_dir, "0.1.0")])
        self.assertEqual([c.path for c in self.fake.by("PUT")], [f"/api/file/{ZIP_NAME}", f"/api/file/{SHA_NAME}"])
        self.assertEqual(self.fake.by("PUT")[0].body_sha, sha(self.zip_bytes))
        self.assertEqual(self.fake.by("PUT")[1].body_sha, sha(self.sha_bytes))
        self.assertIn("PIXELDRAIN_UPLOAD_OK 0.1.0", out)

    def test_list_payload_shape(self):
        self.cli()
        posts = self.fake.by("POST")
        self.assertEqual(len(posts), 1)
        self.assertEqual(posts[0].url, "https://pixeldrain.com/api/list")
        self.assertEqual(posts[0].headers["Content-Type"], "application/json")
        self.assertEqual(posts[0].json, {
            "title": "ArenaMapPort 0.1.0", "anonymous": False,
            "files": [{"id": "F1", "description": ZIP_NAME}, {"id": "F2", "description": SHA_NAME}]})

    def test_custom_title_and_no_list(self):
        self.cli("--title", "Release candidate")
        self.assertEqual(self.fake.by("POST")[0].json["title"], "Release candidate")
        self.record_path.unlink()
        self.fake.calls.clear()
        code, _out, _err = self.cli("--no-list")
        self.assertEqual(code, 0)
        self.assertEqual(self.fake.by("POST"), [])
        self.assertIsNone(self.record()["list"])

    def test_title_over_300_characters_is_refused_before_any_network_call(self):
        code, _out, err = self.cli("--title", "t" * 301)
        self.assertEqual(code, 1)
        self.assertIn("300", err)
        self.assertEqual(self.fake.calls, [])

    def test_record_content(self):
        code, out, _err = self.cli()
        self.assertEqual(code, 0)
        expected_files = [
            {"name": ZIP_NAME, "size": len(self.zip_bytes), "sha256": sha(self.zip_bytes), "id": "F1",
             "url": "https://pixeldrain.com/u/F1", "direct_url": "https://pixeldrain.com/api/file/F1?download"},
            {"name": SHA_NAME, "size": len(self.sha_bytes), "sha256": sha(self.sha_bytes), "id": "F2",
             "url": "https://pixeldrain.com/u/F2", "direct_url": "https://pixeldrain.com/api/file/F2?download"}]
        self.assertEqual(self.record(), {
            "schema": 1, "version": "0.1.0", "source_commit": COMMIT, "uploaded_utc": "2026-10-15T12:00:00Z",
            "files": expected_files,
            "list": {"id": "L3", "url": "https://pixeldrain.com/l/L3", "title": "ArenaMapPort 0.1.0"}})
        self.assertEqual(sorted(p.name for p in self.release_dir.iterdir() if p.name.startswith("pixeldrain")),
                         ["pixeldrain.json"])
        self.assertNotIn(KEY, self.record_path.read_text(encoding="utf-8"))

    def test_markdown_block_carries_the_links_size_hash_and_virustotal_pages(self):
        code, out, _err = self.cli()
        self.assertEqual(code, 0)
        block = out.split("----- Markdown block (copy from here) -----")[1].split("----- end of Markdown block")[0]
        for needle in ("https://pixeldrain.com/l/L3", "https://pixeldrain.com/u/F1",
                       "https://pixeldrain.com/api/file/F1?download", sha(self.zip_bytes),
                       f"{len(self.zip_bytes):,} bytes", ZIP_NAME,
                       f"{VT}{sha(self.dlls[CLIENT_REL])}", f"{VT}{sha(self.dlls[SERVER_REL])}"):
            self.assertIn(needle, block)
        self.assertIn("exist only after somebody has uploaded each DLL", out)

    def test_source_commit_comes_from_the_manifest_also_without_verification(self):
        code, out, _err = self.cli("--skip-verify")
        self.assertEqual(code, 0, out)
        self.assertEqual(self.verify_calls, [])
        self.assertEqual(self.record()["source_commit"], COMMIT)
        self.assertIn("verification skipped", out)

    def test_a_failed_verification_stops_before_any_network_call(self):
        self.verify_error = upload.release.VerificationError("SHA-256 mismatch: " + ZIP_NAME)
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("SHA-256 mismatch", err)
        self.assertEqual(self.fake.calls, [])
        self.assertFalse(self.record_path.exists())

    def test_a_zip_outside_the_verified_set_is_refused(self):
        stray = self.release_dir / "maschine-ArenaMapPort-0.1.0-extra.zip"
        stray.write_bytes(b"not part of the release")
        code, _out, err = self.cli("--files", ZIP_NAME, stray.name)
        self.assertEqual(code, 1)
        self.assertIn("not part of the verified release", err)
        self.assertEqual(self.fake.calls, [])

    def test_files_option_selects_files_and_rejects_paths(self):
        code, _out, _err = self.cli("--files", SHA_NAME, "--no-list")
        self.assertEqual(code, 0)
        self.assertEqual([c.path for c in self.fake.by("PUT")], [f"/api/file/{SHA_NAME}"])
        code, _out, err = self.cli("--files", "../escape.zip")
        self.assertEqual(code, 1)
        self.assertIn("unusable file name", err)

    def test_a_missing_single_zip_points_at_files_for_multi_part_releases(self):
        (self.release_dir / ZIP_NAME).unlink()
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("--files", err)
        self.assertEqual(self.fake.calls, [])

    def test_version_option_and_release_dir_option(self):
        elsewhere = self.tmp / "elsewhere"
        elsewhere.mkdir()
        for name in (ZIP_NAME, SHA_NAME):
            (elsewhere / name).write_bytes((self.release_dir / name).read_bytes())
        code, _out, _err = self.cli("--release-dir", str(elsewhere), "--version", "0.1.0")
        self.assertEqual(code, 0)
        self.assertTrue((elsewhere / "pixeldrain.json").is_file())
        self.assertFalse(self.record_path.exists())

    # -- skip / force / reuse ----------------------------------------------------------------------------------

    def test_a_second_run_reuses_files_and_list_and_leaves_the_record_alone(self):
        self.cli()
        first = self.record_path.read_bytes()
        self.fake.calls.clear()
        later = lambda: datetime(2027, 1, 1, tzinfo=timezone.utc)  # noqa: E731
        code, out, err = self.cli(runtime=self.runtime(clock=later))
        self.assertEqual((code, err), (0, ""), out)
        self.assertEqual(self.fake.by("PUT"), [])
        self.assertEqual(self.fake.by("POST"), [])
        self.assertEqual([c.method for c in self.fake.calls], ["GET", "GET"])
        self.assertEqual(self.record_path.read_bytes(), first)
        self.assertIn("reusing", out)
        self.assertIn("https://pixeldrain.com/u/F1", out)  # the existing links are printed again

    def test_force_uploads_again(self):
        self.cli()
        self.fake.calls.clear()
        code, out, _err = self.cli("--force")
        self.assertEqual(code, 0, out)
        self.assertEqual(len(self.fake.by("PUT")), 2)
        self.assertEqual(len(self.fake.by("POST")), 1)
        self.assertEqual([f["id"] for f in self.record()["files"]], ["F4", "F5"])

    def test_a_recorded_file_that_vanished_or_differs_is_uploaded_again(self):
        self.cli()
        del self.fake.files["F1"]
        self.fake.files["F2"]["hash_sha256"] = "f" * 64
        self.fake.calls.clear()
        code, out, _err = self.cli()
        self.assertEqual(code, 0, out)
        self.assertEqual(len(self.fake.by("PUT")), 2)
        self.assertIn("gone from Pixeldrain", out)
        self.assertIn("differs", out)
        self.assertEqual(len(self.fake.by("POST")), 1)  # new ids, so a new list

    def test_a_changed_release_file_is_uploaded_new_while_the_unchanged_one_is_reused(self):
        self.cli()
        (self.release_dir / SHA_NAME).write_text(f"{'0' * 64}  {ZIP_NAME}\n", encoding="ascii")
        self.fake.calls.clear()
        code, _out, _err = self.cli("--skip-verify")
        self.assertEqual(code, 0)
        self.assertEqual([c.path for c in self.fake.by("PUT")], [f"/api/file/{SHA_NAME}"])

    def test_the_list_is_reused_only_for_the_same_ids_and_title(self):
        self.cli()
        self.fake.calls.clear()
        code, _out, _err = self.cli("--title", "Another title")
        self.assertEqual(code, 0)
        self.assertEqual(self.fake.by("PUT"), [])
        self.assertEqual(len(self.fake.by("POST")), 1)
        self.assertEqual(self.record()["list"]["title"], "Another title")

    def test_a_run_that_dies_on_the_second_file_keeps_the_first_in_the_record(self):
        original = self.fake.__call__

        def failing(method, url, headers, body, timeout):
            if method == "PUT" and url.endswith(SHA_NAME):
                return 403, {"success": False, "value": "forbidden", "message": "nope"}
            return original(method, url, headers, body, timeout)

        code, _out, err = self.cli(runtime=self.runtime(transport=failing))
        self.assertEqual(code, 1)
        self.assertIn("HTTP 403", err)
        self.assertEqual([f["name"] for f in self.record()["files"]], [ZIP_NAME])
        self.assertIsNone(self.record()["list"])
        self.fake.calls.clear()
        code, _out, _err = self.cli()
        self.assertEqual(code, 0)
        self.assertEqual([c.path for c in self.fake.by("PUT")], [f"/api/file/{SHA_NAME}"])  # the ZIP is reused

    # -- record handling ----------------------------------------------------------------------------------------

    def test_a_corrupt_record_is_an_error_and_stays_untouched(self):
        self.record_path.write_text("{not json", encoding="utf-8")
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("cannot read the upload record", err)
        self.assertEqual(self.record_path.read_text(encoding="utf-8"), "{not json")
        self.assertEqual(self.fake.calls, [])

    def test_a_foreign_or_malformed_record_is_refused(self):
        for content in ({"schema": 2, "files": []}, {"schema": 1, "files": [{"name": 1}]},
                        {"schema": 1, "files": [], "list": {"id": 5}}):
            self.record_path.write_text(json.dumps(content), encoding="utf-8")
            code, _out, err = self.cli()
            self.assertEqual(code, 1, content)
            self.assertIn("unusable", err)
        self.record_path.write_text(json.dumps({"schema": 1, "version": "9.9.9", "files": [], "list": None}),
                                    encoding="utf-8")
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("belongs to version 9.9.9", err)

    def test_the_record_is_replaced_atomically(self):
        self.cli()
        before = self.record_path.read_bytes()
        with patch.object(upload.os, "replace", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                upload.write_record(self.record_path, {"schema": 1, "files": []})
        self.assertEqual(self.record_path.read_bytes(), before)
        self.assertFalse((self.release_dir / "pixeldrain.json.partial").exists())

    def test_a_failing_record_write_is_reported_without_leaving_a_partial_file(self):
        with patch.object(upload.os, "replace", side_effect=OSError("disk full")):
            code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("disk full", err)
        self.assertEqual([p.name for p in self.release_dir.iterdir() if p.name.startswith("pixeldrain")], [])

    def test_the_record_is_written_through_a_partial_file(self):
        seen = []
        original = os.replace

        def spy(source, target):
            seen.append((Path(source).name, Path(target).name, Path(source).is_file()))
            return original(source, target)

        with patch.object(upload.os, "replace", spy):
            self.cli()
        self.assertTrue(seen)
        self.assertTrue(all(src == "pixeldrain.json.partial" and dst == "pixeldrain.json" and existed
                            for src, dst, existed in seen))


# --------------------------------------------------------------------------------------------------------------
# Retries and API errors
# --------------------------------------------------------------------------------------------------------------

class RetryTests(UploaderCase):
    def test_network_errors_are_retried_with_a_growing_backoff(self):
        self.fake.script = [urllib.error.URLError("connection refused"), socket.timeout("timed out"),
                            http.client.RemoteDisconnected("closed"), ]
        path = self.tmp / "probe.bin"
        path.write_bytes(b"x" * 100)
        code, out, err = self.cli("--test-file", str(path), "--retries", "3")
        self.assertEqual((code, err), (0, ""), out)
        self.assertEqual(self.sleeps, [1.0, 2.0, 4.0])
        self.assertEqual(len(self.fake.by("PUT")), 4)  # three failures, one success
        self.assertEqual(self.fake.by("PUT")[-1].body_size, 100)

    def test_a_server_error_is_retried(self):
        path = self.tmp / "probe.bin"
        path.write_bytes(b"x" * 100)
        self.fake.script = [(503, {"success": False, "value": "unavailable", "message": "later"})]
        code, out, _err = self.cli("--test-file", str(path))
        self.assertEqual(code, 0, out)
        self.assertEqual(self.sleeps, [1.0])
        self.assertIn("HTTP 503", out)

    def test_an_upload_that_breaks_midway_restarts_at_byte_zero(self):
        path = self.tmp / "probe.bin"
        path.write_bytes(random.Random(5).randbytes(50_000))
        self.fake.break_next_upload_after = 10_000
        code, _out, err = self.cli("--test-file", str(path))
        self.assertEqual((code, err), (0, ""))
        first, second = self.fake.by("PUT")
        self.assertLess(first.body_size, 50_000)
        self.assertEqual((second.body_size, second.body_sha), (50_000, sha(path.read_bytes())))

    def test_retries_are_bounded(self):
        path = self.tmp / "probe.bin"
        path.write_bytes(b"x")
        self.fake.script = [urllib.error.URLError("down")] * 10
        code, _out, err = self.cli("--test-file", str(path), "--retries", "2")
        self.assertEqual(code, 1)
        self.assertEqual(len(self.fake.by("PUT")), 3)
        self.assertIn("3 attempt", err)
        self.fake.calls.clear()
        self.fake.script = [urllib.error.URLError("down")] * 10
        code, _out, _err = self.cli("--test-file", str(path), "--retries", "0")
        self.assertEqual((code, len(self.fake.by("PUT"))), (1, 1))

    def test_client_errors_are_final(self):
        path = self.tmp / "probe.bin"
        path.write_bytes(b"x")
        for status, needle in ((401, "rejected the API key"), (413, "file_too_large"), (404, "HTTP 404"),
                               (429, "HTTP 429")):
            self.fake.calls.clear()
            self.sleeps.clear()
            self.fake.script = [(status, {"success": False, "value": "code_x", "message": "m"})]
            code, _out, err = self.cli("--test-file", str(path))
            self.assertEqual(code, 1, status)
            self.assertIn(needle, err)
            self.assertEqual((len(self.fake.by("PUT")), self.sleeps), (1, []), status)

    def test_an_http_error_raised_by_a_urllib_transport_counts_as_its_status(self):
        path = self.tmp / "probe.bin"
        path.write_bytes(b"x")
        self.fake.script = [urllib.error.HTTPError("https://pixeldrain.com/api", 401, "Unauthorized", {}, None)]
        code, _out, err = self.cli("--test-file", str(path))
        self.assertEqual(code, 1)
        self.assertIn("rejected the API key", err)
        self.assertEqual(len(self.fake.by("PUT")), 1)

    def test_success_false_in_a_2xx_answer_is_an_error(self):
        path = self.tmp / "probe.bin"
        path.write_bytes(b"x")
        self.fake.script = [(201, {"success": False, "value": "name_too_long", "message": "too long"})]
        code, _out, err = self.cli("--test-file", str(path))
        self.assertEqual(code, 1)
        self.assertIn("name_too_long", err)

    def test_an_answer_without_an_id_is_an_error(self):
        path = self.tmp / "probe.bin"
        path.write_bytes(b"x")
        self.fake.script = [(201, {"success": True})]
        code, _out, err = self.cli("--test-file", str(path))
        self.assertEqual(code, 1)
        self.assertIn("no usable file id", err)

    def test_negative_retries_and_bad_timeouts_are_usage_errors(self):
        for argv in (["--retries", "-1"], ["--timeout", "0"], ["--key-env", "not a name"]):
            with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
                upload.parse_args(argv)
            self.assertEqual(caught.exception.code, 2)


# --------------------------------------------------------------------------------------------------------------
# The key stays secret
# --------------------------------------------------------------------------------------------------------------

class SecrecyTests(UploaderCase):
    FORMS = (KEY, KEY_AUTH, KEY_B64, KEY_PLAIN_B64)

    def assert_clean(self, *texts):
        for text in texts:
            for form in self.FORMS:
                self.assertNotIn(form, text)

    def test_nothing_leaks_on_success(self):
        code, out, err = self.cli()
        self.assertEqual(code, 0)
        self.assert_clean(out, err, self.record_path.read_text(encoding="utf-8"))
        self.assertIn("API key: environment variable PIXELDRAIN_API_KEY", out)

    def test_nothing_leaks_when_the_server_echoes_the_key(self):
        echo = {"success": False, "value": "authentication_failed", "message": f"bad key {KEY} / {KEY_AUTH}"}
        self.fake.script = [(401, echo)]
        code, out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("rejected the API key", err)
        self.assert_clean(out, err)

    def test_nothing_leaks_when_the_network_stack_echoes_the_header(self):
        self.fake.script = [OSError(f"broken pipe while sending Authorization: {KEY_AUTH} ({KEY})")] * 5
        code, out, err = self.cli("--retries", "1")
        self.assertEqual(code, 1)
        self.assertIn("network error", err)
        self.assert_clean(out, err)

    def test_nothing_leaks_from_an_unexpected_exception(self):
        def buggy(*_args):
            raise RuntimeError(f"internal bug with {KEY} and {KEY_B64}")

        code, out, err = self.cli(runtime=self.runtime(transport=buggy))
        self.assertEqual(code, 1)
        self.assertIn("unexpected RuntimeError", err)
        self.assertNotIn("Traceback", err)
        self.assert_clean(out, err)

    def test_nothing_leaks_in_a_dry_run_or_from_the_api_layer(self):
        code, out, err = self.cli("--dry-run")
        self.assertEqual(code, 0)
        self.assert_clean(out, err)
        api = upload.Api(upload.Secret(KEY), lambda *_a: (_ for _ in ()).throw(OSError(f"x {KEY}")),
                         timeout=1, retries=0, sleep=lambda _s: None, log=print)
        with self.assertRaises(upload.ApiError) as caught:
            api.call("GET", "https://pixeldrain.com/api/file/x/info")
        self.assert_clean(str(caught.exception))

    def test_key_file_runs_do_not_print_the_content(self):
        key_file = self.tmp / "key.txt"
        key_file.write_text(KEY + "\n", encoding="ascii")
        code, out, err = self.cli("--key-file", str(key_file), runtime=self.runtime(environ={}))
        self.assertEqual(code, 0)
        self.assert_clean(out, err)
        self.assertEqual(self.fake.calls[0].headers["Authorization"], KEY_AUTH)

    def test_the_key_comes_from_the_registry_when_the_environment_lacks_it(self):
        runtime = self.runtime(environ={}, platform="win32",
                               winreg=FakeWinreg(machine={"PIXELDRAIN_API_KEY": KEY}))
        code, out, err = self.cli(runtime=runtime)
        self.assertEqual(code, 0, err)
        self.assertIn("registry variable PIXELDRAIN_API_KEY (Machine scope)", out)
        self.assertEqual(self.fake.calls[0].headers["Authorization"], KEY_AUTH)
        self.assert_clean(out, err)

    def test_a_missing_key_is_a_clear_error_before_anything_happens(self):
        code, out, err = self.cli(runtime=self.runtime(environ={}))
        self.assertEqual(code, 1)
        self.assertTrue(err.startswith("ERROR: no Pixeldrain API key found"))
        for needle in ("--key-file", "PIXELDRAIN_API_KEY", "registry"):
            self.assertIn(needle, err)
        self.assertNotIn("Traceback", err)
        self.assertEqual((self.fake.calls, self.verify_calls), ([], []))

    def test_key_env_selects_the_variable(self):
        code, _out, _err = self.cli("--key-env", "PD_OTHER", runtime=self.runtime(environ={"PD_OTHER": KEY}))
        self.assertEqual(code, 0)


# --------------------------------------------------------------------------------------------------------------
# Dry run
# --------------------------------------------------------------------------------------------------------------

class DryRunTests(UploaderCase):
    def forbidden(self, *_args):
        raise AssertionError("the transport must not be called in a dry run")

    def test_dry_run_prints_the_plan_and_changes_nothing(self):
        before = self.snapshot_tree(self.repo)
        code, out, err = self.cli("--dry-run", runtime=self.runtime(transport=self.forbidden))
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(self.snapshot_tree(self.repo), before)
        for needle in ("DRY RUN", ZIP_NAME, SHA_NAME, sha(self.zip_bytes), "would be uploaded", "would be created",
                       "ArenaMapPort 0.1.0", f"{VT}{sha(self.dlls[CLIENT_REL])}", "API key: found (environment"):
            self.assertIn(needle, out)
        self.assertNotIn("PIXELDRAIN_UPLOAD_OK", out)
        self.assertEqual(len(self.verify_calls), 1)  # the local verification still runs

    def test_dry_run_without_a_key_reports_it_but_does_not_fail(self):
        code, out, _err = self.cli("--dry-run", runtime=self.runtime(transport=self.forbidden, environ={}))
        self.assertEqual(code, 0)
        self.assertIn("NOT FOUND", out)

    def test_dry_run_mentions_what_the_record_already_holds(self):
        self.cli()
        code, out, _err = self.cli("--dry-run", runtime=self.runtime(transport=self.forbidden))
        self.assertEqual(code, 0)
        self.assertIn("recorded as F1", out)

    def test_dry_run_with_a_test_file_creates_no_directory(self):
        path = self.tmp / "probe.bin"
        path.write_bytes(b"x" * 10)
        bare = self.tmp / "bare"
        bare.mkdir()
        code, out, _err = self.cli("--dry-run", "--test-file", str(path), repo=bare,
                                   runtime=self.runtime(transport=self.forbidden))
        self.assertEqual(code, 0)
        self.assertEqual(list(bare.iterdir()), [])
        self.assertIn("probe.bin", out)

    def test_dry_run_still_rejects_a_bad_selection(self):
        code, _out, err = self.cli("--dry-run", "--files", "missing.zip", runtime=self.runtime(transport=self.forbidden))
        self.assertEqual(code, 1)
        self.assertIn("release file not found", err)


# --------------------------------------------------------------------------------------------------------------
# VirusTotal links
# --------------------------------------------------------------------------------------------------------------

class VirusTotalTests(UploaderCase):
    def spec(self):
        return upload.release.load_spec(self.repo, "0.1.0")

    def test_links_follow_the_sha256_of_the_dist_dlls(self):
        links = upload.virustotal_links(self.dist, self.spec())
        self.assertEqual([(l["role"], l["name"], l["path"]) for l in links],
                         [("client", "maschine-ArenaMapPort.Client.dll", CLIENT_REL),
                          ("server", "maschine-ArenaMapPort.Server.dll", SERVER_REL)])
        self.assertEqual(links[0]["url"], VT + sha(self.dlls[CLIENT_REL]))
        self.assertEqual(links[1]["url"], VT + sha(self.dlls[SERVER_REL]))
        self.assertEqual(links[0]["sha256"], sha(self.dlls[CLIENT_REL]))

    def test_a_missing_dll_has_no_link(self):
        (self.dist / SERVER_REL).unlink()
        links = upload.virustotal_links(self.dist, self.spec())
        self.assertIsNone(links[1]["url"])
        self.assertIsNotNone(links[0]["url"])
        self.assertEqual(upload.virustotal_links(self.tmp / "no-dist", self.spec())[0]["url"], None)

    def test_a_missing_dll_does_not_stop_the_release_but_is_flagged(self):
        (self.dist / SERVER_REL).unlink()
        code, out, _err = self.cli()
        self.assertEqual(code, 0)
        self.assertIn("not found in dist", out)
        self.assertIn(f"WARNING: {SERVER_REL} not found in dist", out)

    def test_dist_option_selects_the_tree(self):
        other = self.tmp / "other-dist"
        data = {CLIENT_REL: b"another client", SERVER_REL: b"another server"}
        for rel, content in data.items():
            (other / rel).parent.mkdir(parents=True, exist_ok=True)
            (other / rel).write_bytes(content)
        code, out, _err = self.cli("--dist", str(other))
        self.assertEqual(code, 0)
        self.assertIn(VT + sha(data[CLIENT_REL]), out)
        self.assertNotIn(VT + sha(self.dlls[CLIENT_REL]), out)

    def test_a_dist_dll_that_differs_from_the_zip_is_warned_about(self):
        (self.dist / CLIENT_REL).write_bytes(b"rebuilt after packaging")
        code, out, _err = self.cli()
        self.assertEqual(code, 0)
        self.assertIn("WARNING: dist's maschine-ArenaMapPort.Client.dll differs from the one inside the release ZIP",
                      out)
        self.assertIn(VT + sha(b"rebuilt after packaging"), out)

    def test_matching_dlls_produce_no_warning(self):
        code, out, _err = self.cli()
        self.assertEqual(code, 0)
        self.assertNotIn("WARNING", out)


# --------------------------------------------------------------------------------------------------------------
# Key check before the first upload (an early 401 / 413 answer is lost on Windows)
# --------------------------------------------------------------------------------------------------------------

class KeyCheckTests(UploaderCase):
    def test_the_key_is_checked_once_before_the_first_upload(self):
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""), out)
        first = self.fake.calls[0]
        self.assertEqual((first.method, first.url), ("GET", "https://pixeldrain.com/api/user"))
        self.assertEqual(first.headers["Authorization"], KEY_AUTH)
        paths = [c.path for c in self.fake.calls]
        self.assertEqual(paths.count("/api/user"), 1)  # two files, one check
        self.assertLess(paths.index("/api/user"), paths.index(f"/api/file/{ZIP_NAME}"))
        self.assertIn("accepted the API key", out)

    def test_nothing_is_checked_when_nothing_is_uploaded(self):
        self.cli()
        self.fake.calls.clear()
        code, _out, _err = self.cli()
        self.assertEqual(code, 0)
        self.assertNotIn("/api/user", [c.path for c in self.fake.calls])

    def test_a_rejected_key_stops_the_run_before_any_file_is_sent(self):
        self.fake.user_script = [(401, {"success": False, "value": "authentication_failed", "message": "no"})]
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("rejected the API key (HTTP 401)", err)
        self.assertEqual(self.fake.by("PUT"), [])
        self.assertEqual(len(self.fake.calls), 1)  # final: not retried
        self.assertEqual(self.sleeps, [])
        self.assertFalse(self.record_path.exists())

    def test_other_client_errors_of_the_check_are_noted_and_ignored(self):
        self.fake.user_script = [(404, {"success": False, "value": "not_found", "message": "?"})]
        code, out, err = self.cli("--test-file", str(self.make_file(size=100)))
        self.assertEqual((code, err), (0, ""), out)
        self.assertIn("HTTP 404 to GET /api/user", out)
        self.assertEqual(len(self.fake.by("PUT")), 1)

    def test_a_server_error_of_the_check_is_retried(self):
        self.fake.user_script = [(503, {"success": False, "value": "unavailable", "message": "later"})]
        code, out, err = self.cli("--test-file", str(self.make_file(size=100)))
        self.assertEqual((code, err), (0, ""), out)
        self.assertEqual(self.sleeps, [1.0])

    def test_a_network_failure_of_the_check_ends_the_run_before_any_upload(self):
        self.fake.user_script = [urllib.error.URLError("down")] * 3
        code, _out, err = self.cli("--test-file", str(self.make_file(size=100)), "--retries", "2")
        self.assertEqual(code, 1)
        self.assertIn("key check failed after 3 attempt(s)", err)
        self.assertEqual(self.fake.by("PUT"), [])

    def test_a_reported_size_limit_below_the_file_size_is_warned_about(self):
        self.fake.user["subscription"]["file_size_limit"] = 100
        code, out, err = self.cli("--test-file", str(self.make_file(size=500)))
        self.assertEqual((code, err), (0, ""), out)
        self.assertIn("WARNING: probe.bin is", out)
        self.assertIn("HTTP 413 file_too_large", out)
        self.assertEqual(len(self.fake.by("PUT")), 1)  # the server decides, the tool still tries

    def test_no_warning_for_a_big_enough_missing_or_unusable_limit(self):
        for index, limit in enumerate((10_000, None, 0, -5, True, "big", 1.5)):
            self.fake.user["subscription"]["file_size_limit"] = limit
            code, out, err = self.cli("--test-file", str(self.make_file(size=500)), "--release-dir",
                                      str(self.tmp / f"limit-{index}"))
            self.assertEqual((code, err), (0, ""), limit)
            self.assertNotIn("WARNING", out, limit)

    def test_the_key_check_answer_is_not_printed(self):
        self.fake.user = {"username": "private-person", "email": "private@example.org", "subscription": {}}
        code, out, err = self.cli("--test-file", str(self.make_file(size=100)))
        self.assertEqual(code, 0)
        self.assertNotIn("private", out + err)
        echo = {"success": False, "value": "authentication_failed", "message": f"bad key {KEY} / {KEY_AUTH}"}
        self.fake.user_script = [(401, echo)]
        code, out, err = self.cli("--test-file", str(self.make_file(size=100)), "--release-dir",
                                  str(self.tmp / "other"))
        self.assertEqual(code, 1)
        for form in (KEY, KEY_AUTH, KEY_B64):
            self.assertNotIn(form, out + err)

    def test_a_hang_up_during_the_upload_names_the_possible_401_or_413(self):
        self.fake.script = [ConnectionAbortedError(10053, "software caused connection abort")] * 2
        code, _out, err = self.cli("--test-file", str(self.make_file(size=100)), "--retries", "1")
        self.assertEqual(code, 1)
        self.assertIn("2 attempt(s): network error ConnectionAbortedError", err)
        self.assertIn("HTTP 401", err)
        self.assertIn("HTTP 413", err)
        self.assertEqual(len(self.fake.by("PUT")), 2)  # the answer was lost, so nothing is known: still retried

    def test_other_network_errors_get_no_such_hint(self):
        self.fake.script = [urllib.error.URLError("down")]
        code, _out, err = self.cli("--test-file", str(self.make_file(size=100)), "--retries", "0")
        self.assertEqual(code, 1)
        self.assertNotIn("413", err)

    def test_a_hang_up_on_a_small_request_gets_no_hint_either(self):
        self.cli()
        self.fake.calls.clear()
        self.fake.script = [ConnectionResetError("reset")] * 2
        code, _out, err = self.cli("--retries", "1")
        self.assertEqual(code, 1)
        self.assertIn("network error ConnectionResetError", err)
        self.assertNotIn("413", err)


class LoopbackServer:
    """A real plain-HTTP server on 127.0.0.1 standing in for pixeldrain.com. Like the server of the review
    reproduction it answers a PUT at once WITHOUT reading the body and then closes the connection, which is what
    makes Windows reset the client's connection and discard the answer."""

    def __init__(self, user_status=200, put_status=201, answer_delay=0.0):
        owner = self
        self.requests = []
        self.user_status = user_status
        self.put_status = put_status
        self.answer_delay = answer_delay

        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *_args):
                pass

            def reply(self, status, payload):
                data = json.dumps(payload).encode()
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(data)
                self.wfile.flush()
                self.close_connection = True

            def do_GET(self):
                owner.requests.append(("GET", self.path))
                if self.path == "/api/user" and owner.user_status == 200:
                    self.reply(200, {"username": "tester", "subscription": {"name": "Pro"}})
                elif self.path == "/api/user":
                    self.reply(owner.user_status, {"success": False, "value": "authentication_failed", "message": "x"})
                else:
                    self.reply(404, {"success": False, "value": "not_found", "message": "x"})

            def do_PUT(self):
                owner.requests.append(("PUT", self.path))
                if owner.answer_delay or owner.put_status == 201:  # it takes the whole body first (and thinks)
                    self.rfile.read(int(self.headers["Content-Length"]))
                    time.sleep(owner.answer_delay)
                if owner.put_status == 201:
                    self.reply(201, {"success": True, "id": "LOOP1"})
                else:
                    self.reply(owner.put_status, {"success": False, "value": "file_too_large", "message": "too big"})

        class Quiet(http.server.ThreadingHTTPServer):
            daemon_threads = True

            def handle_error(self, request, client_address):  # the client hanging up is the point of the test
                pass

        self.server = Quiet(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def connect(self, _host, _port, **kwargs):
        """Stands in for http.client.HTTPSConnection: the same call, but plain HTTP to the loopback server."""
        kwargs.pop("context", None)
        return http.client.HTTPConnection("127.0.0.1", self.port, **kwargs)

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(5)


class RealSocketTests(UploaderCase):
    """http_request over real sockets (HTTPS swapped for loopback HTTP): the behaviour that mocks hide."""

    def start_server(self, **kwargs):
        server = LoopbackServer(**kwargs)
        self.addCleanup(server.close)
        return server

    def tool(self, server, *argv):
        with patch.object(upload.http.client, "HTTPSConnection", server.connect):
            return self.cli(*argv, runtime=self.runtime(transport=None))  # transport=None: the real http_request

    def test_a_rejected_key_is_reported_although_the_server_hangs_up_on_the_upload(self):
        server = self.start_server(user_status=401, put_status=401)
        code, out, err = self.tool(server, "--test-file", str(self.make_file(size=8 * 2 ** 20)), "--retries", "0")
        self.assertEqual(code, 1)
        self.assertIn("rejected the API key (HTTP 401)", err)
        self.assertEqual([method for method, _path in server.requests], ["GET"])  # no body was ever sent
        for form in (KEY, KEY_AUTH, KEY_B64):
            self.assertNotIn(form, out + err)

    def test_an_early_413_is_named_or_at_least_hinted_never_just_a_broken_pipe(self):
        server = self.start_server(put_status=413)
        code, out, err = self.tool(server, "--test-file", str(self.make_file(size=8 * 2 ** 20)), "--retries", "1")
        self.assertEqual(code, 1)
        self.assertIn("413", err)  # the answer itself where the system delivers it, else the hint about it
        if "network error" in err:
            self.assertIn("per-file size limit", err)
        self.assertLessEqual([method for method, _path in server.requests].count("PUT"), 2)
        for form in (KEY, KEY_AUTH, KEY_B64):
            self.assertNotIn(form, out + err)

    def test_a_good_upload_over_a_real_socket(self):
        server = self.start_server()
        path = self.make_file(size=3 * 2 ** 20)
        with patch.object(upload.http.client, "HTTPSConnection", server.connect):
            api = upload.Api(upload.Secret(KEY), upload.http_request, timeout=10, retries=0,
                             sleep=lambda _s: None, log=lambda _m: None)
            self.assertEqual(api.put_file(upload.local_file(path)), "LOOP1")
        self.assertEqual([method for method, _path in server.requests], ["GET", "PUT"])

    def test_no_answer_after_the_whole_file_was_sent_is_not_uploaded_again(self):
        server = self.start_server(answer_delay=3.0)
        code, _out, err = self.tool(server, "--test-file", str(self.make_file(size=200_000)), "--timeout", "0.5",
                                    "--retries", "3")
        self.assertEqual(code, 1)
        self.assertIn("did not answer within 0.5 s", err)
        self.assertIn("not retried", err)
        self.assertEqual([method for method, _path in server.requests].count("PUT"), 1)
        self.assertEqual(self.sleeps, [])


# --------------------------------------------------------------------------------------------------------------
# An upload is never lost from the record
# --------------------------------------------------------------------------------------------------------------

def unavailable(original, method, url, headers, body, timeout):
    """Every GET info fails with a 503; everything else behaves."""
    if method == "GET" and url.endswith("/info"):
        return 503, {"success": False, "value": "unavailable", "message": "later"}
    return original(method, url, headers, body, timeout)


class PendingUploadTests(UploaderCase):
    def test_an_upload_is_recorded_before_it_is_verified_and_reused_by_the_next_run(self):
        code, _out, err = self.cli(runtime=self.with_transport(unavailable))
        self.assertEqual(code, 1)
        self.assertIn("info F1 failed after 4 attempt(s)", err)
        self.assertIn("stored as F1", err)
        self.assertEqual(len(self.fake.by("PUT")), 1)
        record = self.record()
        self.assertEqual(record["files"], [])
        self.assertEqual([(f["name"], f["id"], f["sha256"]) for f in record["other_files"]],
                         [(ZIP_NAME, "F1", sha(self.zip_bytes))])
        self.fake.calls.clear()
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""), out)
        self.assertEqual([c.path for c in self.fake.by("PUT")], [f"/api/file/{SHA_NAME}"])  # the ZIP: not again
        record = self.record()
        self.assertEqual([(f["name"], f["id"]) for f in record["files"]], [(ZIP_NAME, "F1"), (SHA_NAME, "F2")])
        self.assertNotIn("other_files", record)

    def test_a_hash_that_never_appears_keeps_the_upload_for_the_next_run(self):
        self.fake.hide_hash_polls = 99
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("no hash_sha256", err)
        self.assertIn("run again later", err)
        self.assertEqual([f["id"] for f in self.record()["other_files"]], ["F1"])
        self.fake.calls.clear()
        code, _out, err = self.cli()  # still no hash: do not upload 2 GiB again on a guess
        self.assertEqual(code, 1)
        self.assertIn("cannot be confirmed", err)
        self.assertIn("--force", err)
        self.assertEqual(self.fake.by("PUT"), [])
        self.fake.hide_hash_polls = 0  # Pixeldrain has caught up
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""), out)
        self.assertEqual([c.path for c in self.fake.by("PUT")], [f"/api/file/{SHA_NAME}"])
        self.assertEqual([f["id"] for f in self.record()["files"]], ["F1", "F2"])

    def test_a_corrupt_upload_stays_in_the_record_as_a_tracked_orphan_and_is_replaced(self):
        self.fake.corrupt_next = "hash"
        self.assertEqual(self.cli()[0], 1)
        self.fake.calls.clear()
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""), out)
        self.assertIn("differs", out)
        record = self.record()
        self.assertEqual([f["id"] for f in record["files"]], ["F2", "F3"])
        self.assertEqual([f["id"] for f in record["other_files"]], ["F1"])  # the bad copy is still named

    def test_a_missing_answer_after_the_whole_file_was_sent_is_not_retried(self):
        self.fake.script = [upload.AnswerTimeout("no answer")]
        code, _out, err = self.cli("--test-file", str(self.make_file(size=100)))  # default retries: 3
        self.assertEqual(code, 1)
        self.assertIn("did not answer within 120 s", err)
        self.assertIn("may be stored already", err)
        self.assertIn("not retried", err)
        self.assertEqual((len(self.fake.by("PUT")), self.sleeps), (1, []))

    def test_a_missing_answer_to_a_small_request_is_retried(self):
        self.fake.user_script = [upload.AnswerTimeout("slow")]
        code, out, err = self.cli("--test-file", str(self.make_file(size=100)))
        self.assertEqual((code, err), (0, ""), out)
        self.assertEqual(self.sleeps, [1.0])

    def test_a_timeout_while_waiting_for_the_answer_is_an_answer_timeout(self):
        class Slow(connection_class()):
            def getresponse(self):
                raise socket.timeout("timed out")

        with patch.object(upload.http.client, "HTTPSConnection", Slow):
            with self.assertRaises(upload.AnswerTimeout):
                upload.http_request("PUT", "https://pixeldrain.com/api/file/x", {}, b"x", 1)

    def test_a_timeout_while_sending_stays_a_plain_timeout(self):
        cls = connection_class(send_error=socket.timeout("timed out"))
        with patch.object(upload.http.client, "HTTPSConnection", cls):
            with self.assertRaises(TimeoutError) as caught:
                upload.http_request("PUT", "https://pixeldrain.com/api/file/x", {}, b"x", 1)
        self.assertNotIsInstance(caught.exception, upload.AnswerTimeout)


# --------------------------------------------------------------------------------------------------------------
# The record never forgets a list or a file
# --------------------------------------------------------------------------------------------------------------

class RecordKeepingTests(UploaderCase):
    def test_a_no_list_run_keeps_the_recorded_list_and_a_later_run_reuses_it(self):
        self.cli()
        before = self.record_path.read_bytes()
        code, out, err = self.cli("--no-list")
        self.assertEqual((code, err), (0, ""), out)
        self.assertEqual(self.record_path.read_bytes(), before)  # list L3 is still there
        self.assertIn("https://pixeldrain.com/l/L3", out)
        code, _out, _err = self.cli()
        self.assertEqual(code, 0)
        self.assertEqual(sorted(self.fake.lists), ["L3"])  # never a second list for unchanged files
        self.assertEqual(self.record()["list"]["id"], "L3")

    def test_a_no_list_run_that_changes_the_files_moves_the_stale_list_aside(self):
        self.cli()
        del self.fake.files["F1"]  # the ZIP vanished from Pixeldrain: even --no-list uploads it again
        code, out, err = self.cli("--no-list")
        self.assertEqual((code, err), (0, ""), out)
        record = self.record()
        self.assertEqual([f["id"] for f in record["files"]], ["F4", "F2"])
        self.assertIsNone(record["list"])
        self.assertEqual(record["other_lists"], [{"id": "L3", "url": "https://pixeldrain.com/l/L3",
                                                  "title": "ArenaMapPort 0.1.0", "file_ids": ["F1", "F2"]}])
        self.assertEqual([f["id"] for f in record["other_files"]], ["F1"])
        self.assertNotIn("Download everything", out)  # a list of the old files is not announced

    def test_a_run_that_dies_after_a_reupload_does_not_forget_the_old_list(self):
        self.cli()
        del self.fake.files["F1"]

        def flaky(original, method, url, headers, body, timeout):
            if method == "GET" and url.endswith("/api/file/F2/info"):
                return 503, {"success": False, "value": "unavailable", "message": "later"}
            return original(method, url, headers, body, timeout)

        code, _out, _err = self.cli("--retries", "0", runtime=self.with_transport(flaky))
        self.assertEqual(code, 1)
        record = self.record()  # written by the checkpoint after the ZIP was uploaded again
        self.assertIsNone(record["list"])
        self.assertEqual([item["id"] for item in record["other_lists"]], ["L3"])
        self.fake.calls.clear()
        code, out, _err = self.cli()
        self.assertEqual(code, 0, out)
        self.assertEqual(self.fake.by("PUT"), [])
        record = self.record()
        self.assertEqual(record["list"]["id"], "L5")
        self.assertEqual([item["id"] for item in record["other_lists"]], ["L3"])

    def test_a_different_title_keeps_the_old_list_and_returning_to_it_reuses_it(self):
        self.cli()
        self.cli("--title", "Another title")
        record = self.record()
        self.assertEqual((record["list"]["id"], record["list"]["title"]), ("L4", "Another title"))
        self.assertEqual([(i["id"], i["title"]) for i in record["other_lists"]], [("L3", "ArenaMapPort 0.1.0")])
        self.fake.calls.clear()
        code, _out, _err = self.cli()
        self.assertEqual(code, 0)
        self.assertEqual(self.fake.by("POST"), [])
        record = self.record()
        self.assertEqual(record["list"]["id"], "L3")
        self.assertEqual([item["id"] for item in record["other_lists"]], ["L4"])

    def test_a_files_subset_keeps_the_dropped_entry_and_the_old_list(self):
        self.cli()
        code, out, err = self.cli("--files", ZIP_NAME)
        self.assertEqual((code, err), (0, ""), out)
        record = self.record()
        self.assertEqual([f["name"] for f in record["files"]], [ZIP_NAME])
        self.assertEqual([f["name"] for f in record["other_files"]], [SHA_NAME])
        self.assertEqual(record["list"]["id"], "L4")  # a list for exactly the subset
        self.assertEqual([item["id"] for item in record["other_lists"]], ["L3"])
        code, out, _err = self.cli("--dry-run", runtime=self.runtime(transport=None))
        self.assertIn("recorded as F2", out)
        self.fake.calls.clear()
        code, _out, _err = self.cli()  # both files again: nothing uploaded, the first list is back
        self.assertEqual(code, 0)
        self.assertEqual((self.fake.by("PUT"), self.fake.by("POST")), ([], []))
        record = self.record()
        self.assertEqual(record["list"]["id"], "L3")
        self.assertEqual([item["id"] for item in record["other_lists"]], ["L4"])
        self.assertNotIn("other_files", record)

    def test_force_keeps_the_replaced_uploads_and_their_list(self):
        self.cli()
        self.cli("--force")
        record = self.record()
        self.assertEqual([f["id"] for f in record["files"]], ["F4", "F5"])
        self.assertEqual([f["id"] for f in record["other_files"]], ["F1", "F2"])
        self.assertEqual(record["list"]["id"], "L6")
        self.assertEqual([item["id"] for item in record["other_lists"]], ["L3"])

    def test_the_extra_sections_are_validated_when_a_record_is_read(self):
        self.cli()
        self.cli("--files", ZIP_NAME)  # a record with other_files and other_lists
        good = self.record()
        self.assertIn("other_files", good)
        self.assertIn("other_lists", good)
        for broken in ({"other_files": [{"name": 1}]}, {"other_files": "no"}, {"other_lists": [{"id": "L3"}]},
                       {"other_lists": [{"id": "L3", "title": "t", "file_ids": [5]}]}, {"other_lists": 7}):
            self.record_path.write_text(json.dumps({**good, **broken}), encoding="utf-8")
            code, _out, err = self.cli()
            self.assertEqual(code, 1, broken)
            self.assertIn("unusable", err)


# --------------------------------------------------------------------------------------------------------------
# The documented defaults
# --------------------------------------------------------------------------------------------------------------

class DefaultsTests(UploaderCase):
    def test_default_options(self):
        args = upload.parse_args([])
        self.assertEqual((args.retries, args.timeout, args.key_env), (3, 120.0, "PIXELDRAIN_API_KEY"))

    def test_a_default_run_survives_three_failures_with_the_documented_backoff(self):
        self.fake.script = [urllib.error.URLError("down")] * 3
        code, out, err = self.cli("--test-file", str(self.make_file(size=100)))  # no --retries, no --timeout
        self.assertEqual((code, err), (0, ""), out)
        self.assertEqual(self.sleeps, [1.0, 2.0, 4.0])
        self.assertEqual(len(self.fake.by("PUT")), 4)

    def test_a_default_run_gives_up_after_four_attempts(self):
        self.fake.script = [urllib.error.URLError("down")] * 10
        code, _out, err = self.cli("--test-file", str(self.make_file(size=100)))
        self.assertEqual(code, 1)
        self.assertIn("4 attempt(s)", err)
        self.assertEqual(len(self.fake.by("PUT")), 4)
        self.assertEqual(self.sleeps, [1.0, 2.0, 4.0])

    def test_the_backoff_is_capped(self):
        self.fake.script = [urllib.error.URLError("down")] * 10
        self.cli("--test-file", str(self.make_file(size=100)), "--retries", "5")
        self.assertEqual(self.sleeps, [1.0, 2.0, 4.0, upload.MAX_BACKOFF, upload.MAX_BACKOFF])


# --------------------------------------------------------------------------------------------------------------
# Module facts
# --------------------------------------------------------------------------------------------------------------

class ModuleTests(unittest.TestCase):
    def test_help_works_and_the_docstring_documents_the_interface(self):
        out = io.StringIO()
        with redirect_stdout(out), self.assertRaises(SystemExit) as caught:
            upload.parse_args(["--help"])
        self.assertEqual(caught.exception.code, 0)
        for option in ("--release-dir", "--version", "--files", "--title", "--no-list", "--dry-run", "--force",
                       "--skip-verify", "--test-file", "--key-env", "--key-file", "--timeout", "--retries"):
            self.assertIn(option, out.getvalue())
        for needle in ("PUT  /api/file/{name}", "GET  /api/file/{id}/info", "POST /api/list", "EMPTY user name",
                       "pixeldrain.json", "Machine scope"):
            self.assertIn(needle, upload.__doc__)

    def test_urls(self):
        self.assertEqual(upload.file_url("abc"), "https://pixeldrain.com/u/abc")
        self.assertEqual(upload.direct_url("abc"), "https://pixeldrain.com/api/file/abc?download")
        self.assertEqual(upload.list_url("xyz"), "https://pixeldrain.com/l/xyz")

    def test_the_module_imports_nothing_outside_the_standard_library(self):
        source = (REPO / "tools" / "release" / "upload_pixeldrain.py").read_text(encoding="utf-8")
        self.assertNotIn("import requests", source)
        self.assertNotIn("urlopen", source)


# --------------------------------------------------------------------------------------------------------------
# The real packager on a multi-part set (the single-ZIP layout is covered by the mocked verify_set above)
# --------------------------------------------------------------------------------------------------------------

@unittest.skipIf(packager_tests is None, "test_release_zips is not importable")
class RealVerifyTests(packager_tests.FakeRepoCase if packager_tests else unittest.TestCase):
    def test_a_real_multi_part_set_is_verified_and_uploaded_part_by_part(self):
        result = self.build()
        names = [p.name for p in result.zips] + [result.sha256_path.name]
        fake = FakePixeldrain()
        out, err = io.StringIO(), io.StringIO()
        runtime = upload.Runtime(transport=fake, environ={"PIXELDRAIN_API_KEY": KEY}, platform="linux",
                                 sleep=lambda _s: None, clock=lambda: NOW)
        with redirect_stdout(out), redirect_stderr(err):
            code = upload.main(["--release-dir", str(result.out_dir), "--files", *names, "--dist", str(self.dist)],
                               repo=self.repo, runtime=runtime)
        self.assertEqual((code, err.getvalue()), (0, ""), out.getvalue())
        self.assertIn("verified part 1 of 4", out.getvalue())
        self.assertEqual(len(fake.by("PUT")), 5)
        record = json.loads((result.out_dir / "pixeldrain.json").read_text(encoding="utf-8"))
        self.assertEqual(record["source_commit"], "a" * 40)
        self.assertEqual([f["name"] for f in record["files"]], names)
        self.assertEqual(record["files"][0]["sha256"], result.digests[0])
        self.assertNotIn("WARNING", out.getvalue())  # dist DLLs equal the ones packaged

    def test_a_real_single_zip_is_verified_and_uploaded_with_the_default_selection(self):
        import inspect
        if "single" not in inspect.signature(upload.release.build_release).parameters:
            self.skipTest("the packager in the tree has no single-ZIP layout yet")
        result = self.build(single=True)
        self.assertEqual([p.name for p in result.zips], [f"maschine-ArenaMapPort-{self.spec.version}.zip"])
        fake = FakePixeldrain()
        out, err = io.StringIO(), io.StringIO()
        runtime = upload.Runtime(transport=fake, environ={"PIXELDRAIN_API_KEY": KEY}, platform="linux",
                                 sleep=lambda _s: None, clock=lambda: NOW)
        with redirect_stdout(out), redirect_stderr(err):
            code = upload.main(["--release-dir", str(result.out_dir), "--dist", str(self.dist)],
                               repo=self.repo, runtime=runtime)
        self.assertEqual((code, err.getvalue()), (0, ""), out.getvalue())
        self.assertEqual([c.path for c in fake.by("PUT")],
                         [f"/api/file/{result.zips[0].name}", f"/api/file/{result.sha256_path.name}"])
        record = json.loads((result.out_dir / "pixeldrain.json").read_text(encoding="utf-8"))
        self.assertEqual(record["source_commit"], "a" * 40)
        self.assertEqual(record["files"][0]["sha256"], result.digests[0])
        self.assertEqual(record["list"]["title"], f"ArenaMapPort {self.spec.version}")
        self.assertNotIn("WARNING", out.getvalue())

    def test_a_tampered_part_stops_the_upload(self):
        result = self.build()
        data = bytearray(result.zips[1].read_bytes())
        data[len(data) // 2] ^= 0xFF
        result.zips[1].write_bytes(bytes(data))
        fake = FakePixeldrain()
        err = io.StringIO()
        runtime = upload.Runtime(transport=fake, environ={"PIXELDRAIN_API_KEY": KEY}, platform="linux",
                                 sleep=lambda _s: None, clock=lambda: NOW)
        names = [p.name for p in result.zips] + [result.sha256_path.name]
        with redirect_stdout(io.StringIO()), redirect_stderr(err):
            code = upload.main(["--release-dir", str(result.out_dir), "--files", *names], repo=self.repo,
                               runtime=runtime)
        self.assertEqual(code, 1)
        self.assertEqual(fake.calls, [])


if __name__ == "__main__":
    unittest.main()
