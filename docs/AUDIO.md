# Camera audio and intercom

Audio is optional and off by default. **Listen to camera** monitors the camera's
microphone through the PC speakers. **Capture options → Include camera microphone
audio** writes an AAC track with original or re-encoded real-time video. These
controls are independent. Timelapse intentionally disables audio because its
playback time differs from real time. Original video plus AAC is a native remux;
FFmpeg is only needed for monitoring or a video encoding profile.

## Verified transport and codec

On hardware 253, firmware `6.0.24.10_202401091113`:

* `0x0300` starts listening; `0x0301` stops it. Each carries eight zero bytes.
  Neither has a required command response in the inspected mobile implementation.
* Reliable channel 1 carries TNP kind 2 messages. Each has the familiar 24-byte
  frame header: big-endian codec uint16 at 0, flags at 2, sequence uint16 at 6,
  seconds uint32 at 12, and milliseconds uint32 at 20.
* Every complete 16-byte audio payload block is AES-ECB decrypted using the same
  16-byte IPC key as video; trailing partial bytes remain clear. This differs
  from the partial I-frame decryption used for video.
* A local 12-second capture returned 150 packets, each 263 bytes, codec **138**
  (`0x8a`), flags **27**. Every decrypted packet contained one valid ADTS AAC-LC
  frame. Independent PyAV decoding yielded **16,000 Hz mono**, 1024 samples per
  packet, 153,600 samples / 9.6 seconds in total. Audio began a few seconds after
  the start command. Header flags do not correctly describe this stream's rate
  and channel count; OpenYI reads the ADTS configuration instead.
* Of 149 adjacent timestamp deltas, 146 were 64 ms and three were 74 ms. Audio
  and video use the same camera uptime-millisecond clock on this unit. Seconds
  plus the entire uptime field must not be added together as a timestamp.

The receive-stream investigation did not change microphone gain, speaker volume,
detector or cloud settings. The separate speaker investigation is documented below.
This supports the observed AAC-LC stream; it is not a claim that every YI IoT
model has the same codec or clock. Unsupported AAC profiles, malformed ADTS and
codec changes do not silently become nominally synchronized recordings.

## Recording timing and failure behavior

The native MP4 writer uses a video track at 1000 ticks/second and an audio track
at its actual sample rate. It strips ADTS headers but copies AAC access units
unchanged. Each audio fragment starts at its camera timestamp relative to the
clip's first video keyframe, with 1024 samples of duration. Delayed starts and
timestamp gaps are retained. Audio does not advance the video clock.

With audio enabled, a new recording waits for a recognized audio configuration
and a video keyframe. A sequence gap, audio timestamp reversal or codec change
closes the current clip. Reconnects reset the audio configuration. Video-only
recording retains its previous behavior. Low-rate profiles still copy every AAC
packet; FFmpeg's delayed MP4 header preserves nonzero starting audio DTS. This
detail is covered by an independent decode test, because an immediate empty
header incorrectly shifted delayed audio to zero in the initial implementation.

The speaker monitor has bounded packet/PCM queues and is intended for listening,
not precision lip-sync. Video preview and audio playback use separate decoders;
their live buffering latency has not been calibrated. Saved A/V timestamps are
independent of speaker latency. A physical clap/lip-sync test and a long-duration
drift test remain to be done; correct numerical timestamps alone do not prove
sensor capture latency is zero.

`scripts/check_audio_integration.py` generates synthetic H.264 and AAC, introduces
a 250 ms audio delay and 10 ms camera-clock gaps, rotates clips, and independently
decodes both native original and encoded 1 fps recordings. It verifies unchanged
AAC payloads, timestamps, sample rate, mono layout, video counts and two segments.
Existing video-only integration/profile tests continue to pass.

A native Windows UI recording from the physical camera was then independently
decoded: **87.650 seconds**, **1326 H.264 pictures** at 1280×720, and **1358 AAC
frames / 1,390,592 samples** at 16 kHz mono. Video timestamps ran from 0 to
87.583 seconds; audio from 0.363 to 87.639 seconds. Both were strictly increasing,
the MP4 decoded without errors, and the SQLite clip finalized successfully.
The live preview remained about 15 source fps. Speaker monitoring was exercised
without a reported application error; audible output still requires owner
confirmation. This is a short local capture, not a long-term drift qualification.

The Python reference exposes `start_audio()`, `stop_audio()` and `audio_frames`;
the Windows app uses the native C# implementation without Python or vendor DLLs.

## Talk to camera

