# Local02: optional keyless LAN candidate

**Local01 remains the recommended default.** It removes the identified vendor
dependency while retaining authentication and media encryption with a persistent
owner key. Local02 is a separately selected experiment for inspecting a fully
keyless local protocol. It has no password authentication or media encryption;
any reachable LAN client can use its published protocol marker.

Both profiles patch the same original AK3918E / GC1084 application SquashFS
`6.0.24.10_202401091113` by exact SHA-256. Local02's target version is
`6.0.24.10_202610100003`; its [manifest](../profiles/ak3918e-local02.json)
defines every original/result hash, address and byte sequence. It is not a
patch applied on top of a built local01 image. Stock drivers and libraries
remain proprietary, and private compiled images are not committed.

## Difference from local01

Local02 retains local01's identified cloud/account/upload removal, LAN socket
guards, local binding worker and factory transport identity. It changes these
additional behaviors:

| Site (A32) | Change |
| --- | --- |
| `anyka_ipc` `0x434bc` | Local bootstrap clears runtime/cached password buffers and cloud flags, then succeeds without reading a key file or entropy source. |
| `anyka_ipc` `0x47000` | Local transport readiness no longer requires a populated password. It retains `LOCAL0` and an empty WAN server list. |
| `libYiP2P.so` `0x10838` | Replace the HMAC verifier with an exact 32-byte public protocol-marker comparison. Wrong/empty markers and stock HMAC framing fail. |
| `libYiP2P.so` `0xfe00` | Microphone sender branches to `0xfe04`, bypassing AES. |
| `libYiP2P.so` `0x10104` | Video sender branches to `0xfe0c`, bypassing AES. |
| `libYiP2P.so` `0x106a0` | Speaker receiver branches to `0x106b0`, bypassing AES. |

Library addresses are relative to their ELF image. There are 55 patch ranges
across the same six executables/libraries, plus `fw_version`. Packet headers,
codecs, timestamps, packet acknowledgement and retransmission remain unchanged.
The retained media flags are not used to negotiate encryption.

The command's 32-byte credential field contains ASCII `OpenYI-LAN-v1`, followed
by 19 zero bytes. This is a format marker, not a shared secret or authentication.
The callback's result/output conventions are preserved. Unchanged HMAC/AES
functions can remain in the library, but the tested media paths no longer call
AES key setup. A stored owner key from a different image is not erased on disk;
local02 does not use it for these sessions.

## Matching clients and default behavior

Native C# and Python profiles accept an explicit protocol value:

```json
{"protocol":"openyi-lan-plain-v1"}
```

Absent protocol fields default to `yi-stock`. That mode serves both ordinary
stock cameras and recommended local01, preserving HMAC, device-key validation
and media AES. There is no fallback from failed authentication to keyless mode.
Unknown protocol values fail.

For an explicitly selected local02 connection, clients send the public marker,
query firmware and require exactly `6.0.24.10_202610100003`. They reject stock or
mismatched firmware, skip audio/video decryption and send plaintext talk audio.
Version and UID checks prevent accidental mismatches; they are not cryptographic
identity checks. Camera controls still use the existing packet framing.

**Unresolved hardware version gate:** on the installed local01 camera, the
authenticated `0x1300` response still reported the stock version despite the
verified patched version file and executable. Local02 has not been installed,
and its real response is unknown. The current client gate may therefore reject
a genuine local02 image. Trace and verify the response source before qualifying
this mode; do not weaken the check or infer compatibility from simulations.

The native primary/additional camera editors expose the experimental option.
Pairing verification works on a copy and clears the unused password only in the
new keyless profile. Existing profiles and saved keys remain unchanged unless
the owner explicitly verifies and saves a replacement. Python's loaded profiles
and protocol/reference API support the same mode.

Fresh [local QR provisioning](LOCAL_PAIRING.md) uses the retained scanner format
with a locally generated region marker, just as local01 does. Local02 needs no
key export.
The native guided onboarding's automatic key import is specific to local01.
For local02, use the workshop QR generator and the explicit manual keyless mode,
subject to the unresolved version gate above.

## Offline verification and remaining qualification

Mandatory checks cover all 55 patch reassemblies and 118 named ARM cases:

- 10 keyless bootstrap and marker acceptance/rejection cases, including two
  shared-library load bases;
- 2 callback, 2 startup, 84 destination-guard and 16 provisioning cases;
- 1 unchanged AES primitive vector and 3 actual plaintext microphone/video/
  speaker paths with AES key-setup calls observed and rejected in plaintext mode.

Native simulated-camera tests cover discovery, version rejection, control
readback, unchanged plaintext video/audio, talk acknowledgements/retransmission
and profile migration. Existing authenticated two-camera simulations still pass.
Python reference checks cover default-mode preservation and plaintext command,
media and talk framing. English/Finnish resources cover the new controls.

Building produces two independent, byte-identical packages and fully compares
the decompressed filesystem and metadata. Mandatory verification reconstructs
the intended changes from the pinned source and reruns instruction checks on
the final extracted image. The build's `execution-checks.json`, `patches.txt`,
`contents.json`, `verification.json` and `SHA256SUMS` remain inspectable.

Reference build on 10 October 2026: all 118 named ARM cases and 55 patch
reassemblies passed. Both packages were identical; the application filesystem
occupies 2,826,240 of 3,100,672 available bytes. Reference `update.tar` SHA-256:

```text
b287893de69f81516fd0ba2bf8eb8dc3605196cb2b9c6400e484a8e625d11fb9
```

**Local02 has not been flashed.** Local01's separate
[successful installation](LOCAL01_INSTALLATION.md) does not qualify local02.
Boot, optical QR setup, power cycles,
LAN discovery on actual hardware, media/control/audio behavior, WAN packet
capture, Wi-Fi installation and recovery remain unqualified. Local01's network
guard limits also apply here; these patches are not a kernel firewall.

## Build separately

```powershell
python firmware/openyi_fw.py --build path/to/usr-installed.squashfs --output firmware/build/local02-review --profile ak3918e-local02
python firmware/openyi_fw.py --inspect firmware/build/local02-review
```

The interactive menu also accepts `ak3918e-local02` explicitly, while Enter
selects local01. The final image uses the observed `update.tar` format, not an
assumed `home.bin` wrapper. Flashing remains a separate, image-specific owner
confirmation. The installer currently accepts only the exact pinned stock
source on the camera; switching between patched versions is not silently allowed.
