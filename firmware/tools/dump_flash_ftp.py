"""Two raw NOR reads using a temporary change to one authenticated FTP worker.

Restricted to the exact backed-up BusyBox/build/board. The sole remote write is
four bytes to that worker's /proc/self/mem, restored before disconnect. No flash,
configuration, executable file or other process is written. This is research
tooling for an owner's LAN camera, not a generic firmware installer.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import ftplib
import getpass
import hashlib
import io
import ipaddress
import json
import os
from pathlib import Path
import re


BUSYBOX_SHA256 = "3e7d0f2541caacbd728a7bb9341cfa3cf0c4cd30aa3239a4499a967fcac6d6e7"
VERSION = "6.0.24.10_202401091113"
BOARD = "Cloud39EV2_AK3918E80PIN_MNBD"
FLASH_BYTES = 8 * 1024 * 1024
MEMORY_START, MEMORY_END = 0x1DCC0, 0xB1000
CODE_BASE, PATCH_ADDRESS, WINDOW_BYTES = 0x8000, 0x1DD00, 0x140
ORIGINAL = bytes.fromhex("0100000a")  # A32 BEQ 0x1dd0c, after S_ISREG comparison
PATCHED = bytes.fromhex("010000ea")   # A32 B   0x1dd0c; open/fstat failures remain


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def validate_reference(data: bytes) -> bytes:
    if sha256(data) != BUSYBOX_SHA256:
        raise ValueError("Unqualified BusyBox binary; no camera write permitted")
    if data[PATCH_ADDRESS - CODE_BASE:PATCH_ADDRESS - CODE_BASE + 4] != ORIGINAL:
        raise ValueError("Unexpected reference instruction")
    return data[MEMORY_START - CODE_BASE:MEMORY_START - CODE_BASE + WINDOW_BYTES]


def validate_maps(maps: str) -> None:
    entries = [line.split(maxsplit=5) for line in maps.splitlines()]
    candidates = [entry for entry in entries if len(entry) == 6
                  and entry[0] == "00008000-000b1000"]
    if (len(candidates) != 1 or candidates[0][1:3] != ["r-xp", "00000000"]
            or candidates[0][5] != "/bin/busybox"):
        raise ValueError("Unexpected FTP worker mapping; refusing memory write")


def read_bounded(ftp, path: str, limit: int, *, rest=None) -> tuple[bytes, str]:
    chunks, count = [], 0

    def receive(data):
        nonlocal count
        count += len(data)
        if count > limit:
            raise ValueError("Remote transfer exceeded its bound")
        chunks.append(data)

    response = ftp.retrbinary("RETR " + path, receive, blocksize=65536, rest=rest)
    return b"".join(chunks), response


def read_code_window(ftp) -> bytes:
    # RETR has no byte-count option. Read to the known unmapped gap, where the
    # kernel returns EIO. A 451 is expected ONLY after this exact-length read.
    chunks, count = [], 0

    def receive(data):
        nonlocal count
        count += len(data)
        if count > MEMORY_END - MEMORY_START:
            raise ValueError("Unexpected memory mapping length")
        if sum(map(len, chunks)) < WINDOW_BYTES:
            chunks.append(data[:WINDOW_BYTES - sum(map(len, chunks))])

    try:
        ftp.retrbinary("RETR /proc/self/mem", receive, blocksize=65536,
                       rest=MEMORY_START)
    except ftplib.error_temp as error:
        if str(error) != "451 Error" or count != MEMORY_END - MEMORY_START:
            raise
    else:
        raise ValueError("Unexpected memory read termination")
    return b"".join(chunks)


def write_worker_instruction(ftp, value: bytes) -> None:
    if value not in (ORIGINAL, PATCHED):
        raise ValueError("Only the reviewed instruction and its restoration are allowed")
    ftp.storbinary("STOR /proc/self/mem", io.BytesIO(value), blocksize=4,
                   rest=PATCH_ADDRESS)


def retrieve_flash(ftp, output: Path, pass_number: int) -> dict:
    target = output / f"mtd0-full-pass{pass_number}.bin"
    partial = target.with_suffix(".bin.partial")
    digest, count = hashlib.sha256(), 0
    with partial.open("xb") as stream:
        def receive(data):
            nonlocal count
            count += len(data)
            if count > FLASH_BYTES:
                raise ValueError("Flash transfer exceeded known device size")
            stream.write(data)
            digest.update(data)
        # Read-only MTD character node, never STOR or an ioctl to the flash.
        response = ftp.retrbinary("RETR /dev/mtd0ro", receive, blocksize=65536)
    if count != FLASH_BYTES or not response.startswith("226 "):
        raise ValueError("Incomplete flash transfer; retained as .partial")
    partial.rename(target)
    return {"file": target.name, "bytes": count, "sha256": digest.hexdigest()}


def acquire(ftp, output: Path, reference: bytes) -> dict:
    expected = validate_reference(reference)
    output.mkdir(parents=True, exist_ok=False)
    report = {"started_utc": datetime.now(timezone.utc).isoformat(),
              "kind": "whole-spi-nor-live-reads", "persistent_writes": False,
              "temporary_worker_patch_attempted": False,
              "worker_patch_verified": False, "worker_restore_verified": False,
              "passes": [], "complete": False}

    def get(path, limit=65536):
        return read_bounded(ftp, path, limit)[0]

    try:
        remote_binary = get("/bin/busybox", 1024 * 1024)
        validate_reference(remote_binary)
        if BOARD not in get("/proc/cpuinfo").decode():
            raise ValueError("Unqualified board")
        if get("/usr/fw_version").decode().strip() != VERSION:
            raise ValueError("Unqualified installed version")
        if int(get("/sys/class/mtd/mtd0/size")) != FLASH_BYTES:
            raise ValueError("Unexpected flash size")
        if get("/sys/class/mtd/mtd0/type").strip() != b"nor":
            raise ValueError("Only the qualified NOR device is supported")
        maps = get("/proc/self/maps").decode()
        validate_maps(maps)
        command = get("/proc/self/cmdline").split(b"\0")
        if not command or command[0].rsplit(b"/", 1)[-1] != b"ftpd":
            raise ValueError("Connection is not an isolated FTP worker")
        status = get("/proc/self/status").decode()
        if not re.search(r"^Uid:\s+0\s+0\s+0\s+0\s*$", status, re.M):
            raise ValueError("An authenticated root worker is required")
        report["worker_pid"] = int(re.search(r"^Pid:\s+(\d+)$", status, re.M)[1])
        report["worker_maps_before"] = maps
        report["busybox_sha256"] = BUSYBOX_SHA256
        report["instruction_address"] = hex(PATCH_ADDRESS)
        report["original_instruction"] = ORIGINAL.hex()
        report["temporary_instruction"] = PATCHED.hex()
        report["flash_map"] = get("/proc/mtd").decode()
        node_listing = []
        ftp.retrlines("LIST /dev/mtd0ro", node_listing.append)
        if (len(node_listing) != 1 or not node_listing[0].startswith("cr")
                or not re.search(r"\s90,\s+1\s", node_listing[0])
                or node_listing[0].split()[-1] not in ("mtd0ro", "/dev/mtd0ro")):
            raise ValueError("Unexpected read-only MTD node")
        if read_code_window(ftp) != expected:
            raise ValueError("Runtime code differs from the reviewed binary")
        patched_window = bytearray(expected)
        offset = PATCH_ADDRESS - MEMORY_START
        patched_window[offset:offset + 4] = PATCHED
        try:
            report["temporary_worker_patch_attempted"] = True
            write_worker_instruction(ftp, PATCHED)
            if read_code_window(ftp) != bytes(patched_window):
                raise ValueError("Worker instruction change did not verify")
            report["worker_patch_verified"] = True
            for number in (1, 2):
                print(f"Reading complete flash, pass {number}...", flush=True)
                report["passes"].append(retrieve_flash(ftp, output, number))
        finally:
            try:
                write_worker_instruction(ftp, ORIGINAL)
                report["worker_restore_verified"] = read_code_window(ftp) == expected
                if not report["worker_restore_verified"]:
                    raise ValueError("Worker restoration did not verify")
            except Exception as error:
                report["restoration_error"] = type(error).__name__ + ": " + str(error)
                raise
        report["binary_file_unchanged"] = sha256(get("/bin/busybox", 1024 * 1024)) == BUSYBOX_SHA256
        if not report["binary_file_unchanged"]:
            raise ValueError("Unexpected on-disk BusyBox change")
        report["identical_reads"] = report["passes"][0]["sha256"] == report["passes"][1]["sha256"]
        report["complete"] = True
    except BaseException as error:
        report["error"] = type(error).__name__ + ": " + str(error)
        raise
    finally:
        try:
            report["quit_response"] = ftp.quit()
        except Exception:
            ftp.close()
        report["finished_utc"] = datetime.now(timezone.utc).isoformat()
        (output / "acquisition.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--user", default="root")
    parser.add_argument("--password-env")
    parser.add_argument("--busybox-reference", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--allow-temporary-worker-patch", action="store_true", required=True)
    args = parser.parse_args()
    address = ipaddress.ip_address(args.host)
    if not address.is_private or address.is_loopback or address.is_link_local or address.is_multicast or address.is_unspecified:
        parser.error("An explicit private LAN camera IP is required")
    reference = args.busybox_reference.read_bytes()
    validate_reference(reference)
    if args.output.exists():
        parser.error("Output directory must be new")
    password = os.environ[args.password_env] if args.password_env else getpass.getpass("Camera FTP password: ")
    with ftplib.FTP(timeout=30) as ftp:
        ftp.connect(args.host, 21)
        ftp.login(args.user, password)
        report = acquire(ftp, args.output, reference)
    print(json.dumps({key: report[key] for key in
                     ("complete", "identical_reads", "worker_restore_verified", "passes")}, indent=2))


if __name__ == "__main__":
    main()
