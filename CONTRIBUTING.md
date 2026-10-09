# Contributing

Start with README.md and PROTOCOL.md. Python is the protocol prototype; the
WinForms app and its recording backend are native C#. Keep equivalent wire
behavior covered in both implementations when changing the protocol.

Use synthetic video and fake keys in tests. Never submit device keys, pairing QR
codes, camera UIDs, account credentials, Wi-Fi details, private packet captures,
or camera footage. Describe a new model with its hardware number, firmware version,
command response lengths, and redacted observations. A setting being accepted by
the camera does not prove that its physical behavior changed.

Run:

```powershell
dotnet build YiLocal.sln
dotnet run --project tests/YiLocal.Checks
python -m unittest discover -s tests -v
# Optional independent native mux/decode integration check:
python -m pip install imageio-ffmpeg==0.6.0
python scripts/check_media_integration.py
```

The synthetic test does not access a camera. Hardware checks are opt-in, use an
already paired camera, and must save outside the source checkout. Do not reset or
flash a camera just to test a control. Track unsupported models explicitly rather
than assuming shared branding implies the same protocol.

The source archive should be created with `git archive`, never by zipping the
working directory. The working directory may contain ignored private research.
