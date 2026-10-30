"""The VirusTotal release scanner: key resolution (file, environment, registry), the x-apikey header, the lookup by
hash, the upload with its multipart body, the analysis polling, the request pacer (4 per minute across all requests),
429 / 401 / network handling, the 32 MiB refusal, dry run, exit codes, the record and the Markdown block, and the
guarantee that the API key never reaches any output. No test touches the network: every HTTP call goes through an
in-memory FakeVirusTotal behind the scanner's transport signature, time is a virtual clock that sleeping advances,
and the registry is a fake winreg."""
from contextlib import redirect_stderr, redirect_stdout
from datetime import datetime, timezone
import base64
import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch
import urllib.error
import urllib.parse

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "release"))
import scan_virustotal as scan

KEY = "vt-test-KEY-0123456789abcdef0123456789abcdef"
KEY_B64 = base64.b64encode(KEY.encode()).decode()
NOW = datetime(2026, 10, 16, 12, 0, 0, tzinfo=timezone.utc)
ANALYSIS_TS = int(datetime(2026, 10, 16, 10, 0, 0, tzinfo=timezone.utc).timestamp())
ANALYSIS_ISO = "2026-10-16T10:00:00Z"
OLD_TS = ANALYSIS_TS - 86400
PLUGIN = "BepInEx/plugins/maschine-ArenaMapPort"
SERVER = "SPT_Runtime/user/mods/ArenaMapPort"
CLIENT_REL = f"{PLUGIN}/maschine-ArenaMapPort.Client.dll"
SERVER_REL = f"{SERVER}/maschine-ArenaMapPort.Server.dll"
CLIENT_NAME = "maschine-ArenaMapPort.Client.dll"
SERVER_NAME = "maschine-ArenaMapPort.Server.dll"
GUI = "https://www.virustotal.com/gui/file/"
CLEAN = {"harmless": 0, "malicious": 0, "suspicious": 0, "undetected": 66, "timeout": 0, "type-unsupported": 6,
         "failure": 0, "confirmed-timeout": 0}  # 72 engines


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def csproj(assembly, version):
    return (f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AssemblyName>{assembly}</AssemblyName>'
            f'<Version>{version}</Version></PropertyGroup></Project>')


def error_body(code, message):
    return {"error": {"code": code, "message": message}}


def file_body(digest, stats, date=ANALYSIS_TS):
    return {"data": {"type": "file", "id": digest,
                     "attributes": {"sha256": digest, "last_analysis_stats": stats, "last_analysis_date": date}}}


# --------------------------------------------------------------------------------------------------------------
# Fakes
# --------------------------------------------------------------------------------------------------------------

class VirtualClock:
    """Monotonic time that only sleeping advances."""

    def __init__(self):
        self.t = 1000.0
        self.sleeps = []

    def monotonic(self):
        return self.t

    def sleep(self, seconds):
        self.sleeps.append(seconds)
        self.t += seconds


def parse_multipart(content_type, body):
    """An independent reader of the one-part multipart bodies the scanner sends."""
    found = re.fullmatch(r"multipart/form-data; boundary=([^\s;]+)", content_type)
    assert found, content_type
    delimiter = b"--" + found.group(1).encode("ascii")
    assert body.startswith(delimiter + b"\r\n")
    tail = b"\r\n" + delimiter + b"--\r\n"
    assert body.endswith(tail)
    head, separator, rest = body[len(delimiter) + 2:].partition(b"\r\n\r\n")
    assert separator
    headers = dict(line.split(": ", 1) for line in head.decode("utf-8").split("\r\n"))
    disposition = re.fullmatch(r'form-data; name="([^"]*)"; filename="(.*)"', headers["Content-Disposition"])
    assert disposition, headers
    return SimpleNamespace(boundary=found.group(1), field=disposition.group(1), filename=disposition.group(2),
                           data=rest[:-len(tail)], headers=headers, delimiter_count=body.count(delimiter))


class Call:
    def __init__(self, method, url, headers, body, timeout, at):
        self.method = method
        self.url = url
        self.headers = headers
        self.body = body
        self.timeout = timeout
        self.at = at

    @property
    def path(self):
        return urllib.parse.urlsplit(self.url).path


class FakeVirusTotal:
    """VirusTotal behind transport(method, url, headers, body, timeout) -> (status, data). `script` holds outcomes
    for the next calls (an exception to raise, or a (status, data) tuple) before the normal behaviour resumes."""

    def __init__(self, clock, key=KEY):
        self.clock = clock
        self.key = key
        self.calls = []
        self.known = {}                  # sha256 -> file attributes
        self.analyses = {}
        self.uploads = []
        self.script = []
        self.counter = 0
        self.statuses = ["queued", "in-progress", "completed"]   # what the next upload's analysis goes through
        self.upload_stats = dict(CLEAN)
        self.keep_file_stale = False     # a completed analysis does not refresh the file object
        self.meta_inside_data = False
        self.unregistered_polls = 0      # answer 404 to that many analysis polls first (the analysis registers late)

    def __call__(self, method, url, headers, body, timeout):
        call = Call(method, url, dict(headers), body, timeout, self.clock.t)
        self.calls.append(call)
        if self.script:
            outcome = self.script.pop(0)
            if isinstance(outcome, BaseException):
                raise outcome
            return outcome
        if headers.get("x-apikey") != self.key:
            return 401, error_body("WrongCredentialsError", "Wrong API key")
        path = call.path
        if method == "GET" and path.startswith("/api/v3/files/"):
            digest = path.rsplit("/", 1)[1]
            if digest in self.known:
                return 200, {"data": {"type": "file", "id": digest, "attributes": dict(self.known[digest])}}
            return 404, error_body("NotFoundError", f"File \"{digest}\" not found")
        if method == "POST" and path == "/api/v3/files":
            parsed = parse_multipart(headers["Content-Type"], body)
            self.uploads.append(parsed)
            self.counter += 1
            analysis_id = f"AnalysisId{self.counter}-x=="
            self.analyses[analysis_id] = {"sha": sha(parsed.data), "size": len(parsed.data),
                                          "statuses": list(self.statuses), "stats": dict(self.upload_stats)}
            return 200, {"data": {"type": "analysis", "id": analysis_id}}
        if method == "GET" and path.startswith("/api/v3/analyses/"):
            analysis_id = urllib.parse.unquote(path[len("/api/v3/analyses/"):])
            if self.unregistered_polls > 0:
                self.unregistered_polls -= 1
                return 404, error_body("NotFoundError", f"Analysis \"{analysis_id}\" not found")
            analysis = self.analyses.get(analysis_id)
            if analysis is None:
                return 404, error_body("NotFoundError", "analysis not found")
            status = analysis["statuses"].pop(0) if len(analysis["statuses"]) > 1 else analysis["statuses"][0]
            done = status == "completed"
            if done and not self.keep_file_stale:
                self.known[analysis["sha"]] = {"sha256": analysis["sha"], "size": analysis["size"],
                                               "last_analysis_stats": dict(analysis["stats"]),
                                               "last_analysis_date": ANALYSIS_TS,
                                               "last_analysis_results": {"EngineA": {"category": "undetected"}}}
            info = {"sha256": analysis["sha"], "size": analysis["size"]}
            answer = {"data": {"type": "analysis", "id": analysis_id,
                               "attributes": {"status": status, "date": ANALYSIS_TS - 30,
                                              "stats": dict(analysis["stats"]) if done else {}}}}
            if self.meta_inside_data:
                answer["data"]["meta"] = {"file_info": info}
            else:
                answer["meta"] = {"file_info": info}
            return 200, answer
        raise AssertionError(f"unexpected call {method} {path}")

    def by(self, method, prefix):
        return [c for c in self.calls if c.method == method and c.path.startswith("/api/v3/" + prefix)]

    def know(self, data: bytes, stats=None, date=ANALYSIS_TS):
        digest = sha(data)
        self.known[digest] = {"sha256": digest, "size": len(data), "last_analysis_stats": dict(stats or CLEAN),
                              "last_analysis_date": date}
        return digest


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


def connection_class(status=200, payload=b'{"data": {}}', record=None):
    record = record if record is not None else {}

    class Response:
        def __init__(self):
            self.status = status

        def read(self, amount=-1):
            record["read_amount"] = amount
            return payload if amount < 0 else payload[:amount]

    class Connection:
        def __init__(self, host, port, **kwargs):
            record["init"] = (host, port, kwargs)

        def request(self, method, target, body=None, headers=None):
            record["request"] = (method, target, body, headers)

        def getresponse(self):
            return Response()

        def close(self):
            record["closed"] = True

    return Connection


# --------------------------------------------------------------------------------------------------------------
# Fixture: a fake repo with two DLLs in dist
# --------------------------------------------------------------------------------------------------------------

