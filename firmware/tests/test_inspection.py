import hashlib
import io
from pathlib import Path
import struct
import sys
import tarfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from inspect_update import inspect_bytes


def filesystem():
    result = bytearray(4096)
    struct.pack_into("<5I6H8Q", result, 0, 0x73717368, 3, 0, 131072, 0,
                     4, 17, 0, 1, 4, 0, 0, 96, *([0] * 6))
    return bytes(result)


def package(extra=None, bad_digest=False, bad_superblock=False):
    fs = filesystem()
    if bad_superblock:
        fs = b"oops" + fs[4:]
    digest = "0" * 32 if bad_digest else hashlib.md5(fs).hexdigest()
    entries = [("usr.sqsh4", fs), ("fw_version", b"6.0.24.10_test\n"),
               ("usr.sqsh4.md5", (digest + "  usr.sqsh4\n").encode())]
    stream = io.BytesIO()
    with tarfile.open(fileobj=stream, mode="w") as archive:
        for name, content in entries:
            item = tarfile.TarInfo(name); item.size = len(content)
            archive.addfile(item, io.BytesIO(content))
        if extra:
            archive.addfile(extra, io.BytesIO(b"x" * extra.size))
    return stream.getvalue()


class InspectionTests(unittest.TestCase):
    def test_known_container_and_integrity(self):
        report, files = inspect_bytes(package())
        self.assertEqual(report["version"], "6.0.24.10_test")
        self.assertTrue(report["filesystem"]["md5_matches"])
        self.assertEqual(set(files), {"usr.sqsh4", "usr.sqsh4.md5", "fw_version"})

    def test_corrupt_filesystem(self):
        with self.assertRaisesRegex(ValueError, "MD5"):
            inspect_bytes(package(bad_digest=True))

    def test_false_filesystem_with_matching_checksum(self):
        with self.assertRaisesRegex(ValueError, "SquashFS"):
            inspect_bytes(package(bad_superblock=True))

    def test_path_traversal_rejected(self):
        entry = tarfile.TarInfo("../../outside"); entry.size = 1
        with self.assertRaisesRegex(ValueError, "Unexpected"):
            inspect_bytes(package(extra=entry))

    def test_duplicate_member_rejected(self):
        entry = tarfile.TarInfo("fw_version"); entry.size = 1
        with self.assertRaisesRegex(ValueError, "duplicate"):
            inspect_bytes(package(extra=entry))

    def test_link_rejected(self):
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode="w") as archive:
            entry = tarfile.TarInfo("usr.sqsh4")
            entry.type = tarfile.SYMTYPE; entry.linkname = "../../outside"
            archive.addfile(entry)
        with self.assertRaisesRegex(ValueError, "links"):
            inspect_bytes(stream.getvalue())

    def test_truncated_archive(self):
        with self.assertRaises((tarfile.TarError, ValueError)):
            inspect_bytes(package()[:700])


if __name__ == "__main__":
    unittest.main()
