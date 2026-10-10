# Observed YI TNP LAN protocol

This document describes interoperability observations for an owned camera. Code
in `yicam/protocol.py` and `src/YiLocal.Core/CameraClient.cs` implements these observations. No vendor binary or
cloud connection is needed after obtaining the paired device key.

## Session and transport

UDP packets begin with `F1`, a type byte, and a big-endian 16-bit payload length.
Send type `30` with no payload to the configured private IPv4 address, port 32108.
The camera replies with type `41` from an ephemeral port. The first 20 payload
bytes are its identity (`8s, uint32, 8s`). Return type `41` with those 20 bytes to
that peer. Type `42` confirms the session. Pin the observed identity after the
first successful authenticated command; do not silently accept another device
at the saved address.

The discovery reply comes from a different UDP port than the request. A stateful
host firewall may therefore require an inbound allowance for the app on the local
network. On the development Windows PC, the user allowed the native executable
before its direct LAN connection worked. No Internet port forwarding is needed.

Type `E0` is answered with `E1`. Type `F0` closes the session.

Type `D0` contains a four-byte `D1, channel, sequence16` header followed by a
fragment of that channel's ordered byte stream. Acknowledge received fragments,
including duplicates, with type `D1` and payload `D1, channel, count16, seq16…`.
Each channel has independent wrapping 16-bit sequence numbers starting at zero.
Buffer reordered fragments; never concatenate raw UDP arrival order. Retransmit
unacknowledged outgoing control/audio packets with their original sequence number
and payload. Track acknowledgements by **(channel, sequence)**; a channel-1 ACK
must not clear a channel-0 command with the same numeric sequence, or vice versa.

Channels: 0 commands, 1 audio, 2 live I frames, 3 live P frames, 4 recorded I frames,
5 recorded P frames. The current app uses live video only.

Each reassembled message has an eight-byte TNP header: version, kind, ability
result, reserved byte, big-endian body length32. This camera uses version 2;
kinds are 1 video, 2 audio, 3 command.

## Command authentication

The command body begins with a 40-byte header:

| Offset | Field |
| --- | --- |
| 0 | command16 |
| 2 | command number16 |
| 4 | extension length16 |
| 6 | data length16 |
| 8 | 32-byte authentication field |
| 40 | extension, then command data |

Use a fresh 15-character nonce for each command. The observed client combines a
random seven-character session prefix with eight fresh characters. Compute:

```text
signature = Base64(HMAC-SHA1(device_key, "user=xiaoyiuser&nonce=" + nonce))[:15]
auth = (nonce + "," + signature).ASCII, zero-padded to 32 bytes
```

Replies replace the authentication field with a big-endian result32 at offset 8.
Zero indicates accepted authentication. Reject nonzero authentication results and
unsupported-command responses. Do not infer command success from a UDP ACK alone.

An outdated saved key produced result `1` on the test camera after a power cycle
and move. The already paired vendor app held a different key that authenticated
successfully to the same pinned identity. The native app stops retrying result
`1` and offers key import. This observation does not establish the refresh
protocol, its cloud dependencies, or that every reboot rotates keys.

## Importing an existing vendor pairing on Windows

`VendorClientImporter` supports the 32-bit PC build `1.0.1.1_202209261648` only,
guarded by the executable SHA-256:

```text
f6f9c422fa046f66d032a919e85444c195084c019efc959971a1213f46f32c46
```

The 64-bit native app opens the running client with `PROCESS_QUERY_INFORMATION`
and `PROCESS_VM_READ`. It scans committed, writable private memory in bounded
chunks for the camera object's vtable pointer at main-module RVA `0xA46758`.
It does not inject, hook, or write to the vendor process. No memory dump is saved.

The verified object layout is:

| Offset | Field |
| --- | --- |
| `0x00` | 32-bit vtable pointer |
| `0x04` | MSVC `std::string` camera identity |
| `0xB0` | MSVC `std::string` device key (15 ASCII bytes) |
| `0x14C` | Signed session handle; a negative value is inactive |