class ScanCase(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.tmp = Path(temporary.name)
        self.repo = self.tmp / "repo"
        for project, assembly in (("ArenaMapPort.Client", "maschine-ArenaMapPort.Client"),
                                  ("ArenaMapPort.Server", "maschine-ArenaMapPort.Server")):
            (self.repo / project).mkdir(parents=True)
            (self.repo / project / f"{project}.csproj").write_text(csproj(assembly, "0.2.0"), encoding="utf-8")
        self.dist = self.repo / "dist"
        self.dlls = {CLIENT_REL: b"client dll bytes " * 60, SERVER_REL: b"server dll bytes " * 45}
        for rel, data in self.dlls.items():
            (self.dist / rel).parent.mkdir(parents=True, exist_ok=True)
            (self.dist / rel).write_bytes(data)
        self.client_sha = sha(self.dlls[CLIENT_REL])
        self.server_sha = sha(self.dlls[SERVER_REL])
        self.record_path = self.repo / "work" / "releases" / "0.2.0" / "virustotal.json"
        self.files_record_path = self.record_path.with_name("virustotal-files.json")  # default of explicit FILEs
        self.clock = VirtualClock()
        self.fake = FakeVirusTotal(self.clock)

    def runtime(self, **overrides):
        values = dict(transport=self.fake, environ={"VIRUSTOTAL_API_KEY": KEY}, platform="linux", winreg=None,
                      sleep=self.clock.sleep, monotonic=self.clock.monotonic, clock=lambda: NOW)
        values.update(overrides)
        return scan.Runtime(**values)

    def cli(self, *argv, runtime=None, repo=None):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = scan.main(list(argv), repo=repo or self.repo, runtime=runtime or self.runtime())
        return code, out.getvalue(), err.getvalue()

    def record(self, path=None):
        return json.loads((path or self.record_path).read_text(encoding="utf-8"))

    def assert_no_record(self):
        self.assertFalse(self.record_path.exists())
        self.assertFalse(self.files_record_path.exists())

    def know_both(self, client_stats=None, server_stats=None):
        self.fake.know(self.dlls[CLIENT_REL], client_stats)
        self.fake.know(self.dlls[SERVER_REL], server_stats)

    def snapshot_tree(self, root: Path):
        return sorted(p.relative_to(root).as_posix() for p in root.rglob("*")) if root.exists() else None


# --------------------------------------------------------------------------------------------------------------
# The key
# --------------------------------------------------------------------------------------------------------------

class SecretTests(unittest.TestCase):
    def test_secret_never_prints_its_value(self):
        secret = scan.Secret(KEY)
        for text in (repr(secret), str(secret), f"{secret}", f"{secret!r}", "%s" % secret):
            self.assertNotIn(KEY, text)
        client = scan.Client(secret, lambda *a: (200, {}), timeout=1, sleep=lambda _s: None,
                             monotonic=lambda: 0.0, log=print)
        self.assertNotIn(KEY, repr(client))

    def test_redact_removes_the_key_and_its_base64_form(self):
        cleaned = scan.redact(f"raw {KEY} encoded {KEY_B64}", scan.Secret(KEY))
        self.assertNotIn(KEY, cleaned)
        self.assertNotIn(KEY_B64, cleaned)
        self.assertEqual(scan.redact("nothing here", None), "nothing here")

    def test_clean_text_is_printable_short_and_key_free(self):
        text = scan._clean("line\x1b[31mred\r\n" + KEY + "x" * 1000, scan.Secret(KEY), limit=50)
        self.assertNotIn(KEY, text)
        self.assertFalse(re.search(r"[\x00-\x1f]", text))
        self.assertLessEqual(len(text), 53)


class KeyResolutionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.tmp = Path(temporary.name)

    def resolve(self, key_file=None, environ=None, platform="linux", winreg=None, key_env="VIRUSTOTAL_API_KEY"):
        secret, source = scan.resolve_key(key_file, key_env, environ or {}, platform, winreg)
        return secret.reveal(), source

    def test_key_from_the_environment(self):
        value, source = self.resolve(environ={"VIRUSTOTAL_API_KEY": f"  {KEY}\n"})
        self.assertEqual((value, source), (KEY, "environment variable VIRUSTOTAL_API_KEY"))

    def test_key_env_names_another_variable(self):
        value, _source = self.resolve(environ={"VIRUSTOTAL_API_KEY": "other", "MY_KEY": KEY}, key_env="MY_KEY")
        self.assertEqual(value, KEY)

    def test_key_file_wins_and_is_stripped(self):
        path = self.tmp / "key.txt"
        path.write_bytes(b"\xef\xbb\xbf" + KEY.encode() + b"\r\n")  # PowerShell 5 style: BOM and CRLF
        value, source = self.resolve(key_file=path, environ={"VIRUSTOTAL_API_KEY": "from-env"})
        self.assertEqual(value, KEY)
        self.assertEqual(source, f"--key-file {path}")

    def test_unusable_key_files(self):
        missing = self.tmp / "none.txt"
        with self.assertRaisesRegex(scan.ScanError, "cannot read --key-file"):
            self.resolve(key_file=missing)
        empty = self.tmp / "empty.txt"
        empty.write_text("  \n", encoding="ascii")
        with self.assertRaisesRegex(scan.ScanError, "is empty"):
            self.resolve(key_file=empty)
        utf16 = self.tmp / "utf16.txt"
        utf16.write_bytes(KEY.encode("utf-16"))
        with self.assertRaisesRegex(scan.ScanError, "UTF-16"):
            self.resolve(key_file=utf16)

    def test_a_key_with_blanks_is_refused_without_showing_it(self):
        with self.assertRaises(scan.ScanError) as caught:
            self.resolve(environ={"VIRUSTOTAL_API_KEY": "two words"})
        self.assertNotIn("two words", str(caught.exception))

    def test_registry_is_read_user_first_then_machine_after_the_environment(self):
        winreg = FakeWinreg(user={"VIRUSTOTAL_API_KEY": "user-key"}, machine={"VIRUSTOTAL_API_KEY": "machine-key"})
        value, source = self.resolve(environ={}, platform="win32", winreg=winreg)
        self.assertEqual((value, source), ("user-key", "registry variable VIRUSTOTAL_API_KEY (User scope)"))
        value, _source = self.resolve(environ={"VIRUSTOTAL_API_KEY": "env-key"}, platform="win32", winreg=winreg)
        self.assertEqual(value, "env-key")

    def test_the_machine_scope_is_used_when_the_user_scope_has_nothing(self):
        winreg = FakeWinreg(user={"VIRUSTOTAL_API_KEY": "  "}, machine={"VIRUSTOTAL_API_KEY": KEY})
        value, source = self.resolve(environ={"VIRUSTOTAL_API_KEY": "   "}, platform="win32", winreg=winreg)
        self.assertEqual((value, source), (KEY, "registry variable VIRUSTOTAL_API_KEY (Machine scope)"))
        self.assertEqual([(h, s) for h, s, _a in winreg.opened],
                         [("HKCU", "Environment"),
                          ("HKLM", r"SYSTEM\CurrentControlSet\Control\Session Manager\Environment")])
        self.assertTrue(all(access == FakeWinreg.KEY_READ for _h, _s, access in winreg.opened))

    def test_the_registry_is_not_consulted_off_windows(self):
        winreg = FakeWinreg(machine={"VIRUSTOTAL_API_KEY": KEY})
        with self.assertRaises(scan.MissingKeyError):
            self.resolve(environ={}, platform="linux", winreg=winreg)
        self.assertEqual(winreg.opened, [])

    def test_the_missing_key_message_names_the_three_places(self):
        with self.assertRaises(scan.MissingKeyError) as caught:
            self.resolve(environ={}, platform="win32", winreg=FakeWinreg())
        text = str(caught.exception)
        for needle in ("no VirusTotal API key found", "three places", "(1) --key-file PATH", "(2) the environment "
                       "variable VIRUSTOTAL_API_KEY", "(3) the Windows registry variable VIRUSTOTAL_API_KEY",
                       "User scope HKCU\\Environment", "Machine scope HKLM\\SYSTEM", "open a new shell"):
            self.assertIn(needle, text)
        with self.assertRaises(scan.MissingKeyError) as caught:
            self.resolve(environ={}, platform="linux")
        self.assertIn("skipped: this is not Windows", str(caught.exception))

    @unittest.skipUnless(sys.platform == "win32", "winreg exists on Windows only")
    def test_the_real_winreg_module_offers_what_the_reader_uses(self):
        import winreg  # only inspected, never queried: the user's real key stays unread
        for name in ("OpenKey", "QueryValueEx", "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE", "KEY_READ"):
            self.assertTrue(hasattr(winreg, name), name)


class MissingKeyCliTests(ScanCase):
    def test_a_missing_key_is_a_clear_error_before_any_request(self):
        code, out, err = self.cli(runtime=self.runtime(environ={}))
        self.assertEqual(code, 1)
        self.assertTrue(err.startswith("ERROR: no VirusTotal API key found"))
        for needle in ("--key-file", "VIRUSTOTAL_API_KEY", "registry"):
            self.assertIn(needle, err)
        self.assertNotIn("Traceback", err)
        self.assertEqual(self.fake.calls, [])
        self.assert_no_record()

    def test_the_key_comes_from_the_registry_when_the_environment_lacks_it(self):
        self.know_both()
        runtime = self.runtime(environ={}, platform="win32", winreg=FakeWinreg(machine={"VIRUSTOTAL_API_KEY": KEY}))
        code, out, err = self.cli(runtime=runtime)
        self.assertEqual((code, err), (0, ""))
        self.assertIn("API key: registry variable VIRUSTOTAL_API_KEY (Machine scope)", out)
        self.assertEqual(self.fake.calls[0].headers["x-apikey"], KEY)

    def test_the_key_file_and_key_env_options(self):
        self.know_both()
        key_file = self.tmp / "key.txt"
        key_file.write_text(KEY + "\n", encoding="ascii")
        code, out, err = self.cli("--key-file", str(key_file), runtime=self.runtime(environ={}))
        self.assertEqual((code, err), (0, ""))
        self.assertIn(f"API key: --key-file {key_file}", out)
        code, out, _err = self.cli("--key-env", "VT_OTHER", runtime=self.runtime(environ={"VT_OTHER": KEY}))
        self.assertEqual(code, 0)
        self.assertIn("API key: environment variable VT_OTHER", out)


# --------------------------------------------------------------------------------------------------------------
# A file VirusTotal already knows
# --------------------------------------------------------------------------------------------------------------

class KnownFileTests(ScanCase):
    def test_a_known_hash_is_read_without_any_upload(self):
        self.know_both()
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""))
        self.assertEqual([(c.method, c.path) for c in self.fake.calls],
                         [("GET", f"/api/v3/files/{self.client_sha}"), ("GET", f"/api/v3/files/{self.server_sha}")])
        self.assertEqual(self.fake.uploads, [])
        self.assertEqual(self.clock.sleeps, [])
        self.assertEqual(out.count("already known to VirusTotal"), 2)
        self.assertIn(f"{CLIENT_NAME}  sha256 {self.client_sha}  0 malicious, 0 suspicious of 72 engines  "
                      f"{GUI}{self.client_sha}  (analysed {ANALYSIS_ISO})", out)
        self.assertIn(f"{SERVER_NAME}  sha256 {self.server_sha}  0 malicious, 0 suspicious of 72 engines  "
                      f"{GUI}{self.server_sha}  (analysed {ANALYSIS_ISO})", out)
        self.assertFalse(self.record()["files"][0]["uploaded"])

    def test_every_request_carries_the_key_header_and_goes_to_virustotal(self):
        self.know_both()
        self.cli()
        for call in self.fake.calls:
            self.assertEqual(call.headers["x-apikey"], KEY)
            self.assertTrue(call.url.startswith("https://www.virustotal.com/api/v3/"))
            self.assertEqual(call.headers["Accept"], "application/json")
            self.assertEqual(call.timeout, 60.0)

    def test_the_socket_timeout_option_reaches_the_transport(self):
        self.know_both()
        self.cli("--timeout", "12.5")
        self.assertEqual({c.timeout for c in self.fake.calls}, {12.5})

    def test_only_the_lookup_is_needed_for_a_known_hash_even_when_the_file_is_huge(self):
        self.know_both()
        with patch.object(scan, "MAX_UPLOAD_BYTES", 10):
            code, _out, err = self.cli()
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(self.fake.uploads, [])

    def test_stats_are_reduced_to_integer_counts(self):
        self.fake.know(self.dlls[CLIENT_REL], {"malicious": True, "harmless": "5", "undetected": 10, "bad": -3})
        self.fake.know(self.dlls[SERVER_REL])
        code, out, _err = self.cli()
        self.assertEqual(code, 0)
        self.assertIn("0 malicious, 0 suspicious of 10 engines", out)
        self.assertEqual(self.record()["files"][0]["stats"], {"undetected": 10})

    def test_a_known_hash_without_an_analysis_yet_is_waited_for(self):
        empty = (200, file_body(self.client_sha, {}))
        self.fake.script = [empty, empty, (200, file_body(self.client_sha, CLEAN))]
        self.fake.know(self.dlls[SERVER_REL])
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""))
        self.assertIn("has no analysis of it yet", out)
        self.assertEqual(self.clock.sleeps, [scan.POLL_INTERVAL, scan.POLL_INTERVAL])
        self.assertEqual(self.fake.uploads, [])

    def test_an_answer_for_another_hash_is_refused(self):
        self.fake.script = [(200, file_body("f" * 64, CLEAN))]
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("another file", err)


