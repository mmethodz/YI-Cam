"""Copy installed camera files over FTP without changing the camera.

This is a file backup, NOT a raw flash image. Links are recorded, never followed
or created. Credentials, calibration and vendor binaries belong in private local
storage. The camera's FTP connection is unencrypted; use the owner's local LAN.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import ftplib
import getpass
import hashlib
import ipaddress
import json
import os
from pathlib import Path, PurePosixPath
import re
import time

ROOTS = ("/bin", "/sbin", "/lib", "/etc", "/usr", "/data")
INVENTORY = ("/proc/cpuinfo", "/proc/version", "/proc/mtd", "/proc/cmdline",
             "/proc/mounts", "/proc/modules", "/proc/meminfo",
             "/sys/ak_info_dump/sensor_id", "/tmp/start_message")
MAX_FILE = 16 * 1024 * 1024
MAX_TOTAL = 128 * 1024 * 1024
MAX_ENTRIES = 4096
MAX_DEPTH = 16


def valid_name(name: str) -> str:
    # A camera-supplied name must remain one ordinary Windows/Unix component.
    if (not name or name in (".", "..") or name[-1:] in (".", " ")
            or any(ord(c) < 32 or c in '<>:"/\\|?*' for c in name)
            or re.fullmatch(r"(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\..*)?",
                            name, re.IGNORECASE)):
        raise ValueError("Unsafe filename in FTP listing")
    return name


def parse_listing(line: str) -> dict | None:
    """Parse the observed BusyBox LIST format; refuse ambiguous entries."""
    if re.fullmatch(r"total\s+\d+", line):
        return None
    fields = line.split(maxsplit=8)
    if len(fields) != 9 or not re.fullmatch(r"[-dl][rwxstST-]{9}", fields[0]):
        raise ValueError("Unsupported FTP listing entry")
    mode, links, owner, group, size, month, day, stamp, name = fields
    entry = {"mode": mode, "owner": owner, "group": group,
             "listed_time": f"{month} {day} {stamp}"}
    if mode.startswith("l"):
        name, separator, target = name.partition(" -> ")
        if not separator or not target:
            raise ValueError("Symlink listing has no target")
        entry["link_target"] = target
    entry["name"] = valid_name(name)
    if not size.isdecimal():
        raise ValueError("Invalid listed file size")
    entry["listed_bytes"] = int(size)
    return entry


class FileBackup:
    def __init__(self, ftp: ftplib.FTP, output: Path):
        self.ftp = ftp
        self.output = output
        self.output.mkdir(parents=True, exist_ok=False)
        self.claimed: set[str] = set()
        self.total = 0
        self.failures = 0
        self.report = {
            "started_utc": datetime.now(timezone.utc).isoformat(),
            "kind": "installed-file-backup-not-raw-flash",
            "camera_writes": False,
            "symlinks": "metadata only; not followed or materialized",
            "consistency": "Live file reads; mutable settings are not an atomic snapshot",
            "roots": list(ROOTS), "entries": [], "inventory": [],
        }

    def claim(self, relative: str) -> Path:
        parts = PurePosixPath(relative).parts
        if not parts or relative.startswith("/") or "/".join(parts) != relative:
            raise ValueError("Invalid local relative path")
        for part in parts:
            valid_name(part)
        key = relative.casefold()
        if key in self.claimed:
            raise ValueError("Duplicate or case-colliding local path")
        self.claimed.add(key)
        if len(self.claimed) > MAX_ENTRIES:
            raise ValueError("Backup entry limit exceeded")
        return self.output.joinpath(*parts)

    def retrieve(self, remote: str, target: Path, limit: int,
                 expected: int | None = None) -> dict:
        if not 0 <= limit <= MAX_FILE or any(ord(c) < 32 for c in remote):
            raise ValueError("Invalid retrieval limit or remote path")
        target.parent.mkdir(parents=True, exist_ok=True)
        # Failed downloads remain visibly incomplete; never masquerade as files.
        partial = target.with_name(target.name + ".partial")
        size = 0
        digest = hashlib.sha256()
        started = time.monotonic()
        with partial.open("xb") as stream:
            def accept(chunk: bytes):
                nonlocal size
                size += len(chunk)
                self.total += len(chunk)
                if size > limit or self.total > MAX_TOTAL:
                    raise ValueError("Backup byte limit exceeded")
                if time.monotonic() - started > 120:
                    raise TimeoutError("File transfer exceeded time bound")
                stream.write(chunk)
                digest.update(chunk)

            self.ftp.retrbinary("RETR " + remote, accept, blocksize=32768)
        if expected is not None and size != expected:
            raise ValueError("File size changed or transfer was incomplete")
        if target.exists():
            raise FileExistsError("Refusing to overwrite backup file")
        partial.rename(target)
        return {"bytes": size, "sha256": digest.hexdigest()}

    def inventory(self):
        for remote in INVENTORY:
            entry = {"remote": remote}
            self.report["inventory"].append(entry)
            try:
                target = self.claim("inventory/" + remote[1:])
                entry.update(self.retrieve(remote, target, 128 * 1024))
            except ftplib.error_perm as error:
                entry["unavailable"] = str(error)
        # Read NOR/NAND identity; do not assume cat is an adequate NAND backup.
        table = self.output / "inventory/proc/mtd"
        if not table.exists():
            return
        for number in re.findall(r"^mtd(\d+):", table.read_text(), re.MULTILINE):
            for attribute in ("type", "size", "erasesize"):
                remote = f"/sys/class/mtd/mtd{number}/{attribute}"
                entry = {"remote": remote}
                self.report["inventory"].append(entry)
                try:
                    target = self.claim("inventory/" + remote[1:])
                    entry.update(self.retrieve(remote, target, 4096))
                except ftplib.error_perm as error:
                    entry["unavailable"] = str(error)

    def tree(self, remote: str, depth: int = 0):
        if depth > MAX_DEPTH:
            raise ValueError("Directory depth limit exceeded")
        rows = []

        def add_row(row):
            if len(rows) >= MAX_ENTRIES:
                raise ValueError("Directory entry limit exceeded")
            rows.append(row)

        self.ftp.retrlines("LIST " + remote, add_row)
        for row in rows:
            entry = parse_listing(row)
            if entry is None:
                continue
            path = remote + "/" + entry.pop("name")
            entry["remote"] = path
            target = self.claim("files" + path)
            self.report["entries"].append(entry)
            if entry["mode"].startswith("l"):
                continue
            if entry["mode"].startswith("d"):
                target.mkdir(parents=True, exist_ok=True)
                self.tree(path, depth + 1)
            else:
                expected = entry["listed_bytes"]
                if expected > MAX_FILE:
                    raise ValueError("Listed file exceeds maximum size")
                try:
                    entry.update(self.retrieve(path, target, expected, expected))
                except ftplib.error_perm as error:
                    entry["error"] = str(error)
                    self.failures += 1

    def run(self):
        try:
            self.inventory()
            for root in ROOTS:
                self.claim("files" + root).mkdir(parents=True, exist_ok=True)
                self.tree(root)
                print(f"Copied {root}: {len(self.report['entries'])} entries, "
                      f"{self.total:,} bytes received", flush=True)
            self.report["status"] = "complete" if not self.failures else "partial"
        except Exception as error:
            self.report["status"] = "aborted"
            self.report["error"] = f"{type(error).__name__}: {error}"
            raise
        finally:
            self.report["received_bytes"] = self.total
            self.report["finished_utc"] = datetime.now(timezone.utc).isoformat()
            (self.output / "manifest.json").write_text(
                json.dumps(self.report, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, help="Owner's private LAN camera IP")
    parser.add_argument("--user", default="root")
    parser.add_argument("--password-env", help="Environment variable containing FTP password")
    parser.add_argument("--output", type=Path, required=True, help="New private local directory")
    args = parser.parse_args()
    address = ipaddress.ip_address(args.host)
    if (not address.is_private or address.is_loopback or address.is_link_local
            or address.is_multicast or address.is_unspecified):
        parser.error("Use the camera's private LAN IP address")
    password = (os.environ[args.password_env] if args.password_env
                else getpass.getpass("Camera FTP password: "))
    # Credentials are never printed or saved in the manifest.
    with ftplib.FTP() as ftp:
        ftp.connect(str(address), 21, timeout=12)
        ftp.login(args.user, password)
        backup = FileBackup(ftp, args.output)
        backup.run()
    print(f"File backup {backup.report['status']}; this is not a raw flash image.")
    return 1 if backup.failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