Select a **PC microphone** in the live view, then click **Talk to camera**. The
button changes to **Stop talking** and a red **Mic live** indicator appears once
audio packets are being sent. Click again or press Escape to stop. Switching tabs,
switching away from OpenYI, disconnecting or closing also releases the microphone.
It never starts on launch or reconnect. Listening is paused while talking and
resumes afterward if it was selected. This is half-duplex intercom behavior, without
desktop acoustic echo cancellation. Windows must allow microphone access to desktop
apps; unavailable devices and capture failures are reported in the status bar.

The selected input name is stored with app preferences. A missing selected device
requires choosing an available input; it does not silently switch microphones.
The Windows default option follows the system default. WinMM can expose identical
names for different devices, so distinguishing those devices remains a limitation.

WinMM captures signed 16-bit PCM at 16 kHz mono. FFmpeg's open-source AAC-LC encoder
produces one ADTS access unit per 1024 samples at 32 kbit/s. Data stays in bounded
memory/pipes; PC microphone audio is not added to the recording catalogue. Speech
uses a separate short-lived authenticated LAN session so its transport failure
does not tear down the recording session. It is paced at 64 ms per packet and
stops on capture stalls or excessive backlog instead of building a delayed queue.
No proprietary encoder or vendor runtime is loaded. This UI currently targets
the primary camera; additional-camera talk controls remain future work.

### Observed speaker initialization

On hardware 253, firmware `6.0.24.10_202401091113`:

1. Connect and authenticate with the existing device key.
2. Send video start `0x2345` with the usual generation, selected quality, `1,0`.
   Receive one video frame before continuing.
3. Send video stop `0x02ff` with eight zero bytes and await transport ACKs.
   This avoids a second ongoing video stream during speech.
4. Send speaker start `0x0350` with uint32 zero (the mobile half-duplex mode).
5. Send encrypted AAC on reliable channel 1; acknowledge/retransmit independently
   of commands on channel 0.
6. Release the PC microphone, discard unsent retransmissions, send speaker stop
   `0x0351` with eight zero bytes, then close the talk session.

The audio message is TNP v2 kind 2, followed by a 24-byte header and an entire
ADTS packet. Codec uint16 at offset 0 is **138**, flags at offset 2 are **2**,
and offset 12 contains a nonzero big-endian uint32 counter advancing by 20 per
packet. Sequence and millisecond fields are zero in this observed mobile path.
The counter does not govern transmission speed or A/V recording timestamps.
All full 16-byte payload blocks use AES-ECB with `device_key + "0"`; the final
partial block is clear. Packets larger than 1024 bytes or codecs other than the
verified AAC-LC mono 16 kHz format are rejected.

The mobile references are `TnpCamera.ThreadRecordAudioAAC`, `ThreadSendAudio`,
`startSpeaking`, `stopSpeaking`, `sendStopPlayVideoCommand`, `TNPFrameHead`, and
`SFrameInfo.createAudioTimestamp`. Related **older** firmware's `yi_p2p_recv_audio_data`
uses per-session stream state to decide whether to decrypt speaker data. That
static observation suggested the bootstrap fix; it is not a claim that the
current firmware has identical internals.

### Verification and remaining limits

Two initial Python tone probes were acknowledged but silent. After adding video
initialization, the owner heard both low-level tones. A further test stopped the
bootstrap video first and both tones were again heard. This physically verifies
the speaker/codec/encryption sequence, beyond transport ACKs. Speaker volume was
not changed. The mobile SDK exposes a volume getter/setter, but the getter timed
out on this camera and its generic device-info field is not a validated level.

Synthetic native checks cover header/encryption, exact retransmissions after
wrong-channel ACKs, independent camera keys and speaker initialization order.
`scripts/check_talk_integration.py` exercises the actual native FFmpeg pipe with
paced synthetic PCM, independently decodes 24 AAC frames / 24,576 mono samples,
and checks cancellation and capture/startup failure cleanup. It never opens a real
microphone. These checks complement, rather than replace, physical speech tests.
The native Windows microphone path was also exercised against the real camera:
the UI reached **Mic live**, Escape released the input and encoder, and the live
preview continued near 15 fps. A simultaneous 77.62-second balanced 5 fps recording
finalized and independently decoded 388 video frames and 1201 AAC frames with
strictly increasing timestamps. Audible speech from the selected PC input remains
an owner check; the successful tone confirmations above used the Python sender.
Long intercom sessions, all microphone drivers, simultaneous hardware cameras and
the effect of half-duplex speaker mode on camera microphone gain/muting remain
unqualified. The recorder continues using the received camera AAC and timestamps;
it does not synthesize or record the outgoing PC microphone track.