# --------------------------------------------------------------------------------------------------------------
# Upload, multipart body, polling
# --------------------------------------------------------------------------------------------------------------

class UploadTests(ScanCase):
    def test_an_unknown_file_is_uploaded_polled_and_read_again(self):
        self.fake.know(self.dlls[SERVER_REL])
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""))
        paths = [(c.method, c.path) for c in self.fake.calls]
        analysis = "/api/v3/analyses/" + urllib.parse.quote("AnalysisId1-x==", safe="")
        self.assertEqual(paths, [("GET", f"/api/v3/files/{self.client_sha}"), ("POST", "/api/v3/files"),
                                 ("GET", analysis), ("GET", analysis), ("GET", analysis),
                                 ("GET", f"/api/v3/files/{self.client_sha}"),
                                 ("GET", f"/api/v3/files/{self.server_sha}")])
        self.assertEqual(self.clock.sleeps[:3], [scan.POLL_INTERVAL] * 3)  # one pause before every poll
        for needle in ("not known to VirusTotal yet; uploading", "analysis queued (poll 1)",
                       "analysis in-progress (poll 2)", "analysis completed (poll 3)", "already known"):
            self.assertIn(needle, out)
        record = self.record()
        self.assertEqual([f["uploaded"] for f in record["files"]], [True, False])
        self.assertEqual(record["files"][0]["analysis_date_utc"], ANALYSIS_ISO)

    def test_an_analysis_that_registers_late_is_polled_again(self):
        self.fake.know(self.dlls[SERVER_REL])
        self.fake.unregistered_polls = 2
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""))
        analysis = "/api/v3/analyses/" + urllib.parse.quote("AnalysisId1-x==", safe="")
        paths = [(c.method, c.path) for c in self.fake.calls]
        self.assertEqual(paths.count(("GET", analysis)), 5)   # two 404 answers, then queued, in-progress, completed
        self.assertEqual(out.count("analysis not registered yet, HTTP 404"), 2)
        self.assertIn("analysis completed (poll 5)", out)
        self.assertEqual([f["uploaded"] for f in self.record()["files"]], [True, False])

    def test_an_analysis_that_never_registers_is_an_error(self):
        self.fake.unregistered_polls = 99
        code, _out, err = self.cli(str(self.dist / CLIENT_REL))
        self.assertEqual(code, 1)
        self.assertIn(f"404 for analysis AnalysisId1-x== {scan.NOT_FOUND_POLLS} times in a row", err)
        self.assertIn("run the scan again", err)

    def test_the_multipart_body_is_what_virustotal_expects(self):
        self.fake.know(self.dlls[SERVER_REL])
        self.cli()
        upload_call = self.fake.by("POST", "files")[0]
        parsed = self.fake.uploads[0]
        self.assertEqual(parsed.field, "file")
        self.assertEqual(parsed.filename, CLIENT_NAME)
        self.assertEqual(parsed.data, self.dlls[CLIENT_REL])
        self.assertEqual(parsed.headers["Content-Type"], "application/octet-stream")
        self.assertEqual(parsed.delimiter_count, 2)  # one opening delimiter, one closing delimiter
        self.assertEqual(upload_call.headers["Content-Length"], str(len(upload_call.body)))
        self.assertTrue(upload_call.headers["Content-Type"].startswith("multipart/form-data; boundary="))
        self.assertEqual(upload_call.headers["x-apikey"], KEY)

    def test_the_boundary_is_redrawn_when_it_occurs_in_the_data(self):
        first, second = "aa" * 16, "bb" * 16
        data = b"prefix ----ArenaMapPortScan" + first.encode() + b" suffix"
        with patch.object(scan.secrets, "token_hex", side_effect=[first, second]):
            content_type, body = scan.multipart_body("x.dll", data)
        self.assertEqual(content_type, f"multipart/form-data; boundary=----ArenaMapPortScan{second}")
        parsed = parse_multipart(content_type, body)
        self.assertEqual(parsed.data, data)

    def test_binary_data_with_line_breaks_survives_the_body(self):
        data = bytes(range(256)) * 4 + b"\r\n\r\n--\r\n"
        parsed = parse_multipart(*scan.multipart_body("a.dll", data))
        self.assertEqual(parsed.data, data)

    def test_quotes_and_line_breaks_in_the_file_name_are_escaped(self):
        _content_type, body = scan.multipart_body('we"ird\r\nname.dll', b"x")
        self.assertIn(b'filename="we%22ird%0D%0Aname.dll"', body)

    def test_force_upload_skips_the_lookup_and_uploads_a_known_file_again(self):
        self.fake.know(self.dlls[CLIENT_REL], date=OLD_TS)
        self.fake.upload_stats = {**CLEAN, "malicious": 1, "undetected": 65}
        code, out, err = self.cli("--force-upload", str(self.dist / CLIENT_REL))
        self.assertEqual(code, 2, err)
        self.assertEqual([(c.method, c.path.split("/")[3]) for c in self.fake.calls[:2]],
                         [("POST", "files"), ("GET", "analyses")])
        self.assertEqual(len(self.fake.uploads), 1)
        self.assertIn("--force-upload", out)
        self.assertIn("1 malicious, 0 suspicious of 72 engines", out)
        record = self.record(self.files_record_path)  # explicit FILEs: the default record is the files record
        self.assertEqual(record["files"][0]["uploaded"], True)
        self.assertEqual(record["files"][0]["analysis_date_utc"], ANALYSIS_ISO)

    def test_a_stale_file_object_after_a_forced_upload_falls_back_to_the_analysis(self):
        self.fake.know(self.dlls[CLIENT_REL], date=OLD_TS)
        self.fake.keep_file_stale = True
        self.fake.upload_stats = {**CLEAN, "malicious": 2, "undetected": 64}
        code, out, _err = self.cli("--force-upload", str(self.dist / CLIENT_REL))
        self.assertEqual(code, 2)
        self.assertIn("2 malicious", out)
        self.assertIn("(analysed 2026-10-16T09:59:30Z)", out)  # the analysis' own date

    def test_a_missing_file_object_after_the_analysis_falls_back_to_the_analysis(self):
        self.fake.keep_file_stale = True  # the file never becomes known
        code, out, err = self.cli(str(self.dist / CLIENT_REL))
        self.assertEqual((code, err), (0, ""))
        self.assertIn("0 malicious, 0 suspicious of 72 engines", out)

    def test_the_analysis_meta_may_also_sit_inside_data(self):
        self.fake.meta_inside_data = True
        code, _out, err = self.cli(str(self.dist / CLIENT_REL))
        self.assertEqual((code, err), (0, ""))

    def test_an_analysis_of_another_file_is_refused(self):
        original = self.fake.__call__

        def swapped(method, url, headers, body, timeout):
            status, data = original(method, url, headers, body, timeout)
            if "/analyses/" in url:
                data["meta"] = {"file_info": {"sha256": "e" * 64}}
            return status, data

        code, _out, err = self.cli(str(self.dist / CLIENT_REL), runtime=self.runtime(transport=swapped))
        self.assertEqual(code, 1)
        self.assertIn("belongs to another file", err)

    def test_a_completed_analysis_without_stats_is_an_error(self):
        self.fake.upload_stats = {}
        self.fake.keep_file_stale = True
        code, _out, err = self.cli(str(self.dist / CLIENT_REL))
        self.assertEqual(code, 1)
        self.assertIn("carries no engine statistics", err)

    def test_a_waiting_analysis_that_never_completes_stops_at_wait(self):
        self.fake.statuses = ["queued"]
        code, out, err = self.cli("--wait", "50", str(self.dist / CLIENT_REL))
        self.assertEqual(code, 1)
        self.assertIn("did not finish within --wait 50 s", err)
        self.assertIn("keeps running on VirusTotal", err)
        self.assertEqual(len(self.fake.by("GET", "analyses")), 2)  # polls at +20 s and +40 s, none at +60 s
        self.assert_no_record()

    def test_wait_zero_still_reads_a_known_file(self):
        self.know_both()
        code, _out, err = self.cli("--wait", "0")
        self.assertEqual((code, err), (0, ""))

    def test_a_forced_upload_with_wait_zero_cannot_wait_for_the_analysis(self):
        code, _out, err = self.cli("--wait", "0", "--force-upload", str(self.dist / CLIENT_REL))
        self.assertEqual(code, 1)
        self.assertIn("did not finish within --wait 0 s", err)


