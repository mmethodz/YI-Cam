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

Fresh QR provisioning remains unverified. The inspected setup flow generates
`b=…&s=…&p=…`, where `b` is obtained from the vendor binding service and the other
fields carry encoded Wi-Fi details. This does not establish that an arbitrary
locally generated binding token can replace the original onboarding flow.
