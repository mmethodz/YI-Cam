# Recording profiles and verification

Original recording remains the default native C# fragmented MP4 path. It does
not require FFmpeg. Optional profiles feed those timestamped fragments to a
separate FFmpeg process and encode with `libx264`, `veryfast`, CRF 28 or 32,
without B frames. Reduced-rate profiles select one source picture per time
bucket without duplicating pictures to raise the rate. Live preview consumes
the original frame stream independently.

The input queue is bounded to 360 video/audio packets and 16 MiB. An encoder failure or
overflow stops recording with an error. The catalogue marks a clip complete
only after the encoder finishes and its fragmented MP4 sample timing is read
successfully. Finalization has a 15-second limit. Segment finalization runs off
the incoming-frame path; original recording continues using the existing muxer.

Low-rate mode retains selected source timestamps. Its last picture has a hold
duration of `1 / selected_fps`, so a clip's playback duration can exceed capture
duration by up to that interval. Timelapse deliberately maps selected frames to
25 fps. `clip_details` stores the profile, camera name, recording kind, target
fps, source capture duration and output frame count separately from the legacy
`clips` table; Python's existing catalogue reader remains compatible.

## Same-source camera measurement, 2026-10-09

One private 30.097-second sample from the test camera (1280 × 720, 456 source
frames, about 15.2 fps) was replayed through each native recording profile with
its captured camera timestamps. No footage, pairing data or QR credentials are
published. These are **short-sample extrapolations**, not hour-long measurements
or guarantees for other scenes/cameras. Each output was decoded independently
with PyAV and checked for frame count, corruption and resolution.

| Profile | Capture fps | Saved frames | Bytes | MB/hour of capture | Playback seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Original stream | Source | 456 | 1,829,879 | 218.88 | 30.097 |
| Balanced, CRF 28 | Source | 456 | 853,871 | 102.13 | 30.080 |
| Balanced, CRF 28 | 5 | 151 | 1,184,935 | 141.73 | 30.230 |
| Smaller, CRF 32 | 1 | 31 | 772,251 | 92.37 | 31.030 |
| Smaller, CRF 32 | 0.5 | 16 | 566,326 | 67.74 | 32.030 |
| Smaller, CRF 32, timelapse | 0.5 | 16 | 36,623 | 4.38 | 0.640 |

MB means 1,000,000 bytes. The owner's longer original recordings were about
300 MB/hour (7.2 GB/day per camera). Do not substitute this short sample's rate
for a long-term budget. The 5 fps result also demonstrates that lowering fps is
not a monotonic size control at constant CRF. Timelapse changes playback timing
and encoder keyframe decisions; it is a distinct capture mode.

## Reproducing the checks

`scripts/check_recording_profiles.py` creates a synthetic pattern, exercises
native rotation and every representative profile, and independently verifies
timestamps, complete frame counts and catalogue durations with PyAV. It also
checks that a snapshot matches the last full-resolution source picture.

`scripts/measure_recording_profiles.py` accepts a private Annex-B capture with
an AUD before each frame, an ordered JSON array of camera millisecond timestamps,
and a new private output directory. It writes actual byte counts and extrapolated
rates to `measurements.json`. Optional development dependency: `imageio-ffmpeg`.
Neither script transmits footage or requires the vendor runtime.

The Windows UI was also exercised against the physical camera: original
recording, balanced encoding at 0.5 fps, and a snapshot saved through the normal
Save dialog. The low-rate clip independently decoded to 85 pictures over 168.104
seconds of capture (170.037 seconds of playback including the final hold), while
the UI continued reporting about 15.1 source fps. The PNG was 1280 × 720. The
original-stream default was restored afterward. This qualifies one camera and
one PC, not sustained multi-camera CPU capacity.

FFmpeg's [select and setpts filters](https://ffmpeg.org/ffmpeg-filters.html) control
frame selection and timelapse timing. Its
[setts bitstream filter](https://ffmpeg.org/ffmpeg-bitstream-filters.html#setts)
sets packet duration. Final catalogue duration is read from the saved MP4,
because progress output can precede that packet-duration adjustment.
