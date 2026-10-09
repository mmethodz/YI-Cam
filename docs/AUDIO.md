# Camera microphone audio

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

No microphone gain, speaker, talk-back, detector or cloud setting was changed.
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
