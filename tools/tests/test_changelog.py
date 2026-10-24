"""Guards for CHANGELOG.md (added with release 0.3.0).

The changelog starts with 0.2.3, the oldest version that is still downloadable; the withdrawn
0.1.0, 0.2.0, 0.2.1 and 0.2.2 keep their notes under docs/releases/ only. These tests pin the
file's shape so a release cannot go out with a changelog that lags the version in the csproj,
a malformed heading, a missing release-note link, or a sentence that calls the Arena gate or
the bundle encryption more than it is (AGENTS.md: a good-faith requirement, never copy
protection). No game data and no Arena install are read.
"""
from __future__ import annotations

import datetime
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
CHANGELOG = ROOT / "CHANGELOG.md"
CLIENT_CSPROJ = ROOT / "ArenaMapPort.Client" / "ArenaMapPort.Client.csproj"
RELEASE_NOTES = ROOT / "docs" / "releases"

SECTION = re.compile(r"^## (?P<version>\d+\.\d+\.\d+) - (?P<date>\d{4}-\d{2}-\d{2}|unreleased)$")
WITHDRAWN = ("0.1.0", "0.2.0", "0.2.1", "0.2.2")
SUBHEADINGS = {"Added", "Changed", "Fixed", "Changed (internal)", "Known limitations", "Compatibility"}


def _read() -> str:
    return CHANGELOG.read_text(encoding="utf-8")


def _csproj_version() -> str:
    match = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", CLIENT_CSPROJ.read_text(encoding="utf-8"))
    assert match, "no <Version> in ArenaMapPort.Client.csproj"
    return match.group(1)


def _sections() -> list[tuple[str, str, str]]:
    """(version, date, body) of every '## ' section, in file order."""
    sections = []
    current = None
    for line in _read().splitlines():
        if line.startswith("## "):
            match = SECTION.match(line)
            assert match, f"malformed section heading: {line!r}"
            current = [match.group("version"), match.group("date"), []]
            sections.append(current)
        elif current is not None:
            current[2].append(line)
    return [(v, d, "\n".join(body)) for v, d, body in sections]


def _version_key(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in version.split("."))


class ChangelogShapeTests(unittest.TestCase):
    def test_the_file_exists_and_is_not_empty(self):
        self.assertTrue(CHANGELOG.is_file(), "CHANGELOG.md is missing at the repository root")
        self.assertTrue(_read().startswith("# Changelog"))

    def test_every_section_heading_is_version_dash_iso_date_or_unreleased(self):
        headings = [line for line in _read().splitlines() if line.startswith("## ")]
        self.assertGreaterEqual(len(headings), 2)
        for line in headings:
            match = SECTION.match(line)
            self.assertIsNotNone(match, f"heading is not '## <version> - <YYYY-MM-DD>' or '## <version> - unreleased': {line!r}")
            if match.group("date") != "unreleased":
                datetime.date.fromisoformat(match.group("date"))  # a real calendar date

    def test_the_newest_section_names_the_client_csproj_version(self):
        newest_version = _sections()[0][0]
        self.assertEqual(newest_version, _csproj_version(),
                         "the first section of CHANGELOG.md must name the version in ArenaMapPort.Client.csproj")

    def test_the_0_2_3_section_is_present_and_dated_by_its_publication(self):
        dated = {version: date for version, date, _ in _sections()}
        self.assertIn("0.2.3", dated)
        self.assertEqual(dated["0.2.3"], "2026-10-19")

    def test_the_file_covers_0_2_3_and_later_only(self):
        versions = [version for version, _, _ in _sections()]
        for withdrawn in WITHDRAWN:
            self.assertNotIn(withdrawn, versions, f"{withdrawn} was withdrawn and has no changelog section")
        self.assertEqual(versions[-1], "0.2.3", "0.2.3 is the oldest section")

    def test_sections_run_newest_first(self):
        sections = _sections()
        versions = [_version_key(version) for version, _, _ in sections]
        self.assertEqual(versions, sorted(versions, reverse=True), "versions must descend")
        self.assertEqual(len(set(versions)), len(versions), "a version appears twice")
        dates = [date for _, date, _ in sections if date != "unreleased"]
        self.assertEqual(dates, sorted(dates, reverse=True), "dates must not increase going down the file")
        for _, date, _ in sections[1:]:
            self.assertNotEqual(date, "unreleased", "only the newest section may be unreleased")

    def test_each_section_links_its_release_note_and_the_note_exists(self):
        for version, _, body in _sections():
            link = f"docs/releases/v{version}.md"
            self.assertIn(link, body, f"section {version} does not link {link}")
            self.assertTrue((RELEASE_NOTES / f"v{version}.md").is_file(), f"{link} does not exist")

    def test_each_section_has_a_compatibility_part_and_only_known_subheadings(self):
        for version, _, body in _sections():
            subheadings = re.findall(r"^### (.+?)\s*$", body, flags=re.MULTILINE)
            self.assertIn("Compatibility", subheadings, f"section {version} has no Compatibility part")
            for name in subheadings:
                self.assertIn(name, SUBHEADINGS, f"section {version}: unexpected sub-heading {name!r}")
            self.assertEqual(len(subheadings), len(set(subheadings)), f"section {version}: a sub-heading repeats")


class ChangelogWordingTests(unittest.TestCase):
    def test_the_gate_and_the_encryption_are_never_called_copy_protection(self):
        # AGENTS.md: a good-faith installation check and a requirement for the player's own Arena files, never more.
        text = re.sub(r"\s+", " ", _read())
        for match in re.finditer(r"copy[- ]protection", text, flags=re.IGNORECASE):
            before = text[max(0, match.start() - 8):match.start()].lower()
            self.assertTrue(before.endswith("not ") or before.endswith("no "),
                            f"'copy protection' must only appear negated: ...{text[max(0, match.start() - 60):match.end() + 20]}...")

    def test_no_arena_object_path_or_data_file_name_is_published(self):
        # Public text describes the maps, not Arena's hierarchy or data files (object names such as 'Door_01 (2)',
        # level or sharedassets file names, the animation script's name).
        internal = re.compile(r"Door_\d|One_man_Door|sharedassets|\blevel\d+\b|\.resS\b|\.resource\b|DotAnimator|SBG_Arena|\bSOO\b")
        match = internal.search(_read())
        self.assertIsNone(match, f"Arena-internal name in the changelog: {match.group(0) if match else ''}")

    def test_no_key_or_hash_sized_hex_string_is_pasted(self):
        # A content key (32 bytes) and a SHA-256 are both 64 hex digits; neither belongs in a changelog.
        self.assertIsNone(re.search(r"\b[0-9a-fA-F]{64}\b", _read()))


if __name__ == "__main__":
    unittest.main()
