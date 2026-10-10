# Local01: owner-controlled local camera firmware candidate

Source: `AK3918E / GC1084`, board `Cloud39EV2_AK3918E80PIN_MNBD`, firmware
`6.0.24.10_202401091113`. Target version: `6.0.24.10_202610100002`.
The [profile](../profiles/ak3918e-local01.json) pins the original application
SquashFS and each input/result binary by SHA-256. It refuses other versions.

This is an open-source patch/build workflow over the owner's stock firmware,
not a replacement open-source kernel, ISP, driver or P2P SDK. Vendor binary
licences remain applicable; private input/output images are not committed.
OpenYI retains the stock protocol as its default. This candidate works with that
authenticated protocol; the optional local02 client mode is separate. The
Siteledger commercial application is unchanged.

**Recommended first candidate:** local01 removes the identified vendor dependency
while preserving the tested local authentication and media protocol. The owner
controls the key's persistence and delivery to the client. [Local02](LOCAL02.md)
is a separate keyless experiment, never an automatic fallback.

## Implemented changes

| Area | Candidate behavior |
| --- | --- |
| Device password | Preserve the current 15-character key on migration; otherwise create a random local key. Save as `/etc/jffs2/openyi.key`; reload across process restarts without cloud refresh/expiry. |
| Local authentication | Retain actual HMAC-SHA1 checking and session/replay behavior. Replace the two alternate password arguments with the same canonical key. No anonymous-access success stub. |
| Media | Retain original AES, frame/timestamp, audio and speaker routines. Key derivation remains password + ASCII `0`. |
| Binding/account | Retire `/v5/ipc/qr_bind`, cloud device registration/reset/configuration requests. Preserve the caller's local Wi-Fi save and spoken-success path. Saved Wi-Fi no longer requires a cloud binding token. |
| Local transport startup | Supply an empty server list and local six-character listen value (`LOCAL0`); preserve factory TNP ID/device key. Keep normal authenticated mode, skip WAN discovery, pass local listen flags. |
| Periodic registration | Exit the worker after initial local readiness/binding. No periodic password refresh, cloud clock/hardware/log polling. Failed key initialization exits without invoking vendor binding-failure configuration recovery. |
| Uploads/notifications | Retire object-storage initialization/cleanup workers, motion upload, event upload/update, app notification and debug-report entry points. Local detection/tracking routines remain unchanged. |
| Cloud helper/OTA | `cloudAPI` exits before executing its main command body. Vendor-selected OTA callback is retired; the explicit owner workshop remains the update route. |
| Socket imports | Guard known app/P2P network imports; retire cloud-library socket/resolver imports. Block public IPv4, IPv6 and DNS destinations on guarded paths; retain private IPv4, loopback, link-local, limited broadcast and local IPC. |

Six ELF files change: `anyka_ipc`, `cloudAPI`, `libYiP2P.so`, `libcloudapi.so`,
`liboss.so`, `libcrypto.so.1.0.0`, plus `fw_version`. Fifty-one precise patch
ranges total 1,780 bytes. Filesystem entries, binary lengths and all other bytes
are preserved. Some retired cloud code remains unreachable inside the original
binary rather than being removed from the ELF layout.

The profile includes network-guard assembly in space formerly occupied by
retired upload/WAN-detection functions. Their public entries return directly;
guards use ABI-preserving, position-independent GOT trampolines. An early
assembler expression error in one trampoline was caught by execution tests
before any image was flashed. Tests now exercise both allowed and denied paths
at multiple shared-library load addresses.

## What the mandatory offline pass establishes

- Every declared patch reassembles from reviewed source to its exact bytes.
  Assembly source hashes and LF line endings are pinned.
- Sixteen key scenarios exercise the real replacement ARM routine: existing
  key, migration, entropy, restart, short reads/writes, malformed files and
  failed permission/read/write/fsync/close operations.
- The unchanged camera HMAC/SHA1/Base64 code matches an independent Python
  calculation. Correct/wrong keys, legacy alternate credentials, session
  caching and nonce replay have explicit checks. The app callback can supply
  only the canonical credential in local01.
- Actual AES encryption/decryption matches a FIPS-197 vector. Actual video and
  microphone senders use the expected key. Actual speaker receive recovers the
  encrypted payload using OpenYI's talk-back header and the same key.
- The registration worker follows local saved-Wi-Fi/fresh-binding paths and
  exits on key failure without configuration recovery. Voice and persistence
  operations are observed through test boundaries, not sent to hardware.
- Sixteen provisioning cases execute the original QR field/Base64/password
  parser and region selection with locally generated markers. Optical scanning,
  Wi-Fi association and the physical success prompt remain untested; see
  [local provisioning](LOCAL_PAIRING.md).
- Eighty-four destination-guard cases cover LAN/public/DNS/IPv6/truncated
  addresses/local IPC, return values, errno, arguments, stack and preserved
  registers, including two library load bases.
