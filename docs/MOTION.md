# Local motion investigation

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
OpenYI therefore does not advertise motion-triggered recording or pre-roll yet,
and no PC-side vision is running. Continuous, low-rate and timelapse capture
remain independent choices.

For a community test with motion alerts enabled and a deliberate walk-through:

```powershell
python tools/motion_probe.py "$env:LOCALAPPDATA/YI Local/device.dpapi" --seconds 90
```

The tool changes no detector/tracking settings and does not save images or keys.
Its output contains event times. Record the physical movement time separately,
then test sustained movement, another movement within one minute, and stopping.
Only a reliably refreshed signal can support a truthful post-motion timer.
