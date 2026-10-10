# Installed AK3918E patch workflow — 10 October 2026

## Evidence boundary

Target: `Cloud39EV2_AK3918E80PIN_MNBD`, GC1084, ARM926EJ-S (ARMv5TEJ),
8 MiB NOR. Installed version `6.0.24.10_202401091113` is the first patch baseline.
The newer offered package has different addresses and is not this profile's source.

The backups passed acquisition/integrity checks, not restoration tests. All
regions agree between the original reads except 423 bytes of saved-clock
records in C. See [the audit](BACKUP_VERIFICATION.md). Offline checks establish
exact image contents and changes, not the modified program's behavior or
guaranteed boot/recovery. No modified image has been installed during this work.

## Located routines and dependencies

Installed `bin/anyka_ipc` SHA-256:
`4936afbacdebbbe768bd7537926b04cf1f43e3bd29135290488a214fc49e7c89`.
These virtual addresses apply **only to this binary**, using A32 instructions.

| Routine/site | Address | Finding and decision |
| --- | --- | --- |
| `judge_bindkey` | `0x234dc` | QR/bind-key parsing; not proof of offline token cryptography |
| `webapi_do_login` | `0x434bc` | Copies response/cached data into key and configuration buffers, then saves state; do not blindly return success |
| `webapi_do_bindkey` | `0x43ea8` | Runs `cloudAPI` against `/v5/ipc/qr_bind`, interprets JSON code `20000`, returns 1 on success |
| Binding call | `0x479b4` | Caller compares return to 1, then enters spoken-success and configuration-persistence path |
| `webapi_do_event_update` | `0x443ac` | Cloud event path; separate from local detection/tracking |
| `yi_cloud_init` | `0x46128` | Mixes local startup/IP/version/key/state setup with cloud calls and worker creation |
| `yi_report_debug_info` | `0x46c08` | Debug-upload implementation |
| `yi_report_debug_strings` | `0x46ea8` | Variadic wrapper returns 0 after reporting; first bounded patch candidate |
| `webapi_do_tnp_on_line` | `0x47000` | Calls `/v4/tnp/on_line` and populates returned state; not a proven side-effect-free heartbeat |
| Keepalive worker | `0x478cc` | Binding/online/login work; requires state-machine tracing |
| `is_binded` | `0x4a3cc` | Reads state; does not itself verify a cloud token |
| `yi_p2p_on_auth` | `0x2d3e4` | Delegates local authentication to `yi_p2p_do_auth`; retained |

`liboss.so` contains `yi_get_token` (`0x27c90`) and `yi_oss_cloud_init`
(`0x2c668`). `libYiP2P.so` has `yi_p2p_init` (`0xea88`). These addresses are
relative to their respective ELF libraries. Disabling one callback does not
establish that either library stops WAN traffic.

The login routine copies 32-byte data to `0x51b1dc` and configuration offset
`0x74` at base `0x4b1f90`, then calls `yi_save_cfg`. The meaning and initialization
requirements of each key must be established before replacing login. Local
device-key authentication is separate from vendor-account login.

## First precise patch

Profile: [`ak3918e-debug01.json`](../profiles/ak3918e-debug01.json).

At VA `0x46ea8`, file offset `0x3eea8`, replace eight bytes **before any prologue
stack changes**:

```text
original: 0c 00 2d e9  f0 40 2d e9   ; PUSH {r2,r3}; PUSH {r4-r7,lr}
patched:  00 00 a0 e3  1e ff 2f e1   ; MOV r0,#0; BX lr
```

The original wrapper's normal return is 0. This skips its debug reporting while
preserving that return contract. It does not patch a Thumb entry, replace a
post-prologue return, or disable local authentication.

Expected patched binary SHA-256:
`0849c22507bf07039c5c08de45f921819e5452fea8094558a80e473d2db0b15e`.
The only other changed file is `fw_version`, set to `6.0.24.10_202610100001`
to satisfy the installed OTA version comparison.

