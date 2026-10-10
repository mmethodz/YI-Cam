import ftplib
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import dump_flash_ftp as dump


MAPS = "00008000-000b1000 r-xp 00000000 1f:04 5 /bin/busybox\n"
WINDOW = bytearray(dump.WINDOW_BYTES)
OFFSET = dump.PATCH_ADDRESS - dump.MEMORY_START
WINDOW[OFFSET:OFFSET + 4] = dump.ORIGINAL
WINDOW = bytes(WINDOW)


class FakeFtp:
    def __init__(self, fail_flash=False, version=dump.VERSION):
        self.code = WINDOW
        self.writes = []
        self.flash_reads = 0
        self.quit_called = False
        self.fail_flash = fail_flash
        self.version = version

    def retrlines(self, command, callback):
        assert command == "LIST /dev/mtd0ro"
        callback("crw-r--r-- 1 root root 90, 1 Jan 1 2000 mtd0ro")

    def retrbinary(self, command, callback, blocksize=65536, rest=None):
        if command == "RETR /proc/self/mem":
            assert rest == dump.MEMORY_START
            callback(self.code)
            callback(bytes(dump.MEMORY_END - dump.MEMORY_START - len(self.code)))
            raise ftplib.error_temp("451 Error")
        if command == "RETR /dev/mtd0ro":
            self.flash_reads += 1
            if self.fail_flash:
                callback(b"x")
                raise ftplib.error_temp("451 Error")
            callback(bytes(dump.FLASH_BYTES))
            return "226 Operation successful"
        payloads = {
            "/bin/busybox": b"synthetic",
            "/proc/cpuinfo": dump.BOARD.encode(),
            "/usr/fw_version": self.version.encode(),
            "/sys/class/mtd/mtd0/size": str(dump.FLASH_BYTES).encode(),
            "/sys/class/mtd/mtd0/type": b"nor\n",
            "/proc/self/maps": MAPS.encode(),
            "/proc/self/cmdline": b"ftpd\0-w\0/\0-t\0" + b"600\0",
            "/proc/self/status": b"Name:\tftpd\nPid:\t123\nUid:\t0\t0\t0\t0\n",
            "/proc/mtd": b"synthetic flash map",
        }
        callback(payloads[command.removeprefix("RETR ")])
        return "226 Operation successful"

    def storbinary(self, command, data, blocksize, rest):
        value = data.read()
        self.writes.append((command, rest, value))
        self.code = self.code[:OFFSET] + value + self.code[OFFSET + 4:]
        return "226 Operation successful"

    def quit(self):
        self.quit_called = True
        return "221 Operation successful"

    def close(self):
        self.quit_called = True


class FlashTests(unittest.TestCase):
    def test_unknown_binary_refused_before_any_remote_action(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, "Unqualified BusyBox"):
                dump.acquire(object(), Path(directory) / "unused", b"unknown")
            self.assertFalse((Path(directory) / "unused").exists())

    def test_mapping_must_be_private_exact_executable_and_file(self):
        dump.validate_maps(MAPS)
        for value in (MAPS.replace("r-xp", "r-xs"), MAPS.replace("000b1000", "000c1000"),
                      MAPS.replace("busybox", "other"), MAPS.replace("r-xp", "rwxp"), MAPS + MAPS):
            with self.subTest(value=value), self.assertRaises(ValueError):
                dump.validate_maps(value)

    def test_no_arbitrary_memory_payload(self):
        with self.assertRaises(ValueError):
            dump.write_worker_instruction(object(), b"other bytes")

    def test_short_memory_error_is_not_an_accepted_code_read(self):
        class Short:
            def retrbinary(self, command, callback, **kwargs):
                callback(WINDOW)
                raise ftplib.error_temp("451 Error")
        with self.assertRaises(ftplib.error_temp):
            dump.read_code_window(Short())

    def test_failed_flash_read_restores_worker_and_keeps_partial(self):
        ftp = FakeFtp(fail_flash=True)
        with tempfile.TemporaryDirectory() as directory, patch.object(dump, "validate_reference", return_value=WINDOW):
            output = Path(directory) / "new"
            with self.assertRaises(ftplib.error_temp):
                dump.acquire(ftp, output, b"synthetic")
            self.assertEqual([item[2] for item in ftp.writes], [dump.PATCHED, dump.ORIGINAL])
            self.assertEqual(ftp.code, WINDOW)
            self.assertTrue(ftp.quit_called)
            self.assertTrue((output / "mtd0-full-pass1.bin.partial").exists())
            self.assertFalse((output / "mtd0-full-pass1.bin").exists())

    def test_version_guard_never_writes(self):
        ftp = FakeFtp(version="other")
        with tempfile.TemporaryDirectory() as directory, patch.object(dump, "validate_reference", return_value=WINDOW):
            with self.assertRaisesRegex(ValueError, "Unqualified installed version"):
                dump.acquire(ftp, Path(directory) / "new", b"synthetic")
        self.assertFalse(ftp.writes)
        self.assertTrue(ftp.quit_called)

    def test_success_reads_twice_and_writes_only_worker_instruction(self):
        ftp = FakeFtp()
        with tempfile.TemporaryDirectory() as directory, patch.object(dump, "validate_reference", return_value=WINDOW), patch.object(dump, "FLASH_BYTES", 16), patch.object(dump, "BUSYBOX_SHA256", dump.sha256(b"synthetic")):
            report = dump.acquire(ftp, Path(directory) / "new", b"synthetic")
            self.assertTrue(report["complete"])
            self.assertTrue(report["identical_reads"])
            self.assertTrue(report["worker_restore_verified"])
            self.assertTrue(report["binary_file_unchanged"])
            self.assertEqual(ftp.flash_reads, 2)
            self.assertEqual(ftp.writes, [("STOR /proc/self/mem", dump.PATCH_ADDRESS, dump.PATCHED),
                                          ("STOR /proc/self/mem", dump.PATCH_ADDRESS, dump.ORIGINAL)])
            self.assertTrue(ftp.quit_called)


if __name__ == "__main__":
    unittest.main()
