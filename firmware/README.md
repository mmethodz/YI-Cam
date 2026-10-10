# OpenYI firmware workshop

One interactive tool: **build → mandatory verification → inspect → Wi-Fi flash**.

```powershell
python firmware/openyi_fw.py
```

Choose **Build and verify**, inspect the output directory, then choose **Flash an
inspected build** when ready. Building and inspection do not contact a camera.
There is no skip-verification switch or automatic flash after compilation.
The final prompt identifies the camera and exact image hash. Cancelling makes
no flash write. Users do not need to run a sequence of internal scripts.

Requirements: Python 3.10+ and Linux `squashfs-tools` plus `fakeroot`. Windows
uses the **Ubuntu** WSL distribution. The reference build used squashfs-tools
4.6.1. Building/installing uses only Python's standard library. Optional static
analysis dependencies are in `requirements-analysis.txt`.

The current profile accepts only the **AK3918E / GC1084** application filesystem
`6.0.24.10_202401091113`, by exact SHA-256. Supply `usr-installed.squashfs`
extracted from your own backup. Original vendor binaries and private device
backups are not included in this repository.

## Verification and inspection

Two independent builds must produce identical bytes. Each filesystem is fully
decompressed and compared with the intended tree, including file hashes, links,
permissions, owners, timestamps and xattrs. A separate mandatory verification
pass reconstructs the intended changes from the pinned source and checks the
final image, package, partition capacity and checksums again.

The inspection directory contains:

- `update.tar`: final installation image, in the observed vendor TAR format.
- `usr.sqsh4`: rebuilt application filesystem.
- `files/`: regular files extracted for easy Windows inspection.
- `contents.json`: full inventory, including Linux metadata and link targets.
- `patches.txt`: exact before/after bytes, addresses and assembly.
- `verification.json` and `SHA256SUMS`: reproducibility and integrity records.

Wi-Fi installation rechecks the image and camera, takes two fresh complete flash
backups, uploads to RAM, and reads every uploaded byte back. The generated RAM
installer is also saved for inspection before the final explicit confirmation.
An ambiguous flash commit is never automatically retried.

**Current candidate: debug-report suppression only, not complete cloud-free
firmware.** Offline build, repeatability and complete filesystem comparisons have
passed on the captured application image. No modified image has been flashed.
The full Wi-Fi installation, boot and recovery remain unqualified; passing
checksums cannot establish those behaviors. See the [exact workflow and evidence](docs/PATCH_WORKFLOW.md).

The installed loader expects **`update.tar`**, containing `usr.sqsh4`,
`fw_version` and `usr.sqsh4.md5`. Do not rename this to `home.bin`: that recovery
format is not established for this unit. The stock SD path is
`/mnt/update/update.tar`, but its script deletes ISP configuration. The workshop's
RAM installer deliberately retains calibration and dispatches only partition B.

Automated **offline** operations use the same entry point:

```powershell
python firmware/openyi_fw.py --build path/to/usr-installed.squashfs --output firmware/build/review-01
python firmware/openyi_fw.py --inspect firmware/build/review-01
python -B -m unittest discover -s firmware/tests -v
```

There is no unattended command-line flash switch. Internal modules in `tools/`
implement the shared workflow and retain historical research utilities.

## Research and ownership

- [Exact patches, build process and Wi-Fi installation](docs/PATCH_WORKFLOW.md)
- [Vendor update and pairing findings](docs/FINDINGS.md)
- [Hardware, partition map and backup acquisition](docs/HARDWARE_AND_BACKUP.md)
- [Backup verification and remaining recovery uncertainty](docs/BACKUP_VERIFICATION.md)

Foundational firmware research and original tooling belong to **OpenYI**, under
the repository's MIT license. Commercial application/backend integration stays
in the separate Siteledger Surveillance repository. Inputs, generated images,
credentials and private backup/audit records remain in ignored `firmware/.local/`
and `firmware/build/`. Our tools' license does not change the licenses of firmware
components being inspected or repacked.
