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
unacknowledged outgoing control packets with their original sequence number.

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
at byte 91 and tracking mode at byte 68 for hardware 253. These offsets are not
claimed to apply to all camera models.

| Request / response | Payload / behavior |
| --- | --- |
| `1300 / 1301` | Firmware string; empty request |
| `0330 / 0331` | Device info; four zero bytes |
| `2345` | Start live video: use-count byte, resolution byte, `01 00` |
| `1311 / 1312` | Resolution32, use-count32; HD=1, SD=2, auto=0 |
| `1380 / 1381` | Light mode32: 0 infrared in darkness; 1 colour with extra lighting; 2 automatic lighting |
| `400B / 400C` | Tracking32: 0 off, 1 motion tracking; verified by readback |
| `4012` | Direction32, speed32=0; 1 up, 2 down, 3 left, 4 right |
| `4013` | Stop movement; four zero bytes |

PTZ calls are short pulses with a stop in a `finally` block. An unreliable network
can still delay a stop command; the app does not implement indefinite press-to-move.

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
