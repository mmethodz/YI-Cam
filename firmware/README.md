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
4.6.1. The local01/local02 profiles additionally **require** the pinned packages in
`requirements-analysis.txt` for assembly and offline ARM execution. Install
them in the build Python environment (inside Ubuntu when using WSL), for example:

```sh
python3 -m pip install --target firmware/.local/python-runtime -r firmware/requirements-analysis.txt
```

This isolated directory is used by the Linux build engine. No vendor runtime is
executed on the host. Windows key export uses the existing Python DPAPI helper.

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
- `execution-checks.json`: mandatory assembly, selected key/authentication/media,
  QR parser, startup-flow and destination-guard execution results.

Wi-Fi installation rechecks the image and camera, takes two fresh complete flash
backups, uploads to RAM, and reads every uploaded byte back. The generated RAM
installer is also saved for inspection before the final explicit confirmation.
An ambiguous flash commit is never automatically retried.

**Recommended default: `ak3918e-local01`, a local/account-free firmware candidate.**
It supplies a persistent owner-controlled key and local bootstrap, removes the
identified vendor account/upload/notification/periodic-login paths, and guards
the known cloud/P2P socket imports. Normal local authentication, media encryption
and the original camera control routines remain. See the
[candidate's exact scope and remaining tests](docs/LOCAL01.md) and
[credential reader/writer map](docs/LOCAL_KEY_MAP.md).

The separately selected [local02 candidate](docs/LOCAL02.md) removes command
authentication and media AES, with matching native/Python client support. It is
an explicit experiment, never a fallback from local01 or failed authentication.
Owner control of the key lifecycle and account-free provisioning do not require
keyless mode. Remote dashboard authentication/HTTPS remain a separate app concern.

Offline instruction execution, reproducible builds and complete filesystem
comparisons have passed. **Local01's first Wi-Fi installation, reboot and full
application-image readback succeeded on 10 October 2026.** See the
[installation evidence](docs/LOCAL01_INSTALLATION.md). The owner also confirmed
authentication and live video with the existing saved pairing, followed by a
successful reconnection after a separate cold power cycle without a vendor-app
refresh. A physical reset and locally generated QR setup succeeded on 11 October;
the key and transport identity survived, and native direct import/live video passed.
Internet was available. Other camera functions, longer-term stability, WAN isolation
and recovery remain unqualified. Local02 has not been flashed. The older
`ak3918e-debug01` diagnostic profile
is still selectable explicitly with `--profile`; it only suppresses debug reports.

The Windows app's default **Camera pairing → OpenYI firmware setup** now handles
Wi-Fi autofill, QR preview/save, LAN discovery and verified direct key import.
It requires the reviewed local01 firmware; no vendor account or pairing ID is
requested. The workshop remains an independent reference/export route.

After local01 is installed and booted, choose **Export local01 camera key for OpenYI** to
save a new Windows-encrypted `.dpapi` file. Import it using OpenYI's existing
**Import encrypted pairing profile** action. Migration preserves the existing
15-character key when valid; no automatic vendor-app refresh is needed by design.
The export is an explicit LAN maintenance action, never part of offline building.

Choose **Save local setup QR** to generate a PNG offline for display on a phone.
This uses the retained camera parser with a locally generated region marker,
for local01/local02 only. Install the optional `requirements-qr.txt` packages in
the menu's Python environment. The saved image is decode-checked before success.
The native app also provides this distinct QR mode. See the
[exact payload, key handoff and physical-test limits](docs/LOCAL_PAIRING.md).

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
- [Local01 cloud/account removal candidate](docs/LOCAL01.md)
- [Local01 first installation and remaining hardware checks](docs/LOCAL01_INSTALLATION.md)
- [Local02 optional keyless candidate and matching clients](docs/LOCAL02.md)
- [Local key persistence, readers, writers and media use](docs/LOCAL_KEY_MAP.md)
- [Account-free QR composition and owner onboarding](docs/LOCAL_PAIRING.md)
- [Vendor update and pairing findings](docs/FINDINGS.md)
- [Hardware, partition map and backup acquisition](docs/HARDWARE_AND_BACKUP.md)
- [Backup verification and remaining recovery uncertainty](docs/BACKUP_VERIFICATION.md)

Foundational firmware research and original tooling belong to **OpenYI**, under
the repository's MIT license. Commercial application/backend integration stays
in the separate Siteledger Surveillance repository. Inputs, generated images,
credentials and private backup/audit records remain in ignored `firmware/.local/`
and `firmware/build/`. Our tools' license does not change the licenses of firmware
components being inspected or repacked.