For this string layout, length is at `+16` and capacity at `+20`. Capacity below
16 uses inline storage; larger capacity uses the pointer at `+0`. Check bounds,
terminators, and object consistency across two reads. The textual identity is
`prefix-serial-suffix`, with the serial decimal-padded to at least six digits.
Convert to the wire UID as two eight-byte zero-padded ASCII fields surrounding a
big-endian 32-bit serial. Match the saved UID when available; reject ambiguity.

An object with a nonnegative session handle is only an import candidate. The key
must pass an authenticated LAN firmware query against its UID before the DPAPI
profile is replaced. This also rejects stale heap objects. These offsets are not
assumed valid for other vendor builds. The optional Python importer uses Frida
with the same executable hash guard; normal native operation does not use it.

## Verified command formats

All listed integers are big-endian. A returned settings structure has light mode
at byte 91, tracking mode at byte 68 and image rotation at byte 54 for hardware 253. These offsets are not
claimed to apply to all camera models.

| Request / response | Payload / behavior |
| --- | --- |
| `1300 / 1301` | Firmware string; empty request |
| `0330 / 0331` | Device info; four zero bytes |
| `2345` | Start live video: use-count byte, resolution byte, `01 00` |
| `1311 / 1312` | Resolution32, use-count32; HD=1, SD=2, auto=0 |
| `1380 / 1381` | Light mode32: 0 infrared in darkness; 1 colour with extra lighting; 2 automatic lighting |
| `400B / 400C` | Tracking32: 0 off, 1 motion tracking; readback verified and physical tracking confirmed by owner |
| `131F / 1320` | Image rotation32: 0 normal, 1 rotated 180°; returns device info |
| `1396 / 1397` | Read gimbal restore parameter; request four zero bytes, response uint32 |
| `1394 / 1395` | Set gimbal restore parameter32: mobile app sends 20 for on, 0 for off; reference firmware returns device info; verify with `1396` |
| `4012` | Direction32, speed32=0; 1 up, 2 down, 3 left, 4 right |
| `4013` | Stop movement; four zero bytes |

PTZ calls are short pulses with a stop in a `finally` block. An unreliable network
can still delay a stop command; the app does not implement indefinite press-to-move.

### Orientation and gimbal switches

The mobile SDK's `setReverse` maps to `131F`, with a four-byte switch value.
On the reference hardware, the native UI's 0 → 1 transition changed device-info
byte 54 and visibly rotated the live image through 180°. Returning to 0 restored
the image. The camera's date/time overlay remained upright. This is a camera
stream setting, not a transform applied only to OpenYI's preview.

The mobile `getPtzResetLength` / `setPtzResetLength` methods map to `1396` and
`1394`. The settings screen reads nonzero as checked and writes **20**, not 1,
for checked; unchecked writes zero. The reference camera's initial read was 20,
matching the owner's default-on observation. OpenYI reads the current value and
does not write defaults on connection. The native UI's off write read back as 0;
on read back as 20. The setter returned **344 bytes of device info**, rather than
the single integer interpreted by the mobile callback. OpenYI therefore verifies
the write with the separate getter and never treats the settings header as an
echoed value. The original on setting was restored. The exact timing, return target and
relationship to tracking remain experimental; the name alone does not establish
startup calibration or a factory reset.

The mobile left/right and up/down switches are **app-local preferences**, keyed
per camera. `PTZControlFragment` swaps the direction codes before sending PTZ.
There is no motor-axis enable/disable command in this switch path. OpenYI stores
independent direction-reversal booleans in the camera's encrypted profile: pan
swaps 3 ↔ 4, tilt swaps 1 ↔ 2, and stop is unchanged. Existing profiles default
to unchanged arrows. A pairing-key refresh for the same pinned identity retains
these preferences. These switches do not change image rotation or other apps.

The distinct SDK `getReverse2`/`setReverse2` and `getReverse3`/`setReverse3`
commands were not substituted for these app-local controls.