class TooLargeTests(ScanCase):
    def test_the_limit_is_32_mib(self):
        self.assertEqual(scan.MAX_UPLOAD_BYTES, 32 * 1024 * 1024)

    def test_an_unknown_file_over_the_limit_is_refused_before_any_upload(self):
        with patch.object(scan, "MAX_UPLOAD_BYTES", 900):  # the client DLL has 1020 bytes, the server DLL 765
            code, out, err = self.cli(str(self.dist / CLIENT_REL))
        self.assertEqual(code, 1)
        self.assertIn(CLIENT_NAME, err)
        self.assertIn("1,020 bytes", err)
        self.assertIn("VirusTotal accepts", err)
        self.assertEqual(self.fake.uploads, [])
        self.assertEqual(len(self.fake.by("GET", "files")), 1)  # the lookup happened, the POST did not
        self.assertEqual(self.fake.by("POST", "files"), [])
        self.assert_no_record()

    def test_the_refusal_names_the_real_32_mib_limit(self):
        local = scan.LocalFile("big.dll", Path("big.dll"), scan.MAX_UPLOAD_BYTES + 1, "a" * 64)
        client = scan.Client(scan.Secret(KEY), self.fake, timeout=1, sleep=self.clock.sleep,
                             monotonic=self.clock.monotonic, log=lambda _m: None)
        with self.assertRaises(scan.ScanError) as caught:
            client.upload(local)
        text = str(caught.exception)
        self.assertIn("big.dll is 33,554,433 bytes", text)
        self.assertIn("more than the 33,554,432 bytes (32 MiB)", text)
        self.assertEqual(self.fake.calls, [])

    def test_a_file_of_exactly_the_limit_is_not_refused(self):
        data = bytes(4096)
        path = self.tmp / "exact.bin"
        path.write_bytes(data)
        with patch.object(scan, "MAX_UPLOAD_BYTES", len(data)):
            code, _out, err = self.cli(str(path))
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(len(self.fake.uploads), 1)

    def test_force_upload_does_not_get_around_the_limit(self):
        with patch.object(scan, "MAX_UPLOAD_BYTES", 900):
            code, _out, err = self.cli("--force-upload", str(self.dist / CLIENT_REL))
        self.assertEqual(code, 1)
        self.assertIn("VirusTotal accepts", err)
        self.assertEqual(self.fake.calls, [])

    def test_a_file_that_fits_is_still_uploaded(self):
        with patch.object(scan, "MAX_UPLOAD_BYTES", 900):
            code, _out, err = self.cli(str(self.dist / SERVER_REL))
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(len(self.fake.uploads), 1)

    def test_a_file_that_changes_while_scanning_is_refused(self):
        original = self.fake.__call__
        target = self.dist / CLIENT_REL

        def change_then_answer(method, url, headers, body, timeout):
            if method == "GET" and "/files/" in url:
                target.write_bytes(b"something else entirely")
            return original(method, url, headers, body, timeout)

        code, _out, err = self.cli(str(target), runtime=self.runtime(transport=change_then_answer))
        self.assertEqual(code, 1)
        self.assertIn("changed while it was being scanned", err)
        self.assertEqual(self.fake.uploads, [])


# --------------------------------------------------------------------------------------------------------------
# Quota pacing, 429, 401, network errors
# --------------------------------------------------------------------------------------------------------------

class PacingTests(ScanCase):
    def test_no_five_requests_start_within_a_minute(self):
        self.fake.statuses = ["queued"] * 4 + ["in-progress"] * 3 + ["completed"]
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""))
        calls = self.fake.calls
        self.assertGreater(len(calls), 12)
        for index in range(len(calls) - 4):
            self.assertGreaterEqual(calls[index + 4].at - calls[index].at, 60.0, index)
        self.assertIn("for the request quota", out)

    def test_the_quota_pause_is_exact_and_logged(self):
        code, out, _err = self.cli(str(self.dist / CLIENT_REL))
        self.assertEqual(code, 0)
        # GET, POST, poll, poll start at t, t, +20, +40; the third poll (the fifth request overall) waits for t + 61
        self.assertEqual(self.clock.sleeps, [20.0, 20.0, 20.0, 1.0])
        self.assertIn("pausing 1 s for the request quota", out)

    def test_a_quota_pause_that_does_not_fit_into_wait_is_reported(self):
        self.fake.statuses = ["queued"] * 10
        # polls at +20 s and +40 s; the third would start at +61 s (quota), after the 60.5 s budget
        code, _out, err = self.cli("--wait", "60.5", str(self.dist / CLIENT_REL))
        self.assertEqual(code, 1)
        self.assertIn("--wait leaves no time for the next request", err)
        self.assertIn("4 requests per minute", err)
        self.assert_no_record()

    def test_requests_that_are_retried_count_against_the_quota_too(self):
        self.know_both()
        self.fake.script = [urllib.error.URLError("down")] * 3
        self.cli(str(self.dist / CLIENT_REL), str(self.dist / SERVER_REL))
        calls = self.fake.calls
        self.assertEqual(len(calls), 5)
        self.assertGreaterEqual(calls[4].at - calls[0].at, 60.0)


class QuotaExceededTests(ScanCase):
    def test_429_sleeps_a_minute_and_repeats(self):
        self.know_both()
        quota = (429, error_body("QuotaExceededError", "Quota exceeded"))
        self.fake.script = [quota, quota]
        code, out, err = self.cli()
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(self.clock.sleeps, [60.0, 60.0])
        self.assertEqual(out.count("request quota exceeded (HTTP 429); waiting 60 s"), 2)

    def test_429_counts_against_wait(self):
        self.fake.script = [(429, error_body("QuotaExceededError", "Quota exceeded"))] * 5
        code, _out, err = self.cli("--wait", "100", str(self.dist / CLIENT_REL))
        self.assertEqual(code, 1)
        self.assertIn("HTTP 429", err)
        self.assertIn("--wait leaves no time", err)
        self.assertEqual(self.clock.sleeps, [60.0])
        self.assert_no_record()

    def test_the_default_wiring_sleeps_through_time_sleep(self):
        self.know_both()
        self.fake.script = [(429, error_body("QuotaExceededError", "Quota exceeded"))]
        runtime = scan.Runtime(transport=self.fake, environ={"VIRUSTOTAL_API_KEY": KEY}, platform="linux",
                               clock=lambda: NOW)
        with patch.object(scan.time, "sleep", side_effect=self.clock.sleep) as slept, \
                patch.object(scan.time, "monotonic", side_effect=self.clock.monotonic):
            code, _out, err = self.cli(runtime=runtime)
        self.assertEqual((code, err), (0, ""))
        slept.assert_called_once_with(60.0)


