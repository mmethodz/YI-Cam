# Experimental multiple cameras

Only **one physical camera** has been tested. The grid and independent native
sessions are available so owners of additional supported cameras can test them;
this is not full multiple-camera hardware qualification.

The **Cameras · experimental** tab always includes the primary camera. It shares
the existing live session and controls, so opening the grid does not create a
second connection to that camera. Enable additional cameras explicitly, then use
**Add camera**. Enter a LAN address and pairing key, import the current pairing
from the running YI IoT PC app, or import a DPAPI profile from the same Windows
account. Every candidate must authenticate to its camera before it is saved.
The app rejects duplicate IP addresses or wire identities, including the primary.

The initial implementation supports a primary plus up to seven additional
configurations. Each can be enabled, connected/disconnected, recorded, edited or
removed independently. Additional-camera Controls provides quality, night mode,
tracking and bounded PTZ steps. Removing a configuration removes its encrypted
pairing file but leaves its recordings on disk and in the browser. No camera
connects automatically at startup. **Connect configured cameras** starts the enabled entries; disabling
the experimental option disconnects additional sessions and finishes their clips.

All recordings use the saved Capture options; storage budgets apply **per
camera**. An additional camera records under `Videos/YI Local/Cameras/<random-id>`
by default, with its own SQLite catalogue and writer lock. The primary camera's
folder is unchanged. The recording browser combines existing camera catalogues
and supports filtering by camera. Total configured disk budget can therefore be
the per-camera budget multiplied by camera count. The free-space floor is shared
by virtue of checking the same drive, but it is not an aggregate quota allocator.
Higher camera counts and encoding profiles require corresponding CPU, network
and storage capacity; sustained concurrent load has not been measured.

Additional pairing files live in `%LOCALAPPDATA%/YI Local/camera-profiles` and use
Windows DPAPI. The `cameras.json` index contains random local IDs and enable flags,
not pairing keys. Loaded profiles remember their own save path: a reconnect or
identity update cannot write an additional camera into `device.dpapi`. The primary
profile, launcher and executable paths remain compatible with earlier versions.

Each `CameraClient` owns its socket/peer, AES key, authentication nonce/request
state, reliable channels, video/audio queues and stream generation. Each
`CameraSession` owns frame ordering, clocks, snapshot buffering, reconnection and
its recorder. A disconnect or rejected key for one camera does not share transport
state with another. Grid previews use a smaller decode size for additional
cameras; recordings keep their selected source resolution/profile.

Automated tests run two synthetic cameras on separate loopback addresses with
different identities and keys. They verify concurrent authenticated requests,
control routing, video/audio decryption and clock separation. Registry tests
check path traversal rejection, duplicates, DPAPI round trips/save destinations,
enable flags and removal. Separate writer/catalogue tests verify that protecting
or deleting the same clip filename in one camera's folder does not affect another.
These are **software tests**, not evidence from two physical devices.

On the physical reference camera, the primary grid shared the existing 1280×720
live preview. Starting and stopping recording from the grid produced a completed
29.174-second original-stream clip. Independent decoding recovered 442 pictures
with strictly increasing timestamps; the SQLite duration matched. This checks
the primary-camera path through the grid, not concurrent physical cameras.

Community reports should include camera hardware/firmware, camera count, quality,
recording profile/rate/audio, approximate CPU/storage use, and whether reconnect,
key refresh and control routing worked. Do not include pairing keys, Wi-Fi QR
payloads, account tokens or private recordings in an issue.
