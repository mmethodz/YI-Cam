"""Read-only inspection of the observed AKOpen application-only TAR format.

No firmware execution, filesystem mounting, device access, flashing or generic
archive extraction. Recognizing this format does not prove camera compatibility.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import struct
import tarfile

MAX_IMAGE = 32 * 1024 * 1024
MEMBERS = {"usr.sqsh4": 16 * 1024 * 1024, "fw_version": 128,
           "usr.sqsh4.md5": 256}


def inspect_bytes(data: bytes) -> tuple[dict, dict[str, bytes]]:
    if not data or len(data) > MAX_IMAGE:
        raise ValueError("Image is empty or exceeds the 32 MiB inspection bound.")
    files: dict[str, bytes] = {}
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as archive:
        # Iterate with a fixed member limit; don't enumerate an unbounded archive.
        for member in archive:
            if member.name not in MEMBERS or member.name in files:
                raise ValueError("Unexpected or duplicate TAR member; this format is not qualified.")
            if not member.isfile() or member.issparse() or member.linkname:
                raise ValueError("Only ordinary files are accepted; no links or sparse files.")
            if not 0 < member.size <= MEMBERS[member.name]:
                raise ValueError("TAR member size is outside the observed format bounds.")
            stream = archive.extractfile(member)
            if stream is None:
                raise ValueError("Missing member content.")
            content = stream.read(MEMBERS[member.name] + 1)
            if len(content) != member.size:
                raise ValueError("Truncated TAR member.")
            files[member.name] = content
    if set(files) != set(MEMBERS):
        raise ValueError("Expected application filesystem, version and MD5 members.")

    version = files["fw_version"].decode("ascii").strip()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,95}", version):
        raise ValueError("Unrecognized version field.")
    checksum = files["usr.sqsh4.md5"].decode("ascii").strip()
    match = re.fullmatch(r"([0-9A-Fa-f]{32})[ \t]+\*?usr\.sqsh4", checksum)
    if not match:
        raise ValueError("Unrecognized filesystem checksum entry.")
    filesystem = files["usr.sqsh4"]
    md5 = hashlib.md5(filesystem).hexdigest()
    if md5 != match[1].lower():
        raise ValueError("Filesystem MD5 does not match the package.")
    if len(filesystem) < 96:
        raise ValueError("Truncated SquashFS superblock.")
    fields = struct.unpack_from("<5I6H8Q", filesystem)
    (magic, inodes, created, block_size, fragments, compression, block_log,
     flags, ids, major, minor, root_inode, bytes_used, *tables) = fields
    if magic != 0x73717368 or (major, minor) != (4, 0):
        raise ValueError("Expected little-endian SquashFS 4.0.")
    if not 4096 <= block_size <= 1048576 or block_size != 1 << block_log:
        raise ValueError("Invalid SquashFS block size.")
    if not 96 <= bytes_used <= len(filesystem):
        raise ValueError("SquashFS extends beyond its member.")
    if compression != 4:
        raise ValueError("Only the observed XZ-compressed variant is qualified by this inspector.")
    return {
        "schema": 1,
        "format": "AKOpen application-only TAR candidate",
        "version": version,
        "bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "md5": hashlib.md5(data).hexdigest(),
        "members": [{"name": name, "bytes": len(content),
                     "sha256": hashlib.sha256(content).hexdigest()}
                    for name, content in files.items()],
        "filesystem": {"kind": "SquashFS", "version": "4.0", "compression": "xz",
                       "block_bytes": block_size, "bytes_used": bytes_used,
                       "inodes": inodes, "fragments": fragments,
                       "created_unix": created, "md5_matches": True},
        "limitations": ["Checksums are integrity checks, not vendor signatures.",
                        "This is not a full flash backup.",
                        "Container checks do not establish bootability or qualify a device's loader.",
                        "Nothing was executed, patched or flashed."],
    }, files


def inspect_file(path: Path) -> tuple[dict, dict[str, bytes]]:
    with path.open("rb") as source:
        data = source.read(MAX_IMAGE + 1)
    return inspect_bytes(data)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument("--report", type=Path, help="New JSON file; never overwritten")
    parser.add_argument("--extract", type=Path, help="New directory for the three approved members")
    args = parser.parse_args()
    report, files = inspect_file(args.image)
    if args.report and args.report.exists():
        raise ValueError("Report already exists.")
    if args.extract:
        args.extract.mkdir(parents=True, exist_ok=False)
        for name, content in files.items():
            # All names were matched against fixed, flat names above.
            with (args.extract / name).open("xb") as output:
                output.write(content)
    output_json = json.dumps(report, indent=2) + "\n"
    if args.report:
        with args.report.open("x", encoding="utf-8") as output:
            output.write(output_json)
    print(output_json, end="")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, tarfile.TarError, UnicodeError) as error:
        raise SystemExit(f"Inspection refused: {error}") from None
