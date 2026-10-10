# Installed firmware backup verification — 10 October 2026

**Result: complete flash acquisition and integrity verification succeeded.**
The owner now has two full 8,388,608-byte images of this camera's SPI NOR flash,
including its boot area, kernel, root and application filesystems, identity,
configuration, calibration and data. No restore or modified-firmware boot has
been attempted. Those are separate hardware tests.

## Verification performed

| Check | Result |
| --- | --- |
| Whole-device size | Both FTP transfers completed with exactly 8,388,608 bytes, matching live `/proc/mtd` and sysfs |
| Independent reads | All bytes outside partition C are identical; includes initial/trailing areas, kernel, MAC, ENV, A, B and D |
| Mutable difference | Exactly 423 bytes; every differing byte lies within saved-clock inode 13 (`time`) records in partition C |
| Boot area | Identical in both captures; Anyka header `AKSH_240` and `U-Boot 2013.10.0-AK_V2.0.04 (Jun 05 2023 - 14:05:38)` present. No independent embedded bootloader checksum is claimed |
| Kernel container | uImage header CRC and complete payload CRC pass; ARM/Linux metadata and zImage magic are correct |
| Kernel decompression | Embedded XZ stream passes its integrity check; expands to 4,033,884 bytes; embedded kernel version matches live `/proc/version` |
| Root SquashFS | Full 7-Zip test succeeds: 209 file entries, 18 directories |
| Application SquashFS | Full 7-Zip test succeeds: 271 file entries, 9 directories |
| Separate live-file comparison | All 36 root and 85 application regular files match the earlier FTP capture by SHA-256 |
| Separate symlink comparison | 171 root and 186 application link targets match the earlier FTP manifest |
| Writable filesystem records | Scanned JFFS2 header, inode/data and directory/name CRCs pass in C and D, for both images |
| Acquisition cleanup | Temporary FTP-worker instruction restored and verified; on-disk BusyBox unchanged; worker exited; new worker again rejects raw device files |
| Archive verification | Every ZIP member re-read after writing, length/SHA-256 matched; archive SHA-256 saved separately |

The kernel uImage is 1,416,776 bytes, SHA-256
`4a63bb61c06c89098249e2706ea61f95abe555730f43fe0fb3fcd66243977772`.
The [acquisition and partition record](HARDWARE_AND_BACKUP.md#complete-wi-fi-flash-acquisition)
contains both full-image hashes and the temporary-worker method.

These are live captures: the camera continued writing its clock, and CRC checks
do not prove atomic filesystem state. No detected integrity failure remains in
the checks above. A successful SD/bootloader/programmer restoration is the
remaining evidence needed to claim tested recovery, particularly if a future
image cannot boot far enough to provide FTP.

## Private backup locations

The original captures and detailed audit remain in the historical local
`Siteledger-Camera-Firmware` research directory, under:

`.local/device-backup/whole-flash-20261010/`

A second packaged copy is outside all Git repositories:

`%USERPROFILE%/Documents/Siteledger/Camera Backups/AK3918E-GC1084-20261010/stock-camera-backup.zip`

Archive size: **16,273,576 bytes**, 20 entries. Archive SHA-256:

`1460d7908f5ccbf872dc028dd32a2c8b5a3ba9f96466f8759991bb0dc5968808`

The archive includes both raw images, the genuine offered application update,
verification/partition records, the live-file manifest and `SHA256SUMS.txt` for
its payloads. A companion `.sha256` file and `verification.json` sit beside it.
This is an additional local copy, not confirmed independent physical storage.
For disk-failure protection, copy this archive and its checksum to another drive.

The images contain this unit's credentials and identity and remain private.
No original flash files or private audits are committed. At acquisition time the
application repositories were unchanged. Firmware documentation and tooling now
live in public OpenYI under `firmware/`; commercial Siteledger Surveillance
remains separate and unchanged.
