# Local motion recording

In **Capture options**, choose **Motion · local detection on this PC**, set the
changed image area threshold and seconds to keep recording after the last motion,
then save. **Arm motion recording** starts watching; **Disarm motion recording**
finishes the current clip and stops watching. Arming does not immediately create
a recording. The live view distinguishes watching from an active motion window.
Keep OpenYI open and the camera connected. The normal keep-awake behavior applies
while armed. Closing the app or disconnecting it stops the recording session.

Detection runs locally through the selected FFmpeg executable. The camera's own
motion detector, cloud alerts, notification frequency and siren are not changed.
Original-stream recording remains lossless; the optional H.264 profiles and their
capture rates also work with motion mode. Even a 0.5 fps recording is analyzed
from the incoming source stream, independently of preview and recording rate.
Motion is a separate mode from continuous/low-rate and accelerated timelapse.

## Trigger and buffering

Frames are decoded to 160 × 90 grayscale, analyzed at up to five samples per
second (limited by source fps). A median brightness offset discounts global
exposure changes; differences below 20 gray levels are ignored. Two consecutive
samples must reach the changed-area percentage (default 2%). A **lower** area
threshold is more sensitive. This is pixel motion, not person/object recognition.
Camera movement, tracking and nonuniform lighting changes can still trigger it.

Every active sample restarts the post-motion timer (1–3600 seconds, default 30).
The timer uses analyzed camera timestamps. Clips retain the normal catalogue,
rotation, protection, export and storage policy. The Motion browser filter selects
these recordings, including those encoded at reduced fps. Estimates show storage
per hour of recorded footage; daily usage depends on activity and the chosen tail.

One complete compressed GOP is retained for a short lead-in: at most 5 seconds,
180 pictures and 8 MiB. Its length depends on the most recent keyframe; this is
not a guaranteed five-second pre-roll. AAC is buffered separately (at most eight
seconds, 128 packets and 1 MiB) and replayed with the same camera clock. Stream
changes and reconnects clear the detector baseline, timer and buffers. If no
complete GOP is available, recording waits for the next keyframe.

Decoder input/results are bounded. Overload, missing output or more than five
seconds of analysis lag disarms recording with an explicit error. No unbounded
queue or silent fallback to continuous capture is used. Analysis requires a
separate decode process per armed camera; multi-camera CPU capacity remains
unqualified. Frame-to-timestamp alignment assumes the reference camera's
no-B-frame, one-picture-per-video-message stream.

## Verification

Focused native checks cover exposure/noise rejection, threshold changes, the
analysis-rate limit, repeated motion extending the timer, expiration and clock
reset. `scripts/check_motion_integration.py` generates a private-free synthetic
stream with two motion bursts and quiet gaps. Original and balanced 0.5 fps
profiles each produce two complete Motion clips; independent PyAV decoding checks
the GOP lead-in, intact AAC packets and their timestamps. These automated checks
do not by themselves establish detection sensitivity in every real scene.

On 2026-10-10, the owner walked through the reference camera's view with local
detection armed (2% threshold, ten-second tail, balanced 5 fps with AAC). The UI
showed active recording/countdown and then returned to watching at 0% change.
Two completed 1280 × 720 Motion clips independently decoded to 85 video / 265
AAC frames over 17.026 seconds of capture, and 58 video / 181 AAC frames over
11.617 seconds. Live preview still reported about 15 source fps. Camera tracking
was enabled and also moved the view, so this confirms the combined real scene,
not isolated person detection or a precise laboratory measurement of latency.
Private test footage remains outside the repository.

## Camera-side investigation

Tracking and motion detection are different features. Tracking control/readback
works on the reference camera, and the owner confirms physical local tracking
also works. This control does not provide a motion-active event.

The reference hardware-253 camera answers `0x5c06` / `0x5c07` alert history.
The request contains three big-endian uint32 values: zero, start seconds and end
seconds (use the camera's seconds field, not an assumed correct wall clock).
The response contains a big-endian count followed by 12-byte records:
category, start seconds and duration seconds. OpenYI exposes this as a read-only
`CameraClient.AlertHistoryAsync` method with bounded, strict parsing.

A 90-second observation returned 39 empty histories, no unsolicited `0x1fff`
alarm messages, and no change in device-info byte 93. Sensitivity byte 11 was
zero and tracking byte 68 was zero. The owner's motion-alert configuration and a
deliberate walk-through were not confirmed during this capture, so this does
**not** establish that local events never work. No detector setting was changed.

The mobile SDK's other motion queries, `0x1327` and `0x4114`, timed out on the
reference camera. In the older related firmware described in PROTOCOL.md,
`yi_p2p_on_get_motion_detect_cfg` at `0x395f4` is only a logging stub.
`yi_p2p_on_alert_events` at `0x390d0` reads a local event list, persisted in
`/mnt/alarm_event.db`. Its motion callback (`motion_detect_func`, `0x32274`)
can append category-1 records with a fixed six-second duration, throttled to
roughly one minute. This is static evidence from **different firmware**, not a
measured cadence on the reference camera. The detector's enable flags also
matter. A cloud upload path exists separately; a local history reply alone does
not show that event generation is independent of all cloud configuration.

This history is not yet a qualified signal for recording until the last movement
plus a post-motion interval. Repeated movement could be hidden by coalescing.
The owner prefers PC-side detection for direct control of the trigger and timer.
OpenYI therefore uses the local detector above rather than these camera events.

The owner's mobile UI offers motion detection, Low/Medium/High detection
frequency and a 24/7 or custom schedule. Although the UI initially reported
tampering detection unsupported, the owner later confirmed that enabling it
produced a loud horn during movement. Audible/tampering alarms are deliberately
outside OpenYI's requested feature set; local recording does not enable them.
Static mobile-code inspection ties frequency to an app/cloud `push_interval`
setting. It is not established as pixel sensitivity or a refresh interval for
local events. The separate SDK sensitivity command is `0x1335` / `0x1336` with a
big-endian uint32 (0 high, 1 medium, 2 low); device-info byte 11 reports its value.
No sensitivity or detector-enable write was made during this investigation.
Queries `0x1327` and `0x4114` still timed out with tracking enabled. These findings
do not claim that the camera's firmware has no usable motion signal.

For a community test with motion alerts enabled and a deliberate walk-through:

```powershell
python tools/motion_probe.py "$env:LOCALAPPDATA/YI Local/device.dpapi" --seconds 90
```

The tool changes no detector/tracking settings and does not save images or keys.
Its output contains event times. Record the physical movement time separately,
then test sustained movement, another movement within one minute, and stopping.
Only a reliably refreshed signal can support a truthful post-motion timer.
