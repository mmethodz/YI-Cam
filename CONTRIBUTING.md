# Contributing

Start with README.md and PROTOCOL.md. Python is the protocol prototype; the
WinForms app and its recording backend are native C#. Keep equivalent wire
behavior covered in both implementations when changing the protocol.

For interface translations, follow [the localization guide](docs/LOCALIZATION.md).
English and Finnish resources are separate from C# behavior; additional languages
need a resource file and a catalogue entry. Keep saved/protocol identifiers invariant.

Use synthetic video and fake keys in tests. Never submit device keys, pairing QR
codes, camera UIDs, account credentials, Wi-Fi details, private packet captures,
or camera footage. Describe a new model with its hardware number, firmware version,
command response lengths, and redacted observations. A setting being accepted by
the camera does not prove that its physical behavior changed.

Run:

```powershell
dotnet build YiLocal.sln
dotnet run --project tests/YiLocal.Checks
dotnet run --project tests/YiLocal.Checks -c Release -- --localization
python -m unittest discover -s tests -v
# Independent native media integration checks:
python -m pip install imageio-ffmpeg==0.6.0
python scripts/check_media_integration.py
python scripts/check_recording_profiles.py
python scripts/check_audio_integration.py
python scripts/check_motion_integration.py
# Windows player, including encoded low-rate recordings with audio:
python scripts/check_player_integration.py
# Windows preferences and synthetic microphone encoder (no real mic is opened):
dotnet run --project tests/YiLocal.Windows.Checks -c Release -- --settings
python scripts/check_talk_integration.py
# Optional alarm import, repeated output and stop checks (silent test sinks):
python scripts/check_alarm_integration.py
```

These synthetic tests do not access a camera. Native checks also run two simulated
cameras on loopback addresses to verify independent sessions, controls and media.
This is not multiple-camera hardware qualification; see docs/MULTI_CAMERA.md.
Hardware checks are opt-in, use an
already paired camera, and must save outside the source checkout. Do not reset or
flash a camera just to test a control. Track unsupported models explicitly rather
than assuming shared branding implies the same protocol.

The native pairing tests use synthetic process-memory fixtures and fake keys.
They cover inactive/mismatched/changing objects, ambiguous candidates, rejected
authentication, timeouts, cancellation, and keeping the old profile on failure.
To check the version-guarded importer against your own running vendor live view:

```powershell
dotnet run --project tests/YiLocal.Checks -- --inspect-import <your-encrypted-profile.dpapi>
```

This optional read-only check prints only whether the identity and key match the
profile. It does not save the key or contact the camera. Use the Windows app to
test authenticated import under the network allowance for its executable.

The source archive should be created with `git archive`, never by zipping the
working directory. The working directory may contain ignored private research.
