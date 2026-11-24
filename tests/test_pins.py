"""The pins in nodes/Vision.cs follow native/: its checksum table is native/SHA256SUMS, its download URL is the release
of the crate's version, its model's name and SHA-256 are those native/fetch-model.sh fetches, and the first line of
each node file names that version. With HS_VISION_CHECK_RELEASE=1 (CI), the release's own SHA256SUMS is compared too."""

import os
import re
import tomllib
import unittest
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
VISION = ROOT / "nodes" / "Vision.cs"
PEOPLE = ROOT / "nodes" / "People.cs"
PEOPLE_DETECTOR = ROOT / "nodes" / "PeopleDetector.cs"
SUMS = ROOT / "native" / "SHA256SUMS"
CARGO = ROOT / "native" / "Cargo.toml"
FETCH_MODEL = ROOT / "native" / "fetch-model.sh"

SOURCE = re.compile(r'^\s*const string Source = "([^"]*)";\s*$', re.MULTILINE)
RELEASE = re.compile(r"^https://github\.com/dompagoj/hass-sharp-vision/releases/download/v(\d+\.\d+\.\d+)/$")
HEADER = re.compile(r"^// hass-sharp-vision (\d+\.\d+\.\d+), from https://github\.com/dompagoj/hass-sharp-vision$")
MODEL_NAME = re.compile(r'^\s*const string ModelName = "([^"]*)";\s*$', re.MULTILINE)
MODEL_SHA256 = re.compile(r'^\s*const string ModelSha256 = "([^"]*)";\s*$', re.MULTILINE)
ENTRY = re.compile(r'^\s*\["([^"]+)"\] = "([0-9a-f]{64})",\s*$')


def source() -> str:
    found = SOURCE.findall(VISION.read_text(encoding="utf-8"))
    if len(found) != 1:
        raise AssertionError(f"Vision.cs has {len(found)} lines of the form const string Source = \"...\"; not 1")
    return found[0]


def model() -> tuple[str, str]:
    """The model's file name and SHA-256 that Vision.cs pins."""
    text = VISION.read_text(encoding="utf-8")
    names, sums = MODEL_NAME.findall(text), MODEL_SHA256.findall(text)
    if len(names) != 1 or len(sums) != 1:
        raise AssertionError("Vision.cs has no single ModelName and ModelSha256")
    return names[0], sums[0]


def fetched() -> tuple[str, str]:
    """The model's file name and SHA-256 that native/fetch-model.sh downloads and checks."""
    text = FETCH_MODEL.read_text(encoding="utf-8")
    names = re.findall(r"^NAME=(\S+)$", text, re.MULTILINE)
    sums = re.findall(r"^SHA256=([0-9a-f]{64})$", text, re.MULTILINE)
    if len(names) != 1 or len(sums) != 1:
        raise AssertionError("fetch-model.sh has no single NAME= and SHA256= line")
    return names[0], sums[0]


def header(path: Path) -> str:
    """The version the first line of a node file names."""
    first = path.read_text(encoding="utf-8").split("\n", 1)[0]
    match = HEADER.match(first)
    if not match:
        raise AssertionError(f"the first line of {path.name} is not // hass-sharp-vision <version>, from <repository>: {first!r}")
    return match.group(1)


def table() -> list[tuple[str, str]]:
    """The (RID, SHA-256) entries between // pinned:begin and // pinned:end, in order."""
    lines = VISION.read_text(encoding="utf-8").splitlines()
    begins = [i for i, line in enumerate(lines) if line.strip() == "// pinned:begin"]
    ends = [i for i, line in enumerate(lines) if line.strip() == "// pinned:end"]
    if len(begins) != 1 or len(ends) != 1 or ends[0] < begins[0]:
        raise AssertionError("Vision.cs has no single // pinned:begin ... // pinned:end block")
    entries = []
    for line in lines[begins[0] + 1 : ends[0]]:
        match = ENTRY.match(line)
        if not match:
            raise AssertionError(f'a line in the pinned table is not ["<rid>"] = "<sha256>",: {line!r}')
        entries.append((match.group(1), match.group(2)))
    return entries


def sums(text: str) -> list[tuple[str, str]]:
    """The (RID, SHA-256) lines of a SHA256SUMS, in order: '<sha256>  libhs_vision-<rid>.so'."""
    entries = []
    for line in text.splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  libhs_vision-(.+)\.so", line)
        if not match:
            raise AssertionError(f"a line of SHA256SUMS is not '<sha256>  libhs_vision-<rid>.so': {line!r}")
        entries.append((match.group(2), match.group(1)))
    return entries


def version() -> str:
    with CARGO.open("rb") as cargo:
        return tomllib.load(cargo)["package"]["version"]


class PinsTest(unittest.TestCase):
    def test_table_is_sha256sums(self):
        self.assertEqual(table(), sums(SUMS.read_text(encoding="utf-8")))

    def test_source_is_the_release_of_this_version(self):
        url = source()
        match = RELEASE.match(url)
        self.assertIsNotNone(match, f"Source is not a hass-sharp-vision release download URL: {url}")
        self.assertEqual(match.group(1), version())

    def test_model_is_the_one_fetch_model_fetches(self):
        self.assertEqual(model(), fetched())

    def test_node_files_name_this_version(self):
        for path in (VISION, PEOPLE, PEOPLE_DETECTOR):
            with self.subTest(path.name):
                self.assertEqual(header(path), version())

    @unittest.skipUnless(os.environ.get("HS_VISION_CHECK_RELEASE") == "1", "the release is compared only with HS_VISION_CHECK_RELEASE=1")
    def test_release_matches(self):
        try:
            with urllib.request.urlopen(source() + "SHA256SUMS", timeout=30) as answer:
                released = answer.read().decode("utf-8")
        except urllib.error.HTTPError as error:
            if error.code == 404:
                self.skipTest(f"v{version()} is not released yet")
            raise
        self.assertEqual(released, SUMS.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
