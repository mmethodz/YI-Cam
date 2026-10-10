# OpenYI

Local camera setup, live viewing and recording for supported **YI IoT** cameras.
OpenYI combines a native Windows surveillance app with an open-source firmware
patch workflow that puts the camera's device key under its owner's control.

**The recommended setup is OpenYI local01 firmware + the OpenYI Windows app.**
Once local01 is installed, generate a Wi-Fi QR locally, show it to the camera,
then discover and connect to it directly. No vendor app, account, binding token
or cloud subscription is needed for this pairing flow. Recordings stay on your
own disk.

![OpenYI Windows app showing live video, local recording, and camera controls (earlier branding)](assets/screenshot-1.png)

[Get started](#get-started) · [Supported camera](#supported-camera) ·
[Features](#features) · [Verification status](#verification-status) ·
[Documentation](#documentation)

## Supported camera

The firmware currently targets **one identified hardware and firmware combination**:

| Item | Supported reference |
| --- | --- |
| Platform / sensor | Anyka AK3918E / GC1084 |
| Board | `Cloud39EV2_AK3918E80PIN_MNBD` |
| Original application firmware | `6.0.24.10_202401091113` |
| Recommended patch profile | `ak3918e-local01` |
| Installed local01 version | `6.0.24.10_202610100002` |

The workshop checks the exact original filesystem and binary hashes. A matching
case, brand, YI IoT app or reported hardware number **253** is not sufficient to
establish compatibility. See [hardware identification and backup evidence](firmware/docs/HARDWARE_AND_BACKUP.md).

Local01 installation, cold-boot reconnection, physical reset and vendor-free QR
pairing have succeeded on the reference camera. **Internet was available during
those tests.** Operation with Internet blocked, a complete traffic audit and
recovery from a failed boot remain unverified. See [verification status](#verification-status).

## Get started

### 1. Install the recommended firmware

Local01 is required for the default **OpenYI firmware setup** page. If your camera
already has the reviewed local01 build, continue to step 2.

Start with the [firmware workshop instructions](firmware/README.md). You need the
matching original application filesystem from your own camera backup, Python
3.10+, and the documented Linux build tools; Windows uses Ubuntu under WSL.
Original vendor images and private camera backups are not included in this repo.
The initial Wi-Fi installation requires a reachable stock camera on your LAN
and owner access to its FTP maintenance interface.

From the repository root, run the single interactive tool:

```powershell
python firmware/openyi_fw.py
```

1. Choose **Build and verify** and keep the default `ak3918e-local01` profile.
2. Let both reproducible builds and the mandatory independent verification finish.
3. Inspect the final image, extracted files, exact patches and verification reports.
4. Choose **Flash an inspected build over Wi-Fi** when ready. The tool verifies
   again, takes fresh full-flash backups, checks the staged bytes and requires
   confirmation of the exact image before writing. It verifies the installed
   application payload after reboot.

Building and inspection do not contact the camera. Flashing is a separate,
explicit action. The installer targets the application partition and preserves
bootloader, kernel, factory identity and calibration.

**The output is `update.tar`, not `home.bin`.** Do not rename it or substitute
generic YI Home SD-card instructions: that recovery route is not established for
this board. Use the [documented installation workflow](firmware/docs/PATCH_WORKFLOW.md).

Local01 keeps local authentication and media encryption, replaces vendor key
refresh with a persistent owner key, and disables the identified cloud binding,
registration, upload and notification paths. The separate local02 keyless profile
is an unflashed experiment; it is not needed for vendor-free pairing.

### 2. Build and launch the Windows app

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
   compatible with [global.json](global.json).
2. Run **Build Windows.cmd**, then **Start Windows.cmd**.
3. Select an [FFmpeg executable](https://ffmpeg.org/download.html) in **Storage**,
   or place `ffmpeg.exe` beside the app. FFmpeg enables live preview, playback,
   compressed recording, motion detection, snapshots and audio features.

The executable is `dist/YI-Local/YiLocal.Windows.exe`. A normal build needs the
.NET 10 Desktop Runtime on the destination PC. To include the runtime:

```powershell
powershell -NoProfile -File scripts/build-windows.ps1 -SelfContained
```

The Windows app is native C# / WinForms. It does not run Python, BlueStacks or a
proprietary vendor runtime. Python is used separately for the firmware workshop
and protocol reference tools.

### 3. Pair directly with OpenYI

Open **Camera pairing → OpenYI firmware setup**. New installations without a
saved camera open this page automatically.

1. Choose the camera's Wi-Fi network. **Use this PC's Wi-Fi** reads connected and
   saved Windows networks, including saved profiles while the PC uses Ethernet.
   Select a network from the list if several are saved. Password autofill depends
   on Windows permissions. You can enter the details yourself, or use **Use saved
   camera's Wi-Fi** to copy them from an existing OpenYI camera.
2. Choose **Show QR code** or **Save QR image**. The saved PNG can be displayed on
   a phone in front of a wall-mounted camera. It contains your Wi-Fi credentials;
   keep it private.
3. Put the already-patched camera into QR setup mode and show it the code. The
   reference unit entered this mode after a physical reset. Wait for Wi-Fi
   connection and the pairing-success prompt.
4. Choose **Find cameras**, select the camera and give it a name. A manual LAN
   address is also available if discovery is blocked.
5. Choose **Connect and save camera**. OpenYI checks the installed firmware,
   reads the owner key directly, authenticates and saves an encrypted profile.
   There is no pairing ID or vendor token to copy.

**Already on the correct Wi-Fi with local01? Skip the QR/reset steps.** Use
**Find cameras → Connect and save camera**. An existing working OpenYI profile
can also be used directly after the firmware migration.

The first camera becomes primary. Pairing another identity adds a separate
experimental camera entry; it does not replace the primary camera. If Windows
requests network access, allow OpenYI on the network containing your cameras.

The [pairing guide](firmware/docs/LOCAL_PAIRING.md) documents the QR format,
maintenance-password override, direct key import and independent Python route.
Key import uses an unencrypted FTP maintenance connection on the LAN; subsequent
camera sessions retain local authentication and media encryption. A reset is
not required to read the owner key, and the tested reset preserved that key.

### 4. Choose how to record

Set the recording folder and limits in **Storage**, then select a mode in
**Capture options**:

| Mode | Behavior |
| --- | --- |
| Continuous / low-rate | Record continuously with real elapsed time. Optional encoding and frame-rate limits reduce storage use. |
| Motion · local detection on this PC | Set a changed-image-area threshold and post-motion recording time: 30 seconds, 1 minute, 5 minutes or custom. Each new movement restarts the timer. |
| Timelapse | Capture selected pictures and play them faster; no audio. |

**Original stream** is the default lossless remux: it preserves the camera's
encoded video without re-encoding. Optional FFmpeg H.264 profiles offer balanced
or smaller files, with recording-rate presets of **5, 1 and 0.5 fps** and a custom
rate limited by the source. These settings do not lower live preview fps.
Original-stream recording itself does not need FFmpeg; local motion analysis does.

Use **Start recording** for continuous/timelapse capture or **Arm motion** for
motion capture. Keep the PC and OpenYI running. Motion recording can include a
short buffered lead-in; its length depends on the camera's keyframes. Camera
movement and lighting changes can also trigger detection.

## Features

| Feature | Available in the Windows app |
| --- | --- |
| Live camera controls | Stream quality, pan/tilt, motion tracking, infrared/color night modes and image rotation. Some gimbal behavior remains experimental. |
| Recording browser | Thumbnails, dates, duration, camera/profile metadata, filters, playback and seeking; protect, export and delete through the recording catalogue. |
| Storage management | Clip rotation, disk budget, free-space floor and optional recycling of completed, unprotected recordings. |
| Snapshots | Save a PNG at the source resolution. |
| Audio | Optional camera microphone monitoring and recording; PC microphone talk-back to the camera speaker. Listening pauses while talking. |
| Optional alarm | Separate motion threshold, entry/exit grace, Home/Away, fixed or until-stopped playback; camera speaker, selected PC output or both. Primary camera only. |
| Multiple cameras · experimental | Independent camera profiles, sessions and recording libraries, with a grid and combined recording browser. |
| English and Finnish | Resource-based localization, ready for community translations. Choose **Settings → App language / Kieli → Suomi**, then restart. |

The measured HD stream on the reference camera is **1280 × 720 at about 15 fps**;
SD is 640 × 360. **Export 4K (upscaled)** is a software enlargement, not native
4K capture. Physical control and recording evidence is documented separately
from post-flash local01 qualification.

Storage estimates use completed recordings. The owner's original recordings
were roughly **300 MB/hour per camera**; actual use depends on the scene and
profile. Re-encoding trades CPU and some detail for storage savings, and reducing
fps alone does not guarantee a smaller file. See [measured examples](docs/RECORDING.md).

Recycling starts **off**. With it enabled, only eligible files in OpenYI's SQLite
catalogue are removed; protected clips, active clips and unrelated files are
excluded. Multi-camera storage budgets apply **per camera**, not across the fleet.

OpenYI currently starts with recording stopped and the alarm in **Home**.
Connect and arm explicitly after launch. A connected recording session retries
after connection loss; this is separate from unattended startup recovery.
The app exposes no remote HTTP panel and needs no Internet port forwarding.

## Settings and local key recovery

Settings are per Windows user and do not depend on where you launch the EXE:

| Data | Default location |
| --- | --- |
| App settings | `%LOCALAPPDATA%\YI Local\settings.json` |
| Previous valid settings | `%LOCALAPPDATA%\YI Local\settings.json.bak` |
| Primary camera profile | `%LOCALAPPDATA%\YI Local\device.dpapi` |
| Additional camera profiles | `%LOCALAPPDATA%\YI Local\camera-profiles` |
| Recordings and catalogue | Your **Videos / YI Local** folder; configurable in Storage |

Storage and capture edits save automatically while recording is stopped; edits
during recording wait until it stops. Path edits save after leaving the text box.
Camera profiles use current-user Windows DPAPI, so copying a `.dpapi` file to
another Windows account is not a portable pairing method. On another PC, import
the key directly from the local01 camera using **Connect and save camera**.

If a local01 connection needs its profile refreshed, use that same direct flow,
or the workshop's **Export local01 camera key for OpenYI** followed by
**Import encrypted pairing profile**. This does not require the vendor app or
another reset. Failed verification preserves the existing saved profile.

The project was previously called YI Local. The repository remains **YI-Cam**,
and existing executable, settings and recording paths keep their old names for
compatibility, including Windows Firewall allowances. No registry setup is needed.

## Verification status

Evidence is from **one physical camera**, plus automated tests. These are
distinct levels of verification:

| Area | Current evidence |
| --- | --- |
| Firmware build and patches | Reproducible images, independent filesystem/checksum verification and selected ARM execution checks pass offline. |
| local01 installation | Wi-Fi flash, reboot and complete application-image readback passed on 10 October 2026. |
| Owner key persistence | Existing pairing reconnected after a separate cold power cycle, without a vendor-app key refresh. |
| Vendor-free setup | Physical reset, local QR scan, Wi-Fi connection, fresh direct key import and native live video passed on 11 October 2026. |
| Existing app features | Recording, motion capture, controls and audio have reference-camera evidence on stock firmware; the full suite still needs repeating after the local01 flash. |
| Multiple cameras | Concurrent software sessions tested with two simulated cameras; multiple physical cameras and sustained load remain unqualified. |
| Remaining firmware checks | Internet-blocked setup/operation, complete network traffic capture, initial key creation without a previous key, extended operation and failed-boot recovery. |

Local01 disables the identified vendor-dependent paths and keeps a local owner
key. That implementation and the successful vendor-free pairing test do not
establish that every process on the camera is silent on the WAN. Read the
[installation evidence](firmware/docs/LOCAL01_INSTALLATION.md) and
[exact patch scope](firmware/docs/LOCAL01.md) before extending support to another
unit. Offline checksum verification establishes image integrity, not recoverability.

## Original firmware compatibility

Existing stock-firmware cameras remain supported through **Camera setup** and
**Original firmware · advanced**. This is a compatibility route; the recommended
vendor-free setup above requires local01.

On stock firmware, the device key can become outdated while vendor account
pairing remains valid. Open the camera's live view in the already-paired vendor
PC client, then use **Camera setup → Import from running YI IoT**. The native
importer supports the verified 32-bit client `1.0.1.1_202209261648`, reads the
matching pairing and authenticates before saving. It does not modify the vendor
process. A known valid key or same-user DPAPI import is also supported.

Stock key recovery may therefore still depend on the vendor client. Stock QR
binding tokens are separate from device keys; they are not needed for local01's
setup flow. Stock-camera compatibility does not remove that camera's cloud behavior.

## Documentation

| Guide | Contents |
| --- | --- |
| [Firmware workshop](firmware/README.md) | Prerequisites, build → verify → inspect → flash, and research index |
| [Local pairing](firmware/docs/LOCAL_PAIRING.md) | QR payload, direct onboarding and key handoff |
| [Local key lifecycle](firmware/docs/LOCAL_KEY_MAP.md) | Key creation, persistence, readers/writers and reset behavior |
| [Protocol](PROTOCOL.md) | Wire formats, commands, measured controls and open questions |
| [Recording](docs/RECORDING.md) / [motion](docs/MOTION.md) | Encoding, rates, storage measurements and trigger behavior |
| [Browser/player](docs/BROWSER.md) | Playback, filters, protection and export |
| [Audio](docs/AUDIO.md) / [alarm](docs/ALARM.md) | Listening, talk-back, optional siren and qualification limits |
| [Multiple cameras](docs/MULTI_CAMERA.md) | Grid, session isolation, per-camera storage and testing limits |
| [Translations](docs/LOCALIZATION.md) | English/Finnish resources and adding a language |
| [Contributing](CONTRIBUTING.md) | Source checks, compatibility reports and private-data handling |

## Python reference and development

`src/YiLocal.Core` contains the native protocol, recording and storage code;
`src/YiLocal.Windows` is the Windows UI. `yicam` is the Python protocol prototype,
GUI and CLI. `firmware` contains the public patch profiles, workshop and findings.

For the Python UI on Windows, use Python 3.12+ with Tcl/Tk, run **Setup.cmd**, then
**Start Recorder.cmd**. Linux/macOS use a virtual environment with
`requirements-crossplatform.txt` and `python -m yicam.app`; keys use the OS
keychain. The native Windows onboarding flow is the recommended client setup.
Linux/macOS desktop integration remains unverified.

Representative source checks:

```powershell
dotnet build YiLocal.sln -c Release
dotnet run --project tests/YiLocal.Checks -c Release
dotnet run --project tests/YiLocal.Checks -c Release -- --localization
python -m unittest discover -s tests -v
python -B -m unittest discover -s firmware/tests -v
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for dependencies and the media integration
checks. Tests use synthetic inputs; hardware operations are explicit. Never
include Wi-Fi QR codes, device keys, private camera footage or raw device backups
in a compatibility report or source archive.

## License and independence

Original OpenYI application code, patch sources and tooling are [MIT licensed](LICENSE).
The firmware workflow patches an owner's stock image; the resulting image still
contains proprietary vendor drivers, libraries and other components. This is not
a fully open-source replacement firmware image. See [third-party notices](THIRD-PARTY.md)
and [firmware scope](firmware/docs/LOCAL01.md).

Vendor binaries, generated firmware images, credentials and recordings are not
distributed in this repository. Use `git archive` for public source packages,
rather than zipping a development checkout containing ignored private files.
OpenYI is independent and is not affiliated with YI or the camera manufacturer.