class FailureTests(ScanCase):
    def test_401_is_final(self):
        self.fake.script = [(401, error_body("WrongCredentialsError", "Wrong API key"))]
        code, out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("API key rejected", err)
        self.assertIn("WrongCredentialsError", err)
        self.assertEqual(len(self.fake.calls), 1)
        self.assertEqual(self.clock.sleeps, [])
        self.assert_no_record()

    def test_a_key_the_server_does_not_accept_ends_the_same_way(self):
        code, _out, err = self.cli(runtime=self.runtime(environ={"VIRUSTOTAL_API_KEY": "somebody-elses-key"}))
        self.assertEqual(code, 1)
        self.assertIn("API key rejected", err)
        self.assertEqual(len(self.fake.calls), 1)

    def test_other_4xx_are_final_and_carry_the_error_code(self):
        self.fake.script = [(403, error_body("ForbiddenError", "You are not authorized"))]
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("HTTP 403: ForbiddenError - You are not authorized", err)
        self.assertEqual(len(self.fake.calls), 1)

    def test_network_errors_are_retried_with_a_1_2_4_second_backoff(self):
        self.know_both()
        self.fake.script = [urllib.error.URLError("down")] * 3
        code, out, err = self.cli(str(self.dist / CLIENT_REL))
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(self.clock.sleeps, [1.0, 2.0, 4.0])
        self.assertEqual(len(self.fake.calls), 4)
        self.assertIn("retry 3 of 3 in 4 s", out)

    def test_the_fourth_failure_is_final(self):
        self.know_both()
        self.fake.script = [ConnectionResetError("reset")] * 10
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("failed after 4 attempt(s)", err)
        self.assertIn("ConnectionResetError", err)
        self.assertEqual(len(self.fake.calls), 4)
        self.assertEqual(self.clock.sleeps, [1.0, 2.0, 4.0])
        self.assert_no_record()

    def test_timeouts_and_http_client_errors_are_network_errors(self):
        self.know_both()
        self.fake.script = [TimeoutError("timed out"), http.client.RemoteDisconnected("closed")]
        code, _out, err = self.cli(str(self.dist / CLIENT_REL))
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(self.clock.sleeps, [1.0, 2.0])

    def test_5xx_answers_are_retried(self):
        self.know_both()
        self.fake.script = [(503, error_body("TransientError", "try again"))]
        code, out, err = self.cli(str(self.dist / CLIENT_REL))
        self.assertEqual((code, err), (0, ""))
        self.assertIn("HTTP 503: TransientError - try again", out)
        self.assertEqual(self.clock.sleeps, [1.0])

    def test_a_urllib_style_http_error_is_an_answer_not_a_crash(self):
        self.fake.script = [urllib.error.HTTPError("u", 404, "Not Found", {}, None)]
        code, _out, err = self.cli(str(self.dist / CLIENT_REL))
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(len(self.fake.uploads), 1)  # the 404 meant "unknown": the file was uploaded

    def test_a_failure_for_the_second_file_writes_no_record(self):
        self.fake.know(self.dlls[CLIENT_REL])
        original = self.fake.__call__

        def fail_server(method, url, headers, body, timeout):
            if self.server_sha in url:
                return 401, error_body("WrongCredentialsError", "Wrong API key")
            return original(method, url, headers, body, timeout)

        code, out, err = self.cli(runtime=self.runtime(transport=fail_server))
        self.assertEqual(code, 1)
        self.assertIn("API key rejected", err)
        self.assertIn(CLIENT_NAME, out)  # the first file's line was printed as it finished
        self.assert_no_record()

    def test_an_answer_without_attributes_is_an_error(self):
        self.fake.script = [(200, {"data": {}})]
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("no attributes object", err)

    def test_an_upload_answer_without_an_id_is_an_error(self):
        self.fake.script = [(404, error_body("NotFoundError", "no")), (200, {"data": {"type": "analysis"}})]
        code, _out, err = self.cli(str(self.dist / CLIENT_REL))
        self.assertEqual(code, 1)
        self.assertIn("no usable analysis id", err)


class FinalLookupTests(ScanCase):
    """After a completed analysis the final GET /files only refreshes the verdict the analysis already carries."""

    def script_after_completion(self, outcomes):
        """A transport that hands `outcomes` to the first calls after the analysis reported 'completed' (so to the
        final lookup, and to its retries)."""
        original = self.fake.__call__

        def transport(method, url, headers, body, timeout):
            result = original(method, url, headers, body, timeout)
            if "/analyses/" in url and result[1]["data"]["attributes"]["status"] == "completed":
                self.fake.script = list(outcomes)
            return result

        return transport

    def assert_analysis_verdict(self, code, out, err):
        self.assertEqual((code, err), (0, ""))
        self.assertIn("the final lookup of the file object failed", out)
        self.assertIn("using the statistics of the completed analysis", out)
        self.assertIn("0 malicious, 0 suspicious of 72 engines", out)
        entry = self.record(self.files_record_path)["files"][0]
        self.assertEqual((entry["uploaded"], entry["total_engines"], entry["stats"]), (True, 72, CLEAN))
        self.assertEqual(entry["analysis_date_utc"], "2026-10-16T09:59:30Z")  # the analysis' own date

    def test_a_final_lookup_that_does_not_fit_into_wait_keeps_the_verdict_of_the_completed_analysis(self):
        self.fake.statuses = ["queued", "completed"]
        # lookup and POST at +0 s, polls at +20 s and +40 s; the fifth request would have to wait until +61 s
        code, out, err = self.cli("--wait", "50", str(self.dist / CLIENT_REL))
        self.assert_analysis_verdict(code, out, err)
        self.assertIn("--wait leaves no time for the next request", out)
        self.assertEqual(len(self.fake.by("GET", "files")), 1)  # only the first lookup; the final one never started
        self.assertEqual(self.clock.sleeps, [20.0, 20.0])
        # the larger budget lets the final lookup wait for its quota slot, as before: the server DLL is unknown too
        code, out, err = self.cli("--wait", "70", str(self.dist / SERVER_REL))
        self.assertEqual((code, err), (0, ""))
        self.assertNotIn("the final lookup", out)
        self.assertEqual(len(self.fake.by("GET", "files")), 3)

    def test_a_detection_in_the_analysis_survives_a_failed_final_lookup(self):
        self.fake.statuses = ["queued", "completed"]
        self.fake.upload_stats = {**CLEAN, "malicious": 2, "undetected": 64}
        code, out, err = self.cli("--wait", "50", str(self.dist / CLIENT_REL))
        self.assertEqual((code, err), (2, ""))
        self.assertIn("2 malicious, 0 suspicious of 72 engines", out)
        self.assertEqual(self.record(self.files_record_path)["files"][0]["malicious"], 2)

    def test_a_server_error_at_the_final_lookup_keeps_the_verdict(self):
        transport = self.script_after_completion([(502, "<html>bad gateway</html>")] * 4)
        code, out, err = self.cli(str(self.dist / CLIENT_REL), runtime=self.runtime(transport=transport))
        self.assert_analysis_verdict(code, out, err)
        self.assertIn("HTTP 502", out)
        for needle in ("retry 1 of 3 in 1 s", "retry 2 of 3 in 2 s", "retry 3 of 3 in 4 s"):  # the usual backoff
            self.assertIn(needle, out)

    def test_a_network_error_at_the_final_lookup_keeps_the_verdict(self):
        transport = self.script_after_completion([urllib.error.URLError("connection reset")] * 4)
        code, out, err = self.cli(str(self.dist / CLIENT_REL), runtime=self.runtime(transport=transport))
        self.assert_analysis_verdict(code, out, err)
        self.assertIn("network error URLError", out)

    def test_an_exhausted_quota_at_the_final_lookup_keeps_the_verdict(self):
        quota = (429, error_body("QuotaExceededError", "Quota exceeded"))
        transport = self.script_after_completion([quota] * 3)
        code, out, err = self.cli("--wait", "100", str(self.dist / CLIENT_REL),
                                  runtime=self.runtime(transport=transport))
        self.assert_analysis_verdict(code, out, err)
        self.assertIn("HTTP 429", out)

    def test_a_refusal_at_the_final_lookup_is_still_an_error(self):
        for status, text in ((401, "API key rejected"), (403, "HTTP 403")):
            self.fake.known.clear()  # the first round's analysis made the file known; the second must upload again
            transport = self.script_after_completion([(status, error_body("ForbiddenError", "no"))])
            code, _out, err = self.cli(str(self.dist / CLIENT_REL), runtime=self.runtime(transport=transport))
            self.assertEqual(code, 1, status)
            self.assertIn(text, err)
            self.assertFalse(self.files_record_path.exists())

    def test_an_answer_for_another_file_at_the_final_lookup_is_still_an_error(self):
        transport = self.script_after_completion([(200, file_body("f" * 64, CLEAN))])
        code, _out, err = self.cli(str(self.dist / CLIENT_REL), runtime=self.runtime(transport=transport))
        self.assertEqual(code, 1)
        self.assertIn("VirusTotal answered for another file", err)
        self.assertFalse(self.files_record_path.exists())

    def test_a_failed_final_lookup_cannot_be_forgiven_without_analysis_statistics(self):
        self.fake.upload_stats = {}
        transport = self.script_after_completion([(502, "<html>bad gateway</html>")] * 4)
        code, _out, err = self.cli(str(self.dist / CLIENT_REL), runtime=self.runtime(transport=transport))
        self.assertEqual(code, 1)
        self.assertIn("lookup maschine-ArenaMapPort.Client.dll failed after 4 attempt(s)", err)
        self.assertFalse(self.files_record_path.exists())

    def test_the_errors_that_may_be_forgiven_are_the_ones_without_an_answer(self):
        self.assertTrue(issubclass(scan.NoAnswerError, scan.ApiError))
        self.assertEqual(scan.NoAnswerError("x", 429).status, 429)
        self.assertFalse(issubclass(scan.UsageError, scan.NoAnswerError))


# --------------------------------------------------------------------------------------------------------------
# The key never reaches any output
# --------------------------------------------------------------------------------------------------------------

