from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from backup_ftp import FileBackup, parse_listing, valid_name


class FakeFtp:
    def __init__(self, rows=(), data=b"file"):
        self.rows, self.data, self.commands = rows, data, []

    def retrlines(self, command, callback):
        self.commands.append(command)
        for row in self.rows:
            callback(row)

    def retrbinary(self, command, callback, blocksize):
        self.commands.append(command)
        callback(self.data)


class BackupTests(unittest.TestCase):
    def test_name_rejects_traversal_windows_aliases_and_commands(self):
        for name in ("..", "../outside", "x/y", "x\\y", "NUL.bin", "COM1",
                     "alternate:stream", "name.", "name ", "a\r\nSTOR bad"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                valid_name(name)

    def test_symlink_recorded_not_followed_or_materialized(self):
        ftp = FakeFtp(["lrwxrwxrwx 1 root root 4 Oct 10 10:00 link -> /dev/mtd0"])
        with tempfile.TemporaryDirectory() as directory:
            backup = FileBackup(ftp, Path(directory) / "new")
            backup.tree("/etc")
            self.assertEqual(ftp.commands, ["LIST /etc"])
            self.assertEqual(backup.report["entries"][0]["link_target"], "/dev/mtd0")
            self.assertFalse((backup.output / "files/etc/link").exists())

    def test_regular_file_only_retrieved_and_hashed(self):
        ftp = FakeFtp(["-rwxr-xr-x 1 1002 1002 4 Oct 10 10:00 tool"])
        with tempfile.TemporaryDirectory() as directory:
            backup = FileBackup(ftp, Path(directory) / "new")
            backup.tree("/bin")
            self.assertEqual(ftp.commands, ["LIST /bin", "RETR /bin/tool"])
            self.assertEqual((backup.output / "files/bin/tool").read_bytes(), b"file")
            self.assertEqual(backup.report["entries"][0]["bytes"], 4)
            self.assertEqual(len(backup.report["entries"][0]["sha256"]), 64)

    def test_truncated_transfer_stays_partial(self):
        with tempfile.TemporaryDirectory() as directory:
            backup = FileBackup(FakeFtp(data=b"ab"), Path(directory) / "new")
            target = backup.claim("files/usr/tool")
            with self.assertRaisesRegex(ValueError, "incomplete"):
                backup.retrieve("/usr/tool", target, 4, 4)
            self.assertFalse(target.exists())
            self.assertEqual(target.with_name("tool.partial").read_bytes(), b"ab")

    def test_oversized_transfer_stays_partial(self):
        with tempfile.TemporaryDirectory() as directory:
            backup = FileBackup(FakeFtp(data=b"too much"), Path(directory) / "new")
            target = backup.claim("files/usr/tool")
            with self.assertRaisesRegex(ValueError, "limit"):
                backup.retrieve("/usr/tool", target, 4, 4)
            self.assertFalse(target.exists())

    def test_existing_destination_and_case_collision_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(FileExistsError):
                FileBackup(FakeFtp(), Path(directory))
            backup = FileBackup(FakeFtp(), Path(directory) / "new")
            backup.claim("files/Test")
            with self.assertRaisesRegex(ValueError, "colliding"):
                backup.claim("files/test")

    def test_device_and_ambiguous_listings_rejected(self):
        self.assertIsNone(parse_listing("total 36"))
        for row in ("crw-rw---- 1 root root 90, 0 Jan 1 1970 mtd0",
                    "not a listing", "-rwxr-xr-x 1 root root 4 Oct 10 10:00 ../bad"):
            with self.subTest(row=row), self.assertRaises(ValueError):
                parse_listing(row)


if __name__ == "__main__":
    unittest.main()
