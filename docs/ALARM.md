# Optional local motion alarm

OpenYI remains a surveillance app with an optional alarm. There are no schedules,
accounts, authentication controls or remote HTTP panel in this feature.

## Use

1. In **Capture options**, select **Motion · local detection on this PC**. Choose
   the recording threshold and recording tail (30 seconds, 1 minute, 5 minutes,
   or a custom number of seconds), then **Arm motion** in Live camera.
2. In **Motion alarm**, enable the optional alarm and choose its separate
   changed-area threshold (default 10%, versus the recording default of 2%).
   A lower threshold is more sensitive. The live percentage helps tune it.
3. Choose sound duration: **30 seconds**, **1 minute**, **5 minutes**, **Custom**
   (1–3600 seconds), or **Until stopped**. New motion extends the recording tail,
   but does not extend this alarm duration.
4. Select **Camera speaker** (default), **Computer output**, or **Camera +
   computer**. For computer playback select a Windows output device. A saved
   device that is missing or has an ambiguous name causes an explicit error;
   it does not silently fall back to another speaker. “Windows default output”
   intentionally follows the current Windows default.
5. Choose **Away / arm alarm**. The exit delay defaults to one minute and accepts
   0–60 minutes. Motion during this delay is ignored. Once armed, two consecutive
   measurements at or above the alarm threshold start the entry countdown
   (default 15 seconds, configurable 0–300).

**Home / disarm alarm** cancels all pending countdowns and silences playback.
Recording continues. **Stop alarm** cancels the current incident but remains Away;
two continuously quiet seconds below the threshold are needed before a new
incident can trigger. Use Home if you want to stay in view without another alarm.
During the exit countdown, Stop returns to Home. Escape returns an active alarm
or entry countdown to Home. Talking also returns the alarm to Home to avoid
competing speaker sessions.

The bottom bar stays visible on every tab: green Home, orange entry/exit countdown,
and red Away/alarm states. The tray has the same indicator, plus Home and Stop
commands. Closing the window exits the app normally. Every launch starts in Home,
even if alarm configuration was enabled and saved. Configuration changes save
automatically in Home; Away is deliberately not a persisted setting.

This first implementation attaches the alarm to the **primary camera's** motion
recording session. Extra experimental grid cameras continue recording independently
but do not trigger an alarm. No multi-camera alarm qualification is claimed.
Disconnecting, stopping motion recording, resetting the decoder baseline, or
losing fresh measurements for five seconds returns the alarm to Home; reconnecting
does not silently rearm it. The PC and OpenYI must remain running.

## Sound and volume

The bundled eight-second siren is [Siren Noise by KevanGC](https://soundbible.com/1577-Siren-Noise.html),
published as Public Domain. See [asset provenance](../assets/alarm/LICENSE.md).
It is embedded in the executable; runtime use does not depend on the website.

WAV, MP3, AAC, M4A, FLAC, OGG and other FFmpeg-readable audio files can be imported.
Only the first 60 seconds of a file up to 100 MiB is used. FFmpeg normalizes the
sample and converts it to AAC-LC, mono 16 kHz, 32 kbit/s. Validated packet data is
bounded to 1 MiB/950 packets and retained in memory while armed. Imported sound is
copied under `%LOCALAPPDATA%\YI Local\alarm-sounds`; moving the original does not
break it. Settings are in the usual `settings.json`, in a separate Alarm section.
User audio is never copied to the repository or recording catalogue.

Camera output repeats prepared AAC through the existing authenticated local
half-duplex talk transport. No microphone input, vendor siren setting, account,
or cloud service is used. Camera playback can mute the camera microphone;
recordings made then may contain silence. Video and recording timing continue.
Computer output decodes the same prepared sample once and loops bounded PCM
buffers. Both outputs stop on cancellation, Home, disconnect or app shutdown.

The sound is normalized for loud playback (-9 LUFS target and -1 dBTP ceiling).
Computer output sets the open waveOut **instance** to 100%, then restores the
previous instance volume on stop if it is still unchanged. System master volume,
mute, driver policy and physical amplifier settings remain effective. This is
full application playback level, not a bypass of those controls. See Microsoft's
[waveOutSetVolume semantics](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutsetvolume).
The camera's separate hardware-volume getter was not supported by the reference
firmware; OpenYI does not send an unqualified volume setter or claim a verified
hardware-maximum override. The audio itself is sent without further attenuation.

**Test sound (3 s, loud)** tests the selected destinations without arming motion.
Home and Stop also stop a test. The two outputs may begin a little apart while
the camera initializes its speaker session.

## Implementation and verification

The existing 160 × 90 local detector supplies every analyzed changed-area sample,
up to five per second, to an independent two-sample alarm threshold. It does not
add a second video decoder or lower preview fps. An alarm is based on pixel
change; PTZ, tracking and lighting can trigger it as well as people.

Entry/exit and alarm timers use monotonic PC time. Sound duration starts when
playback begins, rather than when the event was detected. The camera sender also
enforces its duration outside the UI timer, with packets paced at 64 ms; computer
output uses an exact PCM sample count. Manual stop, stalled output, and sender
failure cannot leave an unbounded background audio queue.

Automated checks cover threshold separation, Home/Away, exit/entry cancellation,
fixed/latched duration, quiet rearm, persistence/legacy defaults, WAV/MP3 import,
the bundled sound, malformed AAC, PCM looping, packet pacing and cancellation.
Motion integration tests also check unthrottled sample/reset delivery while
retaining the existing original/Balanced recordings and AAC timing.
Silent output sinks are used for automated tests: they do not establish audible
alarm loudness, output routing on every Windows device, or scene sensitivity.
The underlying camera talk-back path was physically confirmed previously; this
new siren feature still requires listening verification on actual outputs.