class SecrecyTests(ScanCase):
    FORMS = (KEY, KEY_B64)

    def assert_clean(self, *texts):
        for text in texts:
            for form in self.FORMS:
                self.assertNotIn(form, text)

    def test_nothing_leaks_on_success(self):
        code, out, err = self.cli()
        self.assertEqual(code, 0)
        self.assert_clean(out, err, self.record_path.read_text(encoding="utf-8"))
        self.assertIn("API key: environment variable VIRUSTOTAL_API_KEY", out)

    def test_nothing_leaks_when_the_server_echoes_the_key(self):
        self.fake.script = [(401, error_body("WrongCredentialsError", f"Wrong API key {KEY} / {KEY_B64}"))]
        code, out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("API key rejected", err)
        self.assert_clean(out, err)

    def test_nothing_leaks_when_a_5xx_text_echoes_the_header(self):
        self.fake.script = [(502, f"<html>bad gateway x-apikey: {KEY}</html>")] * 5
        code, out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("HTTP 502", err)
        self.assert_clean(out, err)

    def test_nothing_leaks_when_the_network_stack_echoes_the_header(self):
        self.fake.script = [OSError(f"broken pipe while sending x-apikey: {KEY}")] * 5
        code, out, err = self.cli()
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
        client = scan.Client(scan.Secret(KEY), lambda *_a: (_ for _ in ()).throw(OSError(f"x {KEY}")), timeout=1,
                             sleep=lambda _s: None, monotonic=lambda: 0.0, log=lambda _m: None)
        with self.assertRaises(scan.ApiError) as caught:
            client.request("GET", "/files/" + "a" * 64, label="lookup x")
        self.assert_clean(str(caught.exception), repr(client))

    def test_nothing_leaks_from_the_file_and_registry_sources(self):
        key_file = self.tmp / "key.txt"
        key_file.write_text(KEY + "\n", encoding="ascii")
        self.know_both()
        code, out, err = self.cli("--key-file", str(key_file), runtime=self.runtime(environ={}))
        self.assertEqual(code, 0)
        self.assert_clean(out, err, self.record_path.read_text(encoding="utf-8"))
        runtime = self.runtime(environ={}, platform="win32", winreg=FakeWinreg(user={"VIRUSTOTAL_API_KEY": KEY}))
        code, out, err = self.cli(runtime=runtime)
        self.assertEqual(code, 0)
        self.assert_clean(out, err, self.record_path.read_text(encoding="utf-8"))

    def test_the_key_is_only_ever_sent_in_the_header(self):
        self.cli()
        for call in self.fake.calls:
            self.assertNotIn(KEY, call.url)
            self.assertEqual([k for k, v in call.headers.items() if KEY in str(v)], ["x-apikey"])
            if isinstance(call.body, bytes):
                self.assertNotIn(KEY.encode(), call.body)

    def test_there_is_no_option_that_takes_the_key_itself(self):
        out = io.StringIO()
        with redirect_stdout(out), self.assertRaises(SystemExit):
            scan.parse_args(["--help"])
        self.assertNotRegex(out.getvalue(), r"--key\b(?!-)")
        code, _out, err = self.cli("--key", KEY)
        self.assertEqual(code, 1)
        self.assertNotIn(KEY, err)

    def test_a_key_typed_as_an_option_value_is_not_echoed_by_the_argument_errors(self):
        # argparse repeats what it rejects, and at parse time the key is not even resolved yet
        cases = ([f"--key={KEY}"], [f"--wait={KEY}"], ["--wait", KEY], [f"--timeout={KEY}"], ["--timeout", KEY],
                 [f"--bogus={KEY}"], ["--version", KEY], [f"--version={KEY}"], [f"--key-env={KEY}"],
                 ["--key-env", KEY], [f"--dist={KEY}", KEY, f"--key-file={KEY}x"])
        for argv in cases:
            code, out, err = self.cli(*argv)
            self.assertEqual(code, 1, argv)
            self.assertTrue(err.startswith("ERROR: "), argv)
            self.assert_clean(out, err)
            self.assertEqual(self.fake.calls, [], argv)
        self.assert_no_record()

    def test_a_key_of_an_unusual_shape_is_hidden_by_the_argument_errors_too(self):
        # dots, plus signs, slashes and equals signs are no part of a VirusTotal key, but a slip must not print them
        odd = "vt.test+KEY/0123456789=abcdef.0123456789"
        for argv in ([f"--wait={odd}"], ["--wait", odd], [f"--timeout={odd}"], [f"--bogus={odd}"],
                     [f"--key={odd}"]):
            code, out, err = self.cli(*argv)
            self.assertEqual(code, 1, argv)
            self.assertNotIn(odd, out + err)
            self.assertNotIn("0123456789", out + err)

    def test_the_argument_errors_still_name_the_option_and_show_ordinary_values(self):
        code, _out, err = self.cli(f"--key={KEY}")
        self.assertIn("ambiguous option: --key=*** could match --key-env, --key-file", err)
        code, _out, err = self.cli("--wait", KEY)
        self.assertIn("argument --wait: invalid float value: '***'", err)
        code, _out, err = self.cli("--wait", "soon")
        self.assertIn("argument --wait: invalid float value: 'soon'", err)
        code, _out, err = self.cli("--no-such-option")
        self.assertIn("unrecognized arguments: --no-such-option", err)

    def test_a_key_pasted_as_a_file_name_is_not_echoed_but_a_missing_path_is(self):
        other = "another-key-" + "0123456789abcdef" * 2  # not the resolved key (a new one, say): only the argv scrub sees it
        for argv in ([KEY], [other], ["--dry-run", KEY], ["--dry-run", other]):
            code, out, err = self.cli(*argv)
            self.assertEqual(code, 1, argv)
            self.assertIn("file not found: ***", err)
            self.assert_clean(out, err)
            self.assertNotIn(other, out + err)
        missing = self.tmp / "missing-release-candidate.dll"
        code, _out, err = self.cli(str(missing))
        self.assertEqual(code, 1)
        self.assertIn(str(missing), err)  # a real path stays readable: it has a separator and an extension
        code, _out, err = self.cli("maschine-ArenaMapPort.Client-0.2.0-final.dll")
        self.assertIn("file not found: maschine-ArenaMapPort.Client-0.2.0-final.dll", err)

    def test_typed_value_helpers(self):
        self.assertEqual(scan.typed_values(["--wait=12345678", "--dry-run", "short", "somefile.dll", "--record",
                                            "--k=1234567", "xxxxxxxx", "--wait", "-1"]),
                         ["12345678", "somefile.dll", "xxxxxxxx"])
        self.assertEqual(scan.typed_values(None), [])
        tricky = "abc\\def'ghi-0123456789"
        self.assertEqual(scan.scrub_typed(f"raw {tricky} and repr {tricky!r}", [tricky, "short", "x" * 8]),
                         'raw *** and repr "***"')  # repr() chose double quotes for the single quote inside
        self.assertEqual(scan.scrub_typed("nothing typed here", []), "nothing typed here")
        self.assertEqual(scan.scrub_typed(ValueError("a " + "k" * 20), ["k" * 20]), "a ***")


# --------------------------------------------------------------------------------------------------------------
# Dry run
# --------------------------------------------------------------------------------------------------------------

class DryRunTests(ScanCase):
    def forbidden(self, *_args):
        raise AssertionError("the transport must not be called in a dry run")

    def test_dry_run_prints_the_plan_and_changes_nothing(self):
        before = self.snapshot_tree(self.repo)
        code, out, err = self.cli("--dry-run", runtime=self.runtime(transport=self.forbidden))
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(self.snapshot_tree(self.repo), before)
        self.assertFalse((self.repo / "work").exists())
        for needle in ("DRY RUN", CLIENT_NAME, SERVER_NAME, self.client_sha, self.server_sha,
                       f"{GUI}{self.client_sha}", "API key: found (environment variable VIRUSTOTAL_API_KEY)",
                       "1,020 bytes", str(self.record_path), "looked up by its hash"):
            self.assertIn(needle, out)
        self.assertEqual(self.clock.sleeps, [])

    def test_dry_run_without_a_key_reports_it_but_does_not_fail(self):
        code, out, _err = self.cli("--dry-run", runtime=self.runtime(transport=self.forbidden, environ={}))
        self.assertEqual(code, 0)
        self.assertIn("NOT FOUND", out)
        self.assertIn("no VirusTotal API key found", out)

    def test_dry_run_mentions_force_upload_and_the_size_limit(self):
        with patch.object(scan, "MAX_UPLOAD_BYTES", 900):
            code, out, _err = self.cli("--dry-run", "--force-upload", runtime=self.runtime(transport=self.forbidden))
        self.assertEqual(code, 0)
        self.assertIn("would be uploaded (--force-upload)", out)
        self.assertIn("upload limit, so it can only be scanned if VirusTotal already knows it", out)

    def test_dry_run_with_explicit_files_creates_no_directory(self):
        bare = self.tmp / "bare"
        bare.mkdir()
        probe = self.tmp / "probe.dll"
        probe.write_bytes(b"x" * 10)
        code, out, _err = self.cli("--dry-run", str(probe), "--record", str(bare / "deep" / "vt.json"), repo=bare,
                                   runtime=self.runtime(transport=self.forbidden))
        self.assertEqual(code, 0)
        self.assertEqual(list(bare.iterdir()), [])
        self.assertIn(sha(b"x" * 10), out)

    def test_dry_run_still_rejects_a_missing_file(self):
        code, _out, err = self.cli("--dry-run", str(self.tmp / "missing.dll"),
                                   runtime=self.runtime(transport=self.forbidden))
        self.assertEqual(code, 1)
        self.assertIn("file not found", err)


# --------------------------------------------------------------------------------------------------------------
# Exit codes, record, Markdown
# --------------------------------------------------------------------------------------------------------------

