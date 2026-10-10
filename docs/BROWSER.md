# Recording browser and player

The Windows **Recordings** tab uses the existing SQLite catalogue. It shows
thumbnails, local recording date/time, camera name, playback duration, captured
duration, resolution, profile/type, size and status. Older catalogue entries
without camera/profile metadata remain readable and are labeled unspecified.
The list is paged in groups of 100; thumbnails are decoded on demand for visible
rows and held in memory rather than generating another set of image files.

Date/time filters select overlapping capture intervals. This matters for
timelapse, where four seconds of playback may represent several minutes of
surveillance. Additional filters select a camera, protected/unprotected clips,
and continuous, low-rate, timelapse or motion type. The motion filter is ready
for tagged recordings; it does not imply a qualified motion trigger exists.

Select a completed recording and use Play/Pause, Stop or the seek bar. The
embedded player uses independent FFmpeg video/audio pipes with the same seek origin.
For sound, it maps camera timestamp gaps to silence and presents video against
the Windows audio device's sample position. The 25 fps display conversion may
repeat pictures from a low-rate clip; it does not change the saved recording.
Without sound, playback uses a monotonic clock. Seeking restarts decoding at
the chosen position. Leaving the tab pauses playback. Monitoring live camera
sound is stopped when recorded playback starts, to avoid mixing both sources.

Playback, thumbnail generation and export acquire temporary SQLite reservations.
The native and Python recycling paths honor them, and deletion checks them
inside its existing transaction. This does not change the user's protected flag.
Playback/export renew their two-minute reservations while active; a crashed
reader's reservation expires. Pausing closes the reader and releases its hold.
Protect/unprotect remains persistent. Original export copies the MP4; 4K export
re-encodes video and preserves an optional AAC track. Exports never overwrite an
existing file, and deletion still requires the app's explicit confirmation.

Tests cover lease/recycling exclusion, expiry compatibility with the Python
catalogue, and captured-time filtering. Windows UI checks cover thumbnail and
metadata display, playback advancement on a real AAC/H.264 clip, pausing,
seeking from about 39 to 65 seconds, resumed playback, and release of its
catalogue reservation. Protect/unprotect and the protected filter were also
checked in the UI. A synthetic 0.5 fps red/blue clip verified that seeking into
the middle of a picture's two-second hold shows the correct picture rather
than the next one. Auditory/lip-sync judgment still requires
physical confirmation; numerical timestamps and a running audio device are not
by themselves an end-to-end latency measurement.

The initial single-process player could deadlock before its first picture on
encoded recordings with audio: one output filled before the other could advance
the playback clock. This was reproduced on a 5 fps CRF-28/AAC recording that
played successfully in an external player. Independent decoder pipes remove
that circular wait while retaining bounded buffers and the shared audio clock.
`scripts/check_player_integration.py` exercises the actual Windows player at
source/5/1/0.5 fps, with audio, seeking and silent playback. A paced silent audio
sink tests the pipes without requiring a speaker device or playing fixture tones.
The formerly stalled physical-camera 5 fps CRF-28/AAC clip subsequently advanced
past two minutes in the Windows player; the owner also confirmed correct playback.

The playback clock uses Microsoft's documented
[waveOutGetPosition sample-time query](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutgetposition).
Audio timing conversion uses FFmpeg's
[aresample filter](https://ffmpeg.org/ffmpeg-filters.html#aresample).