The generic `1321 / 1322` day/night command accepted values and changed readback,
but did not force physical IR operation on this unit. `2352 / 2353` did not change
the measured native resolution. Neither is exposed as a verified control.

## Video and recording

Each video message starts with a 24-byte frame header:

| Offset | Field |
| --- | --- |
| 0 | codec16 (`004E` = H.264) |
| 2–5 | flags, live flag, viewer count, use count |
| 6 | shared frame sequence16 |
| 8, 10 | width16, height16 |
| 12 | wall-clock seconds32 |
| 16–19 | day flag, reference, loss counters |
| 20 | timestamp_ms32 |

I-frame payload bytes 4 through 35 are encrypted in two AES-ECB blocks. The AES
key is the 15-byte device key with ASCII `0` appended. The rest of the H.264 frame
is unchanged. P frames are sent without that encryption.

The I and P channels arrive independently; merge by shared frame sequence before
decoding or recording. Wait for an I frame at startup, and discard dependent P
frames after an unrecoverable gap until a fresh I frame arrives.

On this firmware, `timestamp_ms` is a wrapping millisecond uptime, **not** a
0–999 fractional second. Adding it to the seconds field double-counts elapsed
time. The app uses its deltas (with wrap handling) for MP4 timestamps. Other
firmware's fractional millisecond format is handled separately.

MP4 clips start with SPS/PPS and an IDR. Fragment at keyframes; rotate on target
duration, new stream generation, or changed parameter sets. Preserve the encoded
stream. Explicit 4K export is a separate re-encoding operation.

## Research sources and scope