class ExitCodeTests(ScanCase):
    def test_zero_when_every_file_is_clean(self):
        self.know_both()
        code, out, _err = self.cli()
        self.assertEqual(code, 0)
        self.assertIn("VIRUSTOTAL_SCAN_CLEAN 0.2.0", out)

    def test_two_when_a_file_has_a_malicious_detection_and_the_record_is_still_written(self):
        self.know_both(server_stats={**CLEAN, "malicious": 3, "undetected": 63})
        code, out, err = self.cli()
        self.assertEqual(code, 2)
        self.assertEqual(err, "")
        self.assertIn("3 malicious, 0 suspicious of 72 engines", out)
        self.assertIn("VIRUSTOTAL_SCAN_DETECTIONS 1", out)
        self.assertIn(f"WARNING: 1 of 2 file(s) with detections ({SERVER_NAME})", out)
        record = self.record()
        self.assertEqual([f["malicious"] for f in record["files"]], [0, 3])

    def test_two_for_a_suspicious_detection_alone(self):
        self.know_both(client_stats={**CLEAN, "suspicious": 1, "undetected": 65})
        code, out, _err = self.cli()
        self.assertEqual(code, 2)
        self.assertIn("0 malicious, 1 suspicious of 72 engines", out)

    def test_one_on_an_error_and_no_record(self):
        code, _out, err = self.cli(str(self.tmp / "missing.dll"))
        self.assertEqual(code, 1)
        self.assertTrue(err.startswith("ERROR: file not found"))
        self.assert_no_record()

    def test_usage_errors_exit_with_one_so_that_two_always_means_a_detection(self):
        for argv in (["--wait", "-1"], ["--timeout", "0"], ["--key-env", "not a name"], ["--no-such-option"],
                     ["--wait", "soon"]):
            code, out, err = self.cli(*argv)
            self.assertEqual(code, 1, argv)
            self.assertTrue(err.startswith("ERROR: "), argv)
            self.assertEqual(self.fake.calls, [])

    def test_help_exits_zero(self):
        out = io.StringIO()
        with redirect_stdout(out), self.assertRaises(SystemExit) as caught:
            scan.parse_args(["--help"])
        self.assertEqual(caught.exception.code, 0)
        for option in ("FILE", "--dist", "--version", "--record", "--key-env", "--key-file", "--timeout", "--wait",
                       "--force-upload", "--dry-run"):
            self.assertIn(option, out.getvalue())


class RecordTests(ScanCase):
    def test_the_record_content(self):
        self.fake.know(self.dlls[CLIENT_REL], {**CLEAN, "malicious": 1, "undetected": 65})
        self.fake.know(self.dlls[SERVER_REL])
        self.cli()
        record = self.record()
        self.assertEqual(list(record), ["schema", "version", "scanned_utc", "files"])
        self.assertEqual((record["schema"], record["version"], record["scanned_utc"]),
                         (1, "0.2.0", "2026-10-16T12:00:00Z"))
        first, second = record["files"]
        self.assertEqual(list(first), ["name", "size", "sha256", "gui_url", "stats", "malicious", "suspicious",
                                       "total_engines", "analysis_date_utc", "uploaded"])
        self.assertEqual(first, {"name": CLIENT_NAME, "size": len(self.dlls[CLIENT_REL]), "sha256": self.client_sha,
                                 "gui_url": GUI + self.client_sha,
                                 "stats": {**CLEAN, "malicious": 1, "undetected": 65}, "malicious": 1,
                                 "suspicious": 0, "total_engines": 72, "analysis_date_utc": ANALYSIS_ISO,
                                 "uploaded": False})
        self.assertEqual((second["name"], second["malicious"], second["total_engines"]), (SERVER_NAME, 0, 72))

    def test_the_record_is_written_atomically_and_replaced_by_the_next_run(self):
        self.know_both()
        self.cli()
        self.assertEqual(self.snapshot_tree(self.record_path.parent), ["virustotal.json"])
        first = self.record_path.read_bytes()
        self.assertTrue(first.endswith(b"\n"))
        self.assertNotIn(b"\r\n", first)
        self.know_both(client_stats={**CLEAN, "malicious": 1, "undetected": 65})
        self.cli()
        self.assertNotEqual(self.record_path.read_bytes(), first)
        self.assertEqual(self.record()["files"][0]["malicious"], 1)

    def test_a_failing_replace_keeps_the_old_record_and_leaves_no_partial_file(self):
        self.know_both()
        self.cli()
        before = self.record_path.read_bytes()
        with patch.object(scan.os, "replace", side_effect=OSError("disk full")):
            code, _out, err = self.cli("--record", str(self.record_path))
        self.assertEqual(code, 1)
        self.assertIn("disk full", err)
        self.assertEqual(self.record_path.read_bytes(), before)
        self.assertEqual(self.snapshot_tree(self.record_path.parent), ["virustotal.json"])

    def test_the_partial_file_is_what_gets_replaced(self):
        self.know_both()
        calls = []
        real = os.replace

        def spy(source, destination):
            calls.append((Path(source).name, Path(destination).name, Path(source).exists()))
            real(source, destination)

        with patch.object(scan.os, "replace", side_effect=spy):
            self.cli()
        self.assertEqual(calls, [("virustotal.json.partial", "virustotal.json", True)])

    def test_record_option_and_version_option(self):
        self.know_both()
        target = self.tmp / "elsewhere" / "vt.json"
        code, _out, _err = self.cli("--record", str(target))
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(target.read_text(encoding="utf-8"))["version"], "0.2.0")
        self.assert_no_record()
        code, _out, _err = self.cli("--version", "0.3.0")
        self.assertEqual(code, 0)
        other = self.repo / "work" / "releases" / "0.3.0" / "virustotal.json"
        self.assertEqual(json.loads(other.read_text(encoding="utf-8"))["version"], "0.3.0")

    def test_an_invalid_version_is_an_error_and_is_not_echoed(self):
        for argv in (["--version", "banana"], ["--version=banana"], ["--version", ""]):
            code, _out, err = self.cli(*argv)
            self.assertEqual(code, 1, argv)
            self.assertIn("invalid version", err)
            self.assertNotIn("banana", err)
        self.assertEqual(self.fake.calls, [])


class MarkdownTests(ScanCase):
    def test_the_block_has_one_bullet_per_file(self):
        self.know_both(server_stats={**CLEAN, "suspicious": 2, "undetected": 64})
        code, out, _err = self.cli()
        self.assertEqual(code, 2)
        start = out.index("----- Markdown block (copy from here) -----\n") + len(
            "----- Markdown block (copy from here) -----\n")
        end = out.index("----- end of Markdown block -----")
        self.assertEqual(out[start:end].splitlines(), [
            f"- [{CLIENT_NAME}]({GUI}{self.client_sha}): 0 malicious, 0 suspicious of 72 engines ({ANALYSIS_ISO})",
            f"- [{SERVER_NAME}]({GUI}{self.server_sha}): 0 malicious, 2 suspicious of 72 engines ({ANALYSIS_ISO})"])

    def test_brackets_in_a_name_are_escaped(self):
        local = scan.LocalFile("odd[1].dll", Path("odd[1].dll"), 1, "a" * 64)
        lines = scan.markdown_lines([scan.Result(local, {"undetected": 3}, ANALYSIS_TS, False)])
        self.assertEqual(lines, [f"- [odd\\[1\\].dll]({GUI}{'a' * 64}): 0 malicious, 0 suspicious of 3 engines "
                                 f"({ANALYSIS_ISO})"])

    def test_the_result_line_without_a_date(self):
        local = scan.LocalFile("x.dll", Path("x.dll"), 1, "b" * 64)
        line = scan.result_line(scan.Result(local, {"malicious": 1, "undetected": 2}, None, True))
        self.assertEqual(line, f"x.dll  sha256 {'b' * 64}  1 malicious, 0 suspicious of 3 engines  "
                               f"{GUI}{'b' * 64}  (analysed unknown)")

    def test_total_engines_is_the_sum_of_all_stats_values(self):
        local = scan.LocalFile("x.dll", Path("x.dll"), 1, "c" * 64)
        result = scan.Result(local, {"harmless": 1, "malicious": 2, "suspicious": 3, "timeout": 4}, None, False)
        self.assertEqual((result.malicious, result.suspicious, result.total), (2, 3, 10))


# --------------------------------------------------------------------------------------------------------------
# The default file list
# --------------------------------------------------------------------------------------------------------------

