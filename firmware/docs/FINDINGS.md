# Firmware evidence — 10 October 2026

## Scope and isolation

Foundational firmware work now belongs in OpenYI's `firmware/` directory, as
directed by the owner. Acquisition originally took place in the separate local
`Siteledger-Camera-Firmware` research directory; its originals remain there.
The commercial Siteledger Surveillance app remains separate and unchanged. The initial work
was offline analysis. Subsequent authenticated FTP file reads and two complete
Wi-Fi flash acquisitions are recorded in [hardware and backup evidence](HARDWARE_AND_BACKUP.md).
Raw acquisition temporarily changed one FTP worker's private RAM, then restored
and verified the instruction and closed that worker. No persistent camera file,
configuration, firmware update or reboot was performed.

## Supplied manual and actual acquisition

The supplied `http://www.yitechnology.com/support-faq.php?id=1` download link
returned 404. Its surviving [official manual](https://api.yitechnology.com/faq/result/ckshengji.html)
names `home`, `home.bin` and `home_v201` for three distinct YI Home/Dome models.
It describes card insertion and power-on; it does not establish reset-based
flashing for our Anyka device. The [legacy firmware catalogue](https://api.yitechnology.com/firmware/index/class/home)
lists old Home/Dome products and does not identify our target. A stale link alone
does not demonstrate deliberate download protection.

The previously inspected mobile APK provides a more specific route:

- Decompiled `g2.l.c` / `g2.c.s`: GET
  `/vmanager/ipc/firmware/upgrade/app`, with `version`, `did`, `sname`.
- `d1.e` selects `https://plt-api-de.xiaoyi.com` for the EU region.
- `o2.f.s`: A-prefixed DID uses response `code=1`, then `result`; the app
  concatenates `downloadPath` and `fileName`.
- The owner's DID from the supplied camera screenshot, installed version
  `6.0.24.10_202401091113` and `sname=AKOpen` returned `needUpdate=true` and
  `6.0.24.10_202607130958`. No login credentials or LAN device key were needed.

A [firsthand Anyka investigation](https://www.valki.com/hacking-einer-china-ptz-webcam/)
independently describes the same API and installed version for an XY-3820. Its
board identification is a useful lead, not proof of our exact PCB or sensor.

The service's public download was:

`https://iot-firmware-eu.oss-eu-central-1.aliyuncs.com/iotfirmware/AKOpen/382/6.0.24.10_202607130958update`

The path component `382` is a vendor routing value. It must not be equated with
the protocol's observed hardware ID 253 without evidence.

The package was downloaded twice, once by the repeatable fetch tool. Both copies
have the same SHA-256. The `20260806-eu` text in the earlier mobile screenshot is
not this package's embedded version; no equivalence is claimed.

| Integrity evidence | Value |
| --- | --- |
| Bytes | 2,897,920 |
| Package SHA-256 | `e6400360df70de2a69ee77937e31a186450d2e3627d59869b50b1e0d0619a177` |
| Package MD5, matches vendor metadata | `362024e1412dfb6ea79747dab54df74e` |
| `usr.sqsh4` bytes | 2,887,680 |
| `usr.sqsh4` MD5, matches included file | `632a46b091bba946c136dedbc7a45359` |
| `anyka_ipc` SHA-256 | `6214c91879c8e466ea9d069fe96b0ab0139f0c692ae9930a4c9d00ee63044d4d` |

The TAR starts with the `usr.sqsh4` header; that filesystem begins at file offset
`0x200`. The filesystem is little-endian SquashFS 4.0, XZ compression, 128 KiB
blocks, 283 inodes and 2,884,832 bytes used. The other two TAR members are
`fw_version` (23 bytes) and `usr.sqsh4.md5` (44 bytes). The version inside the
filesystem also matches. These checks establish internal consistency, not a
cryptographic vendor signature or full-device backup.

## Update loader: static evidence only

In the downloaded filesystem:

- `sbin/service.sh` sets an update flag when `/mnt/update` exists, then calls
  `/usr/sbin/update.sh` during startup.
- `sbin/update.sh` first looks for `/mnt/update/update.tar`, otherwise
  `/tmp/update.tar`. It unpacks into `/tmp`.
- OTA requires a lexicographically newer `fw_version`. The SD path rejects an
  equal version; this is not proof of a generally supported downgrade/recovery.
- `usr.sqsh4.md5` is checked when present. The script invokes `updater local B=...`
  for the application filesystem. Other optional images target other partitions.
- The script also removes stored ISP configuration before updating and reboots.
  Preserving calibration and recovery is therefore material, even for an
  application-only package.

No signature verification was visible in this shell script. FTP readback later
confirmed the installed `update.sh` is byte-identical and its `service.sh` has
the same SD entry points. The underlying `updater` executable and bootloader
acceptance chain have not been fully examined. Do not infer that arbitrary
modified images will be accepted.

## Pairing path in the downloaded binary

`anyka_ipc` is a 32-bit little-endian ARM ELF, flags `0x05000202`, entry point
`0x2697c`. It lacks `.symtab` but retains dynamic function symbols. The two
functions below contain A32 instructions, not Thumb instructions.

| Function / site | Virtual address | File offset / size |
| --- | --- | --- |
| `judge_bindkey` | `0x2d07c` | `0x2507c`; 952 bytes |
| `webapi_do_bindkey` | `0x4f424` | `0x47424`; 1,044 bytes |
| Direct call to `webapi_do_bindkey` | `0x5301c` | Compare result with 1 at `0x53020`; conditional success branch at `0x53028` |

The binding function invokes the bundled cloud helper against `/v5/ipc/qr_bind`,
parses `code` and recognizes `20000` as success. Its caller compares the returned
value with 1. The success path calls `VOICE_PLAY_BY_ID`, `update_camera_status`,
`set_wifi_config_status`, `set_cfg_wifi_connected_status`,
`save_ssid_pswd_to_ini` and `yi_set_value_to_file`.

This confirms that a camera-side cloud-result check is still present. It does not
establish that forcing a return value alone produces usable local pairing. UID,
LAN key, cloud registration, timers and persisted configuration can be initialized
elsewhere. Those dependencies remain to be traced before choosing a modification.

The older research `anyka_ipc` (`6.0.05.10_202301061607`, SHA-256
`f2f977c41214d549022fcc987d5e55758ce8231d55fa0a4ab707c610d38de6e6`) uses different
offsets and retains a regular symbol table. Its patch locations must not be reused.

## Corrections to the proposed generic patching notes

- C boolean values use zero/nonzero, but a particular status-returning API can
  use zero for success, negative errors, enums or a returned object. Establish the
  callee and caller contract. [Arm's procedure-call ABI](https://github.com/ARM-software/abi-aa/blob/main/aapcs32/aapcs32.rst)
  specifies result placement, not a universal authorization convention.
- Changing `BEQ` to `BNE` reverses the condition; it does not always take success.
  Skipping a call also omits every side effect and leaves its return register
  undefined unless explicitly handled.
- `00 00` in little-endian Thumb decodes as a flag-setting operation on R0
  (`MOVS R0,R0`, also described as `LSLS R0,R0,#0`), not a true NOP. An architectural
  Thumb NOP on supporting cores is `0xbf00` (`00 bf` in little-endian bytes).
  See [Arm's compiler guide](https://documentation-service.arm.com/static/65f9979ed98ff22ceb0c11aa).
- `01 20 70 47` decodes to the suggested Thumb return stub, but overwriting an
  A32 function with those bytes is wrong. Boundaries, instruction state, ABI,
  branch range, literal pools and entry/exit stack effects all matter.

At the time of this initial analysis no application or pairing patch had been
applied. The subsequent [installed-build workflow](PATCH_WORKFLOW.md) produces an
offline debug-report suppression candidate; it has not been flashed. The separate temporary
FTP-worker change used to acquire raw flash is documented in the backup evidence.
Reports contain code locations for exact digests, not a universal recipe for
cameras sharing the case or mobile app.

## Verification and next dependency

The vendor package and extracted selected files were inspected without running
vendor binaries. Seven synthetic parser tests pass: known container, bad MD5,
invalid filesystem header, traversal name, duplicate member, link and truncation.
Two independent downloads match; outer and inner MD5 and embedded version match.
Hardware behavior, reboot and update acceptance are not tested.

One version-specific old-image path, derived from the observed URL and the
installed version, returned HTTP 404. No vendor ID enumeration was attempted.
The subsequent FTP investigation supplied actual hardware, flash-map and installed
loader evidence, a complete capture of the selected installed-file roots and two
whole-flash reads. Every region matches except 423 bytes belonging to saved-clock
JFFS2 inode records in partition C. Both root and application SquashFS images are
extracted; all 85 regular files in the installed application filesystem agree
with the earlier FTP file capture. A verified recovery procedure remains outstanding.
Preserve unique calibration and identity data per unit; do not distribute a dump
containing one customer's credentials.

Twenty-one focused tests now pass: seven package-inspection tests, seven
FTP-backup tests and seven raw-dump guard/restoration tests. The real file capture
completed with no failed file reads; all 173 stored regular files match its
manifest. The raw acquisition verified instruction restoration, an unchanged
on-disk BusyBox, worker termination and normal device-node rejection in a fresh
FTP connection. These checks do not constitute a firmware installation test.
