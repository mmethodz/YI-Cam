# Hardware and Wi-Fi backup evidence — 10 October 2026

## Identification from the owner's running camera

Authenticated FTP readback, without modifying camera files, supplied:

| Item | Device evidence |
| --- | --- |
| Board | `/proc/cpuinfo`: `Cloud39EV2_AK3918E80PIN_MNBD` |
| CPU | ARM926EJ-S revision 5, ARMv5TEJ; saved boot log: AK3918 ID `0x20150200` |
| Kernel | Linux 3.4.35, `AKV_2.5.04`; gcc 4.8.5 / uClibc 0.9.33.2 |
| Installed application | `/usr/fw_version`: `6.0.24.10_202401091113` |
| Sensor | `/sys/ak_info_dump/sensor_id`: `0x1084`; loaded `sensor_gc1084` module |
| Wi-Fi | Loaded module `ZT9101UV20`; this is a driver identification, not a photograph of the chip marking |
| Flash | Saved boot log: XM25QH64C, ID `0x00204017`, 8,192 KiB; sysfs reports NOR, 8,388,608 bytes, 4,096-byte erase blocks |
| Boot arguments | Root `/dev/mtdblock4`, SquashFS, 64 MiB memory argument, serial console `ttySAK0` at 115200 |

This confirms the **E V200 platform** at firmware/kernel readback level. We have
not opened the case or photographed its silicon. The earlier `253` value is the
YI device-info `hardware_version` byte, at offset 8 in the vendor app parser and
OpenYI's observed protocol. The running kernel reports `2.5.04`, so interpreting
253 as kernel/SDK version `2.5.3` is not supported for this camera.