class DefaultFilesTests(ScanCase):
    def test_without_files_the_two_dlls_in_dist_are_scanned_in_client_server_order(self):
        self.know_both()
        code, _out, err = self.cli()
        self.assertEqual((code, err), (0, ""))
        self.assertEqual([c.path.rsplit("/", 1)[1] for c in self.fake.calls], [self.client_sha, self.server_sha])
        self.assertEqual([f["name"] for f in self.record()["files"]], [CLIENT_NAME, SERVER_NAME])

    def test_dist_option(self):
        other = self.tmp / "otherdist"
        for rel, data in self.dlls.items():
            (other / rel).parent.mkdir(parents=True)
            (other / rel).write_bytes(data + b"!")
        self.fake.know(self.dlls[CLIENT_REL] + b"!")
        self.fake.know(self.dlls[SERVER_REL] + b"!")
        code, _out, err = self.cli("--dist", str(other))
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(self.record()["files"][0]["sha256"], sha(self.dlls[CLIENT_REL] + b"!"))

    def test_a_missing_dll_in_dist_is_an_error(self):
        (self.dist / SERVER_REL).unlink()
        code, _out, err = self.cli()
        self.assertEqual(code, 1)
        self.assertIn("file not found", err)
        self.assertIn(SERVER_NAME, err)
        self.assertEqual(self.fake.calls, [])

    def test_explicit_files_replace_the_default_list(self):
        probe = self.tmp / "probe.dll"
        probe.write_bytes(b"probe bytes")
        self.fake.know(b"probe bytes")
        code, out, _err = self.cli(str(probe))
        self.assertEqual(code, 0)
        self.assertEqual([c.path.rsplit("/", 1)[1] for c in self.fake.calls], [sha(b"probe bytes")])
        self.assertEqual([f["name"] for f in self.record(self.files_record_path)["files"]], ["probe.dll"])
        self.assertIn(f"wrote {self.files_record_path}", out)

    def test_explicit_files_never_replace_the_release_record(self):
        # a normal scan writes the release record of the two DLLs ...
        self.know_both()
        self.assertEqual(self.cli()[0], 0)
        release_record = self.record_path.read_bytes()
        self.assertEqual([f["name"] for f in self.record()["files"]], [CLIENT_NAME, SERVER_NAME])
        # ... and a later scan of a stray file, or a forced re-scan of one DLL, leaves it alone
        probe = self.tmp / "probe.dll"
        probe.write_bytes(b"probe bytes")
        self.fake.know(b"probe bytes")
        self.assertEqual(self.cli(str(probe))[0], 0)
        self.assertEqual(self.record_path.read_bytes(), release_record)
        self.assertEqual([f["name"] for f in self.record(self.files_record_path)["files"]], ["probe.dll"])
        code, _out, err = self.cli("--force-upload", str(self.dist / CLIENT_REL))
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(len(self.fake.uploads), 1)
        self.assertEqual(self.record_path.read_bytes(), release_record)
        self.assertEqual([f["name"] for f in self.record(self.files_record_path)["files"]], [CLIENT_NAME])
        self.assertEqual(self.snapshot_tree(self.record_path.parent), ["virustotal-files.json", "virustotal.json"])

    def test_the_default_list_still_writes_the_release_record(self):
        self.know_both()
        probe = self.tmp / "probe.dll"
        probe.write_bytes(b"probe bytes")
        self.fake.know(b"probe bytes")
        self.cli(str(probe))
        self.assertFalse(self.record_path.exists())
        self.assertEqual(self.cli()[0], 0)
        self.assertEqual([f["name"] for f in self.record()["files"]], [CLIENT_NAME, SERVER_NAME])
        self.assertEqual([f["name"] for f in self.record(self.files_record_path)["files"]], ["probe.dll"])

    def test_record_option_overrides_the_files_default_too(self):
        self.know_both()
        self.cli()
        self.fake.know(b"probe bytes")
        probe = self.tmp / "probe.dll"
        probe.write_bytes(b"probe bytes")
        # naming the release record on purpose is allowed: the person chose to replace it
        code, _out, _err = self.cli(str(probe), "--record", str(self.record_path))
        self.assertEqual(code, 0)
        self.assertEqual([f["name"] for f in self.record()["files"]], ["probe.dll"])
        self.assertFalse(self.files_record_path.exists())

    def test_a_dry_run_names_the_record_an_explicit_file_scan_would_write(self):
        probe = self.tmp / "probe.dll"
        probe.write_bytes(b"probe bytes")
        code, out, _err = self.cli("--dry-run", str(probe))
        self.assertEqual(code, 0)
        self.assertIn(f"The record would be {self.files_record_path}", out)
        code, out, _err = self.cli("--dry-run")
        self.assertIn(f"The record would be {self.record_path}", out)
        self.assert_no_record()

    def test_the_same_file_twice_is_refused(self):
        code, _out, err = self.cli(str(self.dist / CLIENT_REL), str(self.dist / CLIENT_REL))
        self.assertEqual(code, 1)
        self.assertIn("listed twice", err)

    def test_explicit_files_outside_a_repository_need_a_record_path(self):
        bare = self.tmp / "bare"
        bare.mkdir()
        probe = self.tmp / "probe.dll"
        probe.write_bytes(b"probe bytes")
        self.fake.know(b"probe bytes")
        code, _out, err = self.cli(str(probe), repo=bare)
        self.assertEqual(code, 1)
        self.assertIn("--record PATH or --version", err)
        target = self.tmp / "out.json"
        code, _out, _err = self.cli(str(probe), "--record", str(target), repo=bare)
        self.assertEqual(code, 0)
        self.assertIsNone(json.loads(target.read_text(encoding="utf-8"))["version"])

    def test_the_real_repository_resolves_its_two_dlls(self):
        spec = scan.release.load_spec(REPO)
        self.assertTrue(spec.client_dll.endswith(".dll"))
        self.assertTrue(spec.server_dll.endswith(".dll"))
        self.assertTrue(spec.version)


# --------------------------------------------------------------------------------------------------------------
# HTTP glue and module facts
# --------------------------------------------------------------------------------------------------------------

class HttpRequestTests(unittest.TestCase):
    def test_request_goes_to_virustotal_over_https_and_parses_json(self):
        seen = {}
        with patch.object(scan.http.client, "HTTPSConnection", connection_class(record=seen)):
            status, data = scan.http_request("GET", "https://www.virustotal.com/api/v3/files/x?a=1",
                                             {"x-apikey": KEY}, None, 33)
        self.assertEqual((status, data), (200, {"data": {}}))
        host, port, kwargs = seen["init"]
        self.assertEqual((host, port, kwargs["timeout"]), ("www.virustotal.com", 443, 33))
        self.assertEqual(seen["request"][:2], ("GET", "/api/v3/files/x?a=1"))
        self.assertEqual(seen["request"][3], {"x-apikey": KEY})
        self.assertTrue(seen["closed"])

    def test_text_and_empty_answers(self):
        with patch.object(scan.http.client, "HTTPSConnection", connection_class(502, b"<html>bad gateway")):
            self.assertEqual(scan.http_request("GET", "https://www.virustotal.com/api/v3/x", {}, None, 1),
                             (502, "<html>bad gateway"))
        with patch.object(scan.http.client, "HTTPSConnection", connection_class(204, b"")):
            self.assertEqual(scan.http_request("GET", "https://www.virustotal.com/api/v3/x", {}, None, 1),
                             (204, None))

    def test_no_other_host_or_scheme_ever_receives_the_header(self):
        with patch.object(scan.http.client, "HTTPSConnection", connection_class()):
            for url in ("http://www.virustotal.com/api/v3/x", "https://www.virustotal.com.evil.example/api/v3/x",
                        "https://virustotal.com/api/v3/x", "https://example.org/api/v3/x"):
                with self.assertRaises(scan.ScanError):
                    scan.http_request("GET", url, {"x-apikey": KEY}, None, 1)

    def test_an_oversized_answer_is_refused(self):
        with patch.object(scan, "MAX_RESPONSE", 10):
            with patch.object(scan.http.client, "HTTPSConnection", connection_class(payload=b"x" * 50)):
                with self.assertRaisesRegex(scan.ScanError, "larger than"):
                    scan.http_request("GET", "https://www.virustotal.com/api/v3/x", {}, None, 1)

    def test_the_body_and_headers_of_a_post_are_passed_through(self):
        seen = {}
        with patch.object(scan.http.client, "HTTPSConnection", connection_class(record=seen)):
            scan.http_request("POST", "https://www.virustotal.com/api/v3/files", {"Content-Length": "3"}, b"abc", 5)
        self.assertEqual(seen["request"], ("POST", "/api/v3/files", b"abc", {"Content-Length": "3"}))


class ModuleTests(unittest.TestCase):
    def test_the_docstring_documents_the_interface(self):
        for needle in ("GET  /api/v3/files/{sha256}", "POST /api/v3/files", "GET  /api/v3/analyses/{id}",
                       "x-apikey", "VIRUSTOTAL_API_KEY", "Machine scope", "virustotal.json", "4 requests per minute",
                       "Exit status: 0", "2 when any file has a detection", "--force-upload", "--dry-run",
                       "32 MiB", "virustotal-files.json", "no error message repeats it"):
            self.assertIn(needle, scan.__doc__)

    def test_defaults(self):
        args = scan.parse_args([])
        self.assertEqual((args.files, args.timeout, args.wait, args.key_env, args.record, args.dist),
                         ([], 60.0, 600.0, "VIRUSTOTAL_API_KEY", None, None))
        self.assertEqual((args.force_upload, args.dry_run), (False, False))
        self.assertEqual((scan.POLL_INTERVAL, scan.QUOTA_REQUESTS, scan.QUOTA_BACKOFF, scan.NETWORK_RETRIES),
                         (20.0, 4, 60.0, 3))

    def test_urls(self):
        self.assertEqual(scan.gui_url("a" * 64), "https://www.virustotal.com/gui/file/" + "a" * 64)
        self.assertEqual(scan.API, "https://www.virustotal.com/api/v3")

    def test_date_helpers(self):
        self.assertEqual(scan.date_value(ANALYSIS_TS), ANALYSIS_ISO)
        for bad in (None, "x", True, 10 ** 30):
            self.assertIsNone(scan.date_value(bad))
            self.assertEqual(scan.date_text(bad), "unknown")

    def test_the_module_imports_nothing_outside_the_standard_library(self):
        source = (REPO / "tools" / "release" / "scan_virustotal.py").read_text(encoding="utf-8")
        self.assertNotIn("import requests", source)
        self.assertNotIn("urlopen", source)


if __name__ == "__main__":
    unittest.main()