Transport framing was cross-checked with the public
[Yihaw research repository](https://github.com/lr-m/Yihaw). Command structures and
authentication were independently inspected in the owner's installed clients;
those vendor files and research downloads stay in ignored private development
storage. Exploit functionality from external research is not used by this app.

## Experimental setup QR

The inspected mobile `GenerateAndScanBarcodeActivity` composes fresh Wi-Fi setup
as `b=<binding-token>&s=<ssid-base64>&p=<password-base64>`. The binding token is
the result of an account-specific vendor service request. No evidence currently
establishes acceptance of a locally invented token or account-free first pairing.

`s` is standard padded Base64 of UTF-8 SSID bytes. For `p`, XOR each UTF-16 password
character with the corresponding character of this repeating 70-character mask:

```text
89JFSjo8HUbhou5776NJOMp9i90ghg7Y78G78t68899y79HY7g7y87y9ED45Ew30O0jkkl
```

If XOR produces NUL, retain the original character. Encode the resulting string
as UTF-8 and standard padded Base64. Fields are concatenated directly, without
URL escaping. This is reversible obfuscation, not encryption.

The mobile Wi-Fi-change path instead composes
`t=1&s=<ssid-base64>&p=<password-base64>&d=<display-device-id>`. This device ID is
distinct from the protocol's 20-byte wire UID. A separate cellular/APN variant
adds `a` and `t` fields and is not implemented.

OpenYI generates PNG files locally with the open source QRCoder library. It does
not contact a service, invent binding tokens, reset the camera, or assert that QR
generation completes pairing. Composition is covered by synthetic fixtures;
camera acceptance, token expiry and account-free provisioning remain unverified.
The existing working camera was deliberately not reset. Saved QR images contain
recoverable network credentials and are never added to the source repository.

### Binding token evidence

The inspected mobile `g2.d.q0` requests `/v2/qrcode/get_bindkey` with `seq=1`,
`userid`, `timestamp`, optional `webauthflow`, and an account-authenticated `hmac`.
This path does **not** submit the new camera's UID, its SSID, or its Wi-Fi password.
The returned `data.bindkey` is copied unchanged into the QR. The request's HMAC
does not establish that the returned token itself contains a signature. Another
client path uses `/v5/qrcode/get_bindkey` with a `did`; do not generalize the v2
request to every device family or setup mode.

The mobile app polls `/v2/qrcode/check_bindkey` with the token, timestamp and
request authentication. The handler recognizes `ret=-3` as token timeout,
`-2` as not found, `-1` as server error, `0` as pending and `1` as success with
`uid`. Pending responses provide `check_after` (bounded to 1–20 seconds, otherwise
3). The UI's separate 120-second waiting limit is **not a measured token TTL**.
These observations establish server-side token lookup and account association,
but not one-time use, reuse rules, token entropy or the actual expiry period.

Static analysis also examined an **older related firmware**,
`6.0.05.10_202301061607`, from the
[Anyka research firmware dump](https://github.com/VGerris/Anyka_ak3918_hacking_journey).
It is not the baseline camera's `6.0.24.10_202401091113`. Nothing from this image
was executed or flashed, and its binaries are not distributed with OpenYI.
The examined `anyka_ipc` SHA-256 is
`f2f977c41214d549022fcc987d5e55758ce8231d55fa0a4ab707c610d38de6e6`.

* `judge_bindkey` (`0x2917c`, 1368 bytes) reads the first two characters to select
  region, language and timezone. Recognized prefixes include `EU`, `US`, `CN`
  and `YI`. This function does not authenticate the token's remaining characters;
  it does not establish that no other firmware function performs further checks.
* `webapi_do_bindkey` (`0x52b50`) invokes the device's cloud helper against
  `/v5/ipc/qr_bind`, then parses the JSON response. Code `20000` returns success.
  The helper formats `uid`, `bindkey`, `timestamp`, `seq=9` and an HMAC. A bundled
  library also contains a `/v4/ipc/qr_bind` path; that string alone does not show
  which path is active.
* `keepalive_thread` calls `webapi_do_bindkey` at `0x56894`. Only its success
  return (`1`) branches to playing `/tmp/audio/success.aac` at `0x56900` and
  setting the Wi-Fi configuration state to successful. Failure clears that state
  and calls `manage_bind_failed`.

Thus this related firmware demonstrates a camera-run flow that waits for a
cloud binding result before playing its success prompt. A spoken success prompt
does not by itself demonstrate offline signature validation. Whether the current
camera also permits authenticated LAN access after Wi-Fi association but before
cloud binding remains unknown.

| Proposed interpretation | Current conclusion |
| --- | --- |
| Opaque one-time nonce | Opaque to the inspected app, with a meaningful region prefix in related firmware; one-time use is unverified. |
| Derived from camera/account data | Issued in an account-authenticated request. No new camera ID is supplied in the inspected v2 issuance path; internal server derivation is unknown. |
| Vendor-signed token | Not established. HTTP request authentication is not proof of a signature inside the token. |
| Validated only by phone/app or cloud | The phone polls cloud state. Related firmware independently calls the cloud binding endpoint, so phone-only validation is not supported by that evidence. |
| Verified by camera firmware | QR parsing and a cloud-result check are demonstrated in related firmware. Offline cryptographic token verification on the current camera is not established. |

### Controlled token experiments

The owner reports resetting the camera, scanning a newly issued mobile-app QR,
and hearing “pairing succeeded.” This verifies the normal vendor QR path after
reset by user observation, not any modified-token case or account-free setup.
Opening its already paired entry in the phone app also succeeded without new
setup; this does not establish reset-free acceptance of a new QR. A pending firmware
update advertised `20260806-eu / Minor bug fix`; compatibility and rollback have
not been established, so the current firmware remains the reference baseline.
After this report, an authenticated read-only LAN query using the saved native
profile still succeeded and returned the unchanged baseline firmware, hardware
253, night mode 0 and tracking 0. No pairing-key replacement was needed for that
check; this does not establish that keys survive every reset.

Optional Python tooling decodes QR images without URL form decoding (Base64 `+`
must stay `+`) and prepares bounded variations offline:

```powershell
python -m pip install -r requirements-qr.txt
python tools/qr_research.py inspect .local/original-setup.png
python tools/qr_research.py cases .local/original-setup.png --output .local/qr-cases
```

Inspection reports field names and lengths; only explicit `--reveal` prints
credentials. Generated PNGs contain recoverable credentials. The tool never
contacts a camera or service, and refuses to overwrite an existing case folder.

| Case | What must be distinguished | Physical result |
| --- | --- | --- |
| Previously issued token | Same unchanged QR as the reference; record issuance time and whether it was used before. | Not run |
| Altered token | Change one tail character, retaining length and the two-character region prefix. | Not run |
| Expired token | Use an old genuine QR; call it expired only with evidence of expiry, not merely the phone's waiting timeout. | Not run |
| Missing token | Omitted `b` and empty `b=` are separate parser cases. | Not run |
| Locally random token | Randomize the tail but retain the region prefix and length to avoid changing server routing. | Not run |

For each case record QR recognition, Wi-Fi/DHCP association, exact spoken prompt,
authenticated local command/stream availability, and any cloud binding result
separately. Otherwise a Wi-Fi success can be mistaken for account binding, or a
region error for a token signature rejection. Do not reset the sole working
camera just to fill this matrix without an agreed recovery procedure.

## Local motion events

See [the motion investigation](docs/MOTION.md) for the verified alert-history
wire layout, negative observations, related-firmware event coalescing and the
remaining physical tests. Alert history is not qualified as continuous motion
state; tracking readback must not be substituted for detection.

## Microphone stream

See [audio transport, codec and timing](docs/AUDIO.md) for the physically captured
AAC format, channel/header layout, complete-block decryption, common-clock muxing
and remaining synchronization tests. The inspected mobile paths are
`TnpCamera.sendStartListeningCommand`, `ThreadRecvAudio`, `AVFrame` and
`AntsUtil.decryptAudioFrame`; codec IDs from the separate legacy `TNP_Proto`
class do not describe the observed stream.

## Talk-back speaker stream

The optional local motion alarm reuses this same speaker transport and mode-0
initialization, sending repeated AAC-LC 16 kHz mono packets at 64 ms intervals.
Its second threshold, entry/exit grace, Home/Away and duration are PC application
state, not camera firmware commands. The vendor tamper alarm is not enabled.
Computer alarm playback uses Windows audio output, with no camera command at all.
Camera alarm playback retains the same half-duplex microphone limitation. Signal
normalization is implemented; a camera hardware-volume override remains
unverified. See [alarm behavior and validation](docs/ALARM.md).

The same hardware/firmware accepts AAC-LC 16 kHz mono ADTS on outgoing reliable
channel 1, TNP v2 kind 2. The speaker must first be initialized in that session:
start video (`2345`), receive a frame, stop video (`02ff`, eight zero bytes), then
start half-duplex speaking (`0350`, four zero bytes). Stop speaking is `0351`
with eight zero bytes. Brief video initialization is essential on the tested unit:
authenticated speaker start and acknowledged audio alone produced silence.

Use the normal 24-byte frame header with codec 138, flags 2, and a nonzero uint32
at offset 12. The inspected legacy mobile sender increments it by 20 per packet;
other frame fields remain zero. AAC contains 1024 samples per packet, so send at
64 ms intervals, not at that counter's nominal rate. Encrypt all complete 16-byte
ADTS payload blocks with the device key plus ASCII `0`; leave the partial tail
clear. This counter is separate from camera recording timestamps.

Two-tone playback was physically confirmed after video initialization, and again
after stopping the bootstrap video before sending audio. No volume change was
required. SDK speaker-volume getter `1438` (one zero byte, response `1439`) timed
out on this camera; device-info byte 17 was zero but is not qualified as a volume
readback for this hardware. SDK setter `1333`/`1334` was **not tested or exposed**.
PC microphone speech through the native Windows sender was also physically
confirmed on 2026-10-10 after the owner unmuted the PC input. The original cause
of that mute is unknown; no camera speaker-volume change was needed.
See [audio documentation](docs/AUDIO.md#talk-to-camera) for provenance and limits.