The user's [E-series catalogue](https://www.anyka.com/en/lists/16.html) and
[A-series catalogue](https://www.anyka.com/en/lists/17.html) were retrieved over
HTTPS. The official [AK3918AV100 description](https://www.anyka.com/en/article/17/9.html)
names an ARM926EJ-S core; [AK3918AV200](https://www.anyka.com/en/article/17/8.html)
is a 64-bit dual-core RISC-V product. E and A are product families, not a universal
ARM-versus-RISC-V division. Each incoming unit needs identification before choosing
its toolchain, drivers and firmware. A common app or enclosure is insufficient.

## Stock update provenance and match

The owner reports finding the vendor firmware guidance in browser history after
the desktop app's failed pairing flow; the pairing problem was resolved by reset.
Our actual image acquisition is separately reproducible through the documented
vendor metadata request using this camera's DID, installed version and AKOpen
family. See [the acquisition record](FINDINGS.md#supplied-manual-and-actual-acquisition).
We accept the downloaded image as the correct stock update for this camera.

Further local confirmation from the installed-file backup:

| File | Installed vs. offered update |
| --- | --- |
| `/usr/sbin/update.sh` | Identical, SHA-256 `cdc61349a0db8b7deedd9e7ab14574c1fd31874f28bf9ad01362c87007beeb6d` |
| `/usr/sbin/camera.sh` | Identical, SHA-256 `2a28e57d14ada8b75b92a45f17dfa9b14e6efb88c5f6b88b8ebd706a97465eba` |
| `/usr/modules/akcamera.ko` | Identical, SHA-256 `fb1b55c824116553ff26577d5c4051adfb1e3d5589f3b7491e4bee6245f3de15` |
| `/usr/modules/ak_info_dump.ko` | Identical, SHA-256 `50829f0bd777d868b282700b0fe4e2741fbc22cb4fad909823adfa1837c1123a` |
| `/usr/sbin/service.sh` | Different; both contain SD debug/factory/update entry points |
| `/usr/bin/anyka_ipc` | Different versions; installed SHA-256 `4936afbacdebbbe768bd7537926b04cf1f43e3bd29135290488a214fc49e7c89` |

The driver module's `vermagic` is `3.4.35 mod_unload ARMv5`. Its embedded source
path includes `/V200/kernel/drivers/media/video/plat-anyka/ak_camera.c`. These
static clues now agree with the running camera's identification.

The genuine stock update match is established well enough to use it as the stock
reference. Acceptance of a **modified** package, preservation of persistent state
and recovery from a failed modification are separate engineering work.

## Actual flash map

The saved boot log reports these physical ranges; `/proc/mtd` corroborates sizes:

| Linux device | Name | Start (inclusive) | End (exclusive) | Bytes |
| --- | --- | --- | --- | ---: |
| mtd0 | spi0.0 — whole device | 0x000000 | 0x800000 | 8,388,608 |
| mtd1 | KERNEL | 0x031000 | 0x1b1000 | 1,572,864 |
| mtd2 | MAC | 0x1b1000 | 0x1b2000 | 4,096 |
| mtd3 | ENV | 0x1b2000 | 0x1b3000 | 4,096 |
| mtd4 | A — root SquashFS | 0x1b3000 | 0x2b3000 | 1,048,576 |
| mtd5 | B — `/usr` SquashFS | 0x2b3000 | 0x5a8000 | 3,100,672 |
| mtd6 | C — `/etc/jffs2` | 0x5a8000 | 0x5b8000 | 65,536 |
| mtd7 | D — `/data` | 0x5b8000 | 0x7e2000 | 2,269,184 |

`mtd0` overlaps the other entries: do not concatenate mtd0 through mtd7 into an
image. A full mtd0 read also covers the initial 0x031000-byte area and the final
0x01e000-byte area outside the named partitions. Both are now present in the
raw acquisitions below. The downloaded 2,887,680-byte `/usr` filesystem fits B with
212,992 bytes of partition capacity remaining; that is not a flash acceptance test.

## What FTP actually allows

The saved OpenYI camera address accepted a TCP connection on port 21. Ports 22,
23, 80 and 443 did not accept connections during this check. Anonymous FTP login
failed. A factory credential documented in a [firsthand same-family report](https://github.com/alienatedsec/yi-hack-v5/discussions/236)
succeeded, with filesystem root as the FTP working directory. Credentials are
not embedded in our backup tool or manifest. No dictionary/password enumeration
was used.

The installed startup script launches FTP from system startup. We downloaded
ordinary files and `/proc`/selected sysfs inventory successfully. Direct retrieval
of `/dev/mtd0` and `/dev/mtdblock0` both returned FTP `550 Error`. Neither produced
a flash image. The local zero-byte `.partial` artifacts are retained as failed
attempts, never labelled backups.

The installed BusyBox identifies itself as 1.24.1. Its behavior matches the
[upstream FTP implementation](https://raw.githubusercontent.com/mirror/busybox/1_24_stable/networking/ftpd.c),
whose RETR handler rejects non-regular files. Proc/sysfs inventory can still be
read because those files report a regular-file type. This explains why root FTP
access alone does not provide the raw MTD dump.

Completed capture: `.local/device-backup/file-tree-20261010/`.

- 173 regular files, 17 directory entries, 366 symlink records.
- 11,865,284 bytes transferred including bounded hardware/boot inventory.
- All 173 regular-file sizes and SHA-256 hashes were verified against the manifest.
- Manifest SHA-256: `f251eb50d81086b767c42a13bb82e176c36afaa8ba1c8c30a3160be5fd3d5855`.
- Roots: `/bin`, `/sbin`, `/lib`, `/etc`, `/usr`, `/data`.
- Symlinks, ownership and modes are metadata only. Links were not followed.
- Configuration and sensor archives are private; no file content is committed.

This is a live installed-file capture, not an atomic snapshot, bootloader backup,
kernel image, raw partition dump, restoration test or redistributable firmware.
The original camera services and configuration were left running. The raw
acquisition below is separate from this read-only file-capture step.

## Complete Wi-Fi flash acquisition

**Succeeded on the owner's unit.** Two full reads of `/dev/mtd0ro` were saved
under `.local/device-backup/whole-flash-20261010/`, each exactly 8,388,608 bytes:

| Capture | SHA-256 |
| --- | --- |
| `mtd0-full-pass1.bin` | `22a9059aef7bef3a6d836cb15b427d85ec34ab9fe4e770920ecfd39d58cbb573` |
| `mtd0-full-pass2.bin` | `d4b40ade8ed8f6d69e33af7e01c622e4e357a854de597a59a5268d37da6401f3` |

The full hashes differ because the camera continued running. Every byte outside
partition C is identical: initial boot area, kernel, MAC, ENV, root filesystem,
application filesystem, data partition and trailing area. The **423 differing
bytes in C** all belong to inode 13, directory entry `time`, under `/etc/jffs2`.
The second capture contains later saved-clock records and obsoletes the previous
record. Header, inode/data and directory/name CRC checks pass for the scanned
nodes in C and D in both captures, using the [Linux JFFS2 layouts](https://github.com/torvalds/linux/blob/v3.4/include/linux/jffs2.h).
These checks do not turn live reads into an atomic snapshot or prove restoration.

### Temporary worker change and verification

The exact installed `/bin/busybox` is 692,580 bytes, SHA-256
`3e7d0f2541caacbd728a7bb9341cfa3cf0c4cd30aa3239a4499a967fcac6d6e7`.
It reports BusyBox 1.24.1. Disassembly identifies the RETR handler's regular-file
test. Its A32 instruction at virtual address `0x1dd00` (file offset `0x15d00`)
is `01 00 00 0a`, `BEQ 0x1dd0c`, immediately after masking/comparing `st_mode`.

The existing authenticated FTP connection can read its own `/proc/self/maps` and
`/proc/self/mem`. Its BusyBox executable mapping is private (`r-xp`), begins at
`0x8000` and ends at `0xb1000`. The surrounding runtime code matched the backed-up
binary. We uploaded only `01 00 00 ea` at `0x1dd00` through FTP REST/STOR to that
worker's `/proc/self/mem`: an unconditional branch to the same success label.
This bypasses only the regular-file type test for that connection; open/fstat
errors remain handled. No camera application, authentication routine, executable
file, flash partition or other process was patched.

The [BusyBox upload implementation](https://github.com/mirror/busybox/blob/1_24_stable/networking/ftpd.c)
uses the REST offset for STOR. Linux's process-memory write path uses
`copy_to_user_page`; the [ARM implementation](https://github.com/torvalds/linux/blob/v3.4/arch/arm/mm/flush.c)
performs cache maintenance. The memory read reaches the known mapping gap and
ends with FTP 451; the tool accepts that result only after the exact expected
byte count. It never treats a short/erroring flash transfer as complete.

Observed verification, recorded in `acquisition.json` and
`post-acquisition-check.json`:

1. Exact board, installed version, NOR size, BusyBox hash, worker identity/root
   UID, private mapping, read-only device node and runtime code checked first.
2. Temporary instruction read back correctly.
3. Both raw transfers completed successfully with exact lengths and hashes.
4. Original four bytes restored and the surrounding code read back correctly.
5. On-disk `/bin/busybox` hash remained unchanged; FTP QUIT succeeded.
6. A new connection found the previous worker absent and again rejected raw
   device retrieval. Installed firmware version remained unchanged.

There were no persistent write commands, firmware installation, reboot, reset,
new shell or new network service. The camera itself continued its normal writes,
including the clock records above. This technique is qualified only for the
exact checked build. `tools/dump_flash_ftp.py` refuses an unrecognized build and
records restoration failures instead of reporting success. If the connection
fails, closing it also ends the worker that owns the temporary private mapping.

### Installed versus offered application image

The root SquashFS begins at `0x1b3000`, uses 971,046 bytes, and has SHA-256
`6554739554a11d15b7329d81bdc2fa2cf4503df703131e121bde794648f1788a`
when trimmed to `bytes_used`. The installed `/usr` SquashFS begins at `0x2b3000`,
uses 2,824,044 bytes, and its similarly trimmed SHA-256 is
`1d1b57886c18d14ff74b692de109bcbcf06b19fc30693d5ab7882d009fe40993`.

All 85 regular files extracted from the installed `/usr` image match the earlier
FTP file capture. Against the offered `6.0.24.10_202607130958` application image:

- 72 regular files have identical content, including the updater and existing
  camera/USB kernel modules.
- 13 regular files changed: `bin/anyka_ipc`, `bin/cloudAPI`, `bin/daemon`,
  `bin/testptz`, `fw_version`, `lib/libYiP2P.so`, `lib/liboss.so`,
  `local/yi_cfg.ini`, and `sbin/{anyka_ipc,recover_cfg,service,station_connect,wifi_manage}.sh`.
- Two files were added: `lib/libapp_osd_ex.so` and `modules/wl_pwm.ko`; no paths
  were removed.
- All 85 common regular files changed numeric ownership from 1002:1002 to
  1011:1012. Ownership is recorded separately from content equality.
- Symlinks were inventoried by type and size; their targets are not included
  in these content-comparison counts.

Detailed local results: `partition-comparison.json`, `filesystem-inspection.json`,
`jffs2-crc-checks.json`, and `comparison/report.json` alongside the raw captures.
The subsequent [backup verification](BACKUP_VERIFICATION.md) adds kernel CRC/XZ
checks, full filesystem decompression, root file/link comparisons and an archive
outside the repositories whose members were independently re-read and hashed.
This establishes an installed/current and offered/new baseline for controlled
firmware work. It does not establish the behavior of the changed binaries by
itself, or prove that a modified update will boot.

## Other routes and remaining recovery work

The [MuhammedKalkan project](https://github.com/MuhammedKalkan/Anyka-Camera-Firmware/tree/823cb55a335a3778aff45e2a3147b793b59ca48c)
was developed on AK3918 V200 hardware and provides an SD-based application route.
Its [Factory launcher](https://github.com/MuhammedKalkan/Anyka-Camera-Firmware/blob/823cb55a335a3778aff45e2a3147b793b59ca48c/Factory/config.sh)
starts network services, Telnet, web/PTZ/RTSP applications and a VPN script. That
is broader than a read-only backup; we did not install or execute it. Its images
also require matching sensor/ISP/Wi-Fi support. Our ZT9101UV20 driver must be
preserved or deliberately ported.

The [OpenIPC kernel configuration](https://github.com/OpenIPC/firmware/blob/3449f149072fa0238c7f872e313d74b057ba1578/br-ext-chip-anyka/board/ak3918ev300/ak3918ev300.generic.config)
is useful reference material. However, the current [official Anyka status page](https://openipc.org/cameras/vendors/anyka)
lists no installable Anyka builds: EV200 needs developer help and EV300 lacks
development hardware. A config file is not a qualified replacement image.

The installed camera itself confirms `/mnt/debug/config.sh` and
`/mnt/Factory/config.sh` hooks. The debug hook returns into ordinary startup;
the Factory branch takes over that part of startup. No SD card is currently
mounted: `/mnt` is tmpfs. An SD debug script could read whole-flash `mtd0ro` to a
normal file on the card, then FTP could download that file. This remains untested
on this unit and requires a card insertion/power cycle.

Another route is an authenticated shell that reads flash into a temporary normal
file (subject to memory/storage capacity) or streams it to the PC. Enabling
Telnet by editing `time_zone.sh` would itself change persistent configuration and
may require a reboot; neither action was performed. Editing that script is not
an initial-access method without prior filesystem write access.

The Wi-Fi acquisition above completes the raw-backup requirement without needing
these alternative routes. Before a modified-image installation, establish a
recoverable restore path for the development unit. A successful Wi-Fi dump does
not make FTP available after a non-booting image; retain the raw captures and
test the applicable SD/bootloader or hardware recovery procedure separately.
