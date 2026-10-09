# YI Local

An independent, open source LAN viewer and recorder for supported **YI IoT** cameras.
Keep your recordings on your own disk and use the camera's controls without a
vendor account login during normal operation.

**Native C# / WinForms is the Windows application. Python remains the protocol
prototype and portable reference.** The Windows application does not run Python,
BlueStacks, or the vendor client in the background.

This is an early release, initially verified with one Anyka-family camera:
hardware **253**, firmware **6.0.24.10_202401091113**. Other cameras using the
same app name may use different hardware and protocols. Fresh QR provisioning,
audio, and native 4K capture are not implemented.

## Windows application

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Run **Build Windows.cmd**, then **Start Windows.cmd**.
3. In **Camera setup**, import an existing `.dpapi` profile from the Python
   prototype under the same Windows account, or enter a known device pairing key.
4. Choose an [FFmpeg executable](https://ffmpeg.org/download.html) in **Storage**
   for live preview and 4K export. Original-stream recording itself is native C#
   and does not need FFmpeg. A sibling `ffmpeg.exe` is detected automatically.
5. Click **Connect camera**. If Windows requests network access, allow the app
   on the network containing your camera. The camera's discovery reply uses a
   different UDP source port, so an inbound LAN allowance can be necessary.

Build output: `dist/YI-Local/YiLocal.Windows.exe`. A standard build needs the
.NET 10 Desktop Runtime on the destination PC. To include the runtime:

```powershell
powershell -NoProfile -File scripts/build-windows.ps1 -SelfContained
```

No camera firmware changes or Internet port forwarding are required. The app
makes no cloud requests and exposes no HTTP server. The camera firmware may still
contact vendor services; this app does not change the camera's network settings.
Router-level Internet blocking has not been tested.

## Controls

| Control | Verified behavior on the test camera |
| --- | --- |
| HD | H.264, 1280 × 720; about 15.2 source frames/s in the measured stream |
| SD | H.264, 640 × 360 |
| Automatic quality | Camera-selected stream; dimensions may change |
| Pan/tilt | Short direction commands followed by stop; left/right physically checked |
| Motion tracking | Enable/disable commands and setting readback verified; following behavior not characterized |
| Infrared | IR activates automatically in darkness; red LEDs and filter click physically confirmed |
| Colour night vision | Extra visible lights activate; physically confirmed |
| Automatic lighting | Mode accepted and read back; trigger behavior not yet characterized |

Forced IR in a bright room is not established. The generic day/night command
accepted values but had no observed physical effect on this model; the app uses
the separate light-mode command that worked.

**Export 4K (upscaled)** creates a 3840 × 2160 software upscale. It does not add
sensor detail or establish native 4K support. Recording preserves the original
camera stream, without re-encoding. Measured frame rates are observations of this
stream, not a claimed hardware maximum.

## Local recording and storage

- Default location: your **Videos / YI Local** folder, outside the source repository.
- Video-only fragmented MP4, using camera timestamps for correct playback speed.
- Default policy: 10-minute clips, 20 GiB recording budget, 2 GiB of free disk space.
  Clip rotation waits for a keyframe, so the target length is approximate.
- Recycling starts **off**. Recording stops when a configured space limit is reached.
- With recycling enabled, the oldest completed, unprotected app-managed clips are
  deleted when needed. An optional maximum age can also trigger recycling.
- Only files registered in the folder's SQLite catalogue are managed. Unrelated
  videos, exports, active clips, and protected clips are excluded from recycling.
- The recording library can play, protect, export, and explicitly delete clips.
- One recorder may write to a library at a time. Python and C# share its catalogue
  format. Use separate folders for simultaneous independent sessions.
- Windows is kept awake during recording. Closing the app finishes the current
  clip. Connection loss finishes a clip and retries; an armed recording resumes
  after a new keyframe.

The C# muxer writes one fragment per picture; Python uses keyframe fragments.
Completed fragments can remain readable after a crash, but the last fragment may
be lost. Interrupted entries remain marked incomplete and are not automatically
recycled. Recovery/catalogue repair and long-duration unattended soak testing
remain future work. The muxer currently targets the verified H.264 stream without
B frames; other codec/reordering formats need explicit support.

## Pairing

The camera requires a **device pairing key**, not the user's account password.
Normal operation uses that saved key to authenticate directly to the LAN camera.
The camera identity is pinned after an authenticated connection.

WinForms saves the profile using current-user Windows DPAPI under
`%LOCALAPPDATA%\YI Local\device.dpapi`. The Python prototype uses
`.local/device.dpapi` on Windows; a source-tree native build can import that profile
automatically on first launch. DPAPI files are tied to the Windows account and
are not portable plain-text exports. Keys are never printed in application logs.

The Python prototype can optionally import a key once from an already paired,
running YI IOT PC live view:

```powershell
.venv\Scripts\python.exe -m pip install -r requirements-import.txt
```

Use **Camera setup → Import** in the Python app. The importer checks the executable
SHA-256 and only supports the tested 32-bit client `1.0.1.1_202209261648`. It reads
the existing local pairing and does not modify the installed vendor executable.
Close the vendor app after import, then import the encrypted profile into WinForms.

**Preserve an existing pairing.** Fresh QR setup is not implemented. The observed
vendor QR includes Wi-Fi information and a server-issued binding token; an
independent provisioning flow has not been established. Do not reset a working
camera merely to try this app.

## Python reference

Python 3.12+ with Tcl/Tk is needed for the prototype UI. On Windows, run **Setup.cmd**,
then **Start Recorder.cmd**. Its launcher hides the console of `python.exe`.

For Linux/macOS, use a virtual environment and install
`requirements-crossplatform.txt`, then run `python -m yicam.app`. Keys use the OS
keychain (Secret Service on Linux, Keychain on macOS), without a plain-text fallback.
Enter a known device key; Windows DPAPI profiles and the optional PC-client importer
are Windows-specific. Live hardware testing so far has been on Windows; Linux/macOS
desktop integration remains unverified.

```powershell
.venv\Scripts\python.exe -m yicam.cli status
.venv\Scripts\python.exe -m yicam.cli record --seconds 60 --quality hd
.venv\Scripts\python.exe -m yicam.cli night infrared
.venv\Scripts\python.exe -m yicam.cli tracking on
.venv\Scripts\python.exe -m yicam.cli move left
```

## Source layout and checks

| Path | Purpose |
| --- | --- |
| `src/YiLocal.Core` | Native LAN protocol, frame ordering, MP4 muxer, storage catalogue |
| `src/YiLocal.Windows` | WinForms UI, preview and export adapters |
| `yicam` | Python protocol prototype, GUI and CLI |
| `tests/YiLocal.Checks` | Native protocol, timing, storage and optional hardware checks |
| `tests/test_*.py` | Python transport and retention tests |
| `scripts/check_media_integration.py` | Synthetic native muxing and independent PyAV decode test |
| [PROTOCOL.md](PROTOCOL.md) | Observed wire formats, command IDs and uncertainty |

```powershell
dotnet build YiLocal.sln
dotnet run --project tests/YiLocal.Checks
python -m unittest discover -s tests -v
```

The synthetic media integration check additionally needs `imageio-ffmpeg==0.6.0`.
It generates a test pattern; no camera or personal video is used. GitHub Actions
is configured for Windows and Linux source checks. See [CONTRIBUTING.md](CONTRIBUTING.md).

MIT licensed original source; dependencies retain their own licenses. See
[THIRD-PARTY.md](THIRD-PARTY.md), including the separate FFmpeg binary packaging notes.
This project is independent and is not affiliated with YI or the camera vendor.

Private research, camera captures, keys, recordings, vendor files and compiled
binaries are excluded from Git. Create public source packages using **git archive**,
not by zipping an entire development checkout containing ignored files.