The binding call at `0x479b4` contains `3b f1 ff eb` (BL binding routine).
A later isolated experiment could replace that call with `01 00 a0 e3`
(MOV r0,#1), retaining the caller's spoken sample, state updates, Wi-Fi and
bind-key persistence. **This is documented, not included in the active profile.**
It skips a cloud gate but does not supply UID/media/local keys or establish
fresh offline provisioning. Replacing login or the whole cloud initializer
would omit unresolved initialization.

BEQ→BNE reverses a condition; it does not always take success. Skipping a call
must define the expected result and account for omitted side effects.
ARMv5TEJ is not Thumb-2. The [Arm AAPCS](https://github.com/ARM-software/abi-aa/blob/main/aapcs32/aapcs32.rst)
defines result placement, not a universal 0/1 success convention.

The optional `tools/analyze_elf.py` produces symbol, direct-call and disassembly
reports without executing the ELF. A32 call scanning cannot exclude indirect
callbacks; linear disassembly includes literal pools. Literal annotations are
not complete pointer/data-flow analysis.

## Rebuild and mandatory offline verification

One public entry point: `python firmware/openyi_fw.py`. Build stages run in
temporary Linux directories under `fakeroot`, preserving ownership and links
when Windows/NTFS hosts the input/output. Vendor programs are never executed.

1. Require exact source filesystem/binary SHA-256, ARM ELF architecture,
   executable-segment mapping, A32 alignment, original bytes and context.
2. Reject overlapping or variable-length patches; require the expected result digest.
3. Preserve metadata and explicitly compare the complete changed-file set.
4. Repack SquashFS 4.0/XZ, 128 KiB blocks, one worker, fixed source creation
   timestamp. Fully decompress and compare all files, links and metadata.
5. Require image size within B: **3,100,672 bytes**. Build a canonical three-file
   USTAR package, fixed metadata, inner MD5 and external SHA-256 manifest.
6. Repeat extraction, patching and packing independently; require identical TAR bytes.
7. Perform a separate mandatory verification pass: reconstruct the intended
   changes from source; decompress the final image; verify the complete tree,
   package structure, padding, hashes and inspection artifacts.
8. Stop for inspection. Flashing is a separate explicit action.

Individual original file mtimes are preserved rather than globally clamped.
See [squashfs-tools 4.6 usage](https://github.com/plougher/squashfs-tools/blob/master/USAGE-4.6).
No identity, calibration, Wi-Fi credentials or raw-flash partition is packaged.

The first candidate filesystem is **2,826,240 bytes**, leaving **274,432 bytes**
in B. Its package SHA-256 is:
`c14e89a2ab5bc7f12d22506f03561fbc5b5ab87eed117056ac031f7dc79b225e`.
All **281 entries** (85 regular files, 186 symbolic links and 10 directories)
passed comparison. Two independent builds matched. An additional independent
7-Zip full decompression test and PowerShell package-hash calculation passed.

Forty-seven focused tests pass, covering parser/source/patch guards, upload
readback, cancelled review, restoration failure, ambiguous commit without retry,
and complete post-flash payload comparison. Two additional offline adversarial
checks rejected a changed package byte and an unreviewed filesystem edit even
after every package/build-record checksum was recalculated. The latter was
rejected by the independent source-to-final-tree comparison, not a checksum flag.

## Wi-Fi installer

The regular authenticated P2P upgrade command is `0x1302` (response `0x130f`).
For this build `libYiP2P.so` invokes `yi_p2p_on_update`, which ignores the mobile
app's supplied URL, calls `yi_get_update_info` for vendor metadata, then starts
the downloader at `0x2c6dc`. On success it copies `update.sh` to RAM and executes
it. Sending our URL in that command could install a vendor-selected image;
the workshop does not send it.

The available root FTP service already proved full flash readback. Local
installation stages only in `/tmp` and uses a temporary change to one authenticated
FTP worker to launch a fixed waiting script:

- Require exact BusyBox SHA-256/mapping, root worker, source version, board,
  updater/script digests, live application hashes and B partition map.
- Replace the worker's STAT handler at VA `0x1db54` (20 bytes). The A32 stub
  loads a fixed command, calls imported `system` at `0xbc94`, emits the ordinary
  200 reply via `0x1cb00`, then returns to the loop at `0x1d8c0`.
- Put `/bin/sh /tmp/openyi-run.sh </dev/null &` in the worker's private status
  text at `0xa35e8`. On-disk BusyBox stays unchanged; no arbitrary command option
  is exposed by the tool.
- The script reports readiness and waits for a matching random token. Both worker
  byte ranges must be restored and read back before the token is atomically
  renamed into place. A waiting script expires after 180 seconds.

The write is performed by the installed `/sbin/updater local B=/tmp/usr.sqsh4`.
The RAM copy of the hash-pinned vendor script retains watchdog/service shutdown
order, with these reviewed changes: omit the call deleting `/etc/jffs2/isp*.conf`;
dispatch B only; omit audio/special-script dispatch; stop on a nonzero updater
status; require outer/inner MD5 before service shutdown. Generated scripts are
saved for review alongside the private installation audit.

Two fresh 8 MiB backups are required before staging. The installer verifies the
source application and kernel uImage CRCs and requires unchanged regions outside
mutable C to agree between reads. Existing staging/other update files cause a
refusal. Uploads are read back byte-for-byte, including after the final user review.
The final confirmation identifies camera address/MAC/version and image hash.
Full offline verification runs again before staging and after review. Once a
commit might have been sent, there is no automatic retry. After reboot, camera
identity, installed version and patched binary digest are checked. A complete
NOR read through the previously audited temporary RETR gate verifies every byte
of the installed B payload against the image and is retained privately. The
worker must be restored again. A disconnect alone is never reported as success.

**Qualification:** source/assembly review and focused failure-path tests have
passed. The full Wi-Fi flash, modified-image boot, and recovery remain **untested**.
Checksums cannot qualify those behaviors. FTP depends on working Linux/network
startup, so this is not a demonstrated recovery route for a non-booting image.

The installed SD script accepts `/mnt/update/update.tar`, but also deletes ISP
configuration and has not been exercised here. Bootloader strings include
`tfupdateimage` and `u-boot.bin`; `home.bin` recovery is not established for this
unit. The tool emits the observed TAR, not a guessed `home.bin` wrapper.

## Remaining cloud-free work

Trace startup/P2P/key state; define local identity and key provisioning; test
reused/altered/expired/missing/random bind keys; separate WAN P2P/object-storage
workers from LAN media; capture traffic with Internet blocked; verify video,
recording, PTZ, night mode, tracking and two-way audio after each change.
A return stub does not establish that all telemetry has disappeared.
Foundational research stays in OpenYI; commercial application features stay separate.