- Two independently packed images must match byte-for-byte. Both are fully
  extracted and compared, including all 281 entries and metadata. Final
  verification reconstructs the expected modifications independently of saved
  checksums, checks package structure/capacity, and reruns execution checks.

An offline integrity pass is mandatory for building, inspection and installation.
There is no skip flag. Its receipt is not a signature, boot qualification, or
claim that every possible program path has been executed.

Reference build on 10 October 2026: **131 named ARM cases**, all **51** patch
reassemblies, and **59** tooling tests passed. The two independent packages were
identical. Application filesystem size: **2,826,240 / 3,100,672 bytes**; all
281 entries passed comparison. Independent 7-Zip full decompression tests of
both SquashFS and TAR also passed. Reference `update.tar` SHA-256:

```text
1b79b956861ef8bf1776faf283e553cc1d8dd3f7248036245a4daae9a9c800bd
```

## Owner workflow

Run `python firmware/openyi_fw.py` and choose **Build and verify**, supplying
the original application SquashFS. The default profile is local01. Inspect
`update.tar`, `usr.sqsh4`, `files/`, `patches.txt`, `contents.json`,
`execution-checks.json`, `verification.json` and `SHA256SUMS` before choosing
the separate flash action. The image never embeds a shared/default owner key.

The [Wi-Fi installer](PATCH_WORKFLOW.md#wi-fi-installer) takes fresh backups,
checks the exact stock source and device identity, stages in RAM, reads staged
bytes back, requires an image-specific confirmation, and audits the installed
B payload after reboot. It does not overwrite bootloader/kernel/identity/ISP
partitions. It currently qualifies upgrades **from the pinned stock version**;
upgrading from another patched build is not silently allowed.

After a successful local01 boot, the optional key export reads the canonical
file and creates a new Windows DPAPI profile, suitable for OpenYI's existing
import/verification flow. It does not display secrets or overwrite current app
settings. An existing client key should survive first migration when valid.
See [the full key lifecycle and retrieval details](LOCAL_KEY_MAP.md).

The key export is an explicit owner action using the camera's root FTP access;
it verifies the installed version and patched binary before reading the key.
The workshop writes a Windows DPAPI profile without displaying the key. FTP
itself is unencrypted; this is a LAN maintenance transfer, not public key
discovery or a remote pairing service. Routine client connections continue to
use the saved key. A remote dashboard's authentication and HTTPS are a separate
connection and are not supplied by this firmware patch.

The emitted payload is the observed **`update.tar`** format. `home.bin` recovery
for this board has not been established; renaming a TAR does not establish it.

## First installation and remaining physical checks

**Local01 was flashed successfully on the reference unit on 10 October 2026.**
The workshop verified the target version, all patched binaries and the complete
B image after reboot. Independent offline comparison of its full readback also
confirmed unchanged boot/kernel/identity/root and data D regions. See the
[installation evidence and precise limits](LOCAL01_INSTALLATION.md).
The owner also confirmed that OpenYI connected with its existing saved pairing
and displayed live video with the vendor app closed.
After a separate unplug/reconnect power cycle, the owner confirmed another
successful connection with that saved pairing, without a vendor-app refresh.
On 11 October, the owner physically reset the unit and scanned a locally generated
QR. The camera announced recognition, Wi-Fi connection and pairing success; OpenYI
reconnected. Direct readback confirmed the same canonical key and factory identity.
The installed native app subsequently found the camera, read its Wi-Fi settings,
imported its key directly and displayed 1280 × 720 video. Internet was available.

Before calling this fully camera-qualified, measure recording, PTZ, IR/color
night modes, tracking and two-way audio; repeat the connection and power-cycle
checks with Internet blocked. One cold-boot reconnection has passed with the
existing key, but WAN availability was not controlled in that test.
Test actual first-time QR provisioning using a locally generated token with
WAN blocked and capture traffic during startup and idle operation.

The socket guards cover the identified imports in the selected user-space
processes; they are not a kernel firewall. Driver/kernel traffic, alternate
executables, raw syscalls, upstream-LAN proxies, firmware variants, concurrent
key changes, hardware entropy quality and every reset/power-loss path are not
proven by these tests. Private-address traffic is still allowed intentionally.
Internet-blocked power-cycle testing, network capture and recoverability remain prerequisites for a
release described as isolated or cloud-free.

Fresh QR parsing, Wi-Fi/sensor drivers and factory identity are retained. The
workshop and native app can now generate the retained syntax with a local marker
and save it as a PNG for display on a phone. Its exact parser and local-success
paths pass offline execution; physical local-marker setup has passed on one unit
with Internet available. Factory installation with no prior canonical key and
WAN-isolated provisioning remain separate qualification cases.
The first Wi-Fi installation is verified; failed-boot recovery remains untested. A complete verified
backup establishes backup integrity, not a demonstrated unbrick procedure.
