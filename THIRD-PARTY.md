# Third-party components

The original OpenYI source is MIT licensed. Dependency licenses remain separate.

- **Siren Noise**, KevanGC — Public Domain, bundled as the optional alarm default.
  [Source/license declaration](https://soundbible.com/1577-Siren-Noise.html) and
  [download provenance and hash](assets/alarm/LICENSE.md). The original WAV is
  embedded in the Windows application; no runtime download is required.

- [QRCoder](https://github.com/Shane32/QRCoder), version 1.8.0, MIT: local PNG setup QR generation. No vendor runtime is required.
No vendor client, APK, firmware, or decompiled source is distributed in this repository.

| Component | Purpose | Upstream |
| --- | --- | --- |
| .NET / Windows Forms | Native Windows application | https://github.com/dotnet/winforms (MIT) |
| Microsoft.Data.Sqlite | Recording catalogue | https://github.com/dotnet/efcore (MIT) |
| SQLitePCLRaw | SQLite interop | https://github.com/ericsink/SQLitePCL.raw (Apache-2.0) |
| SQLite | Embedded database | https://sqlite.org/copyright.html (public domain) |
| PyAV | Python video decoding and muxing | https://github.com/PyAV-Org/PyAV (BSD-3-Clause) |
| Pillow | Python image display | https://github.com/python-pillow/Pillow (HPND) |
| PyCryptodome | Python AES | https://www.pycryptodome.org/src/license |
| keyring (optional) | Linux/macOS system keychain | https://github.com/jaraco/keyring (MIT) |
| Frida (optional) | One-time import from an owned, paired PC client | https://github.com/frida/frida (wxWindows Library Licence 3.1) |

FFmpeg is an external executable selected by the user for native live preview and
4K export. Original-stream recording in the C# backend does not require FFmpeg.
No FFmpeg executable is committed or included in the source archive. The build
script can copy a user-supplied executable for local use.

FFmpeg builds have different licensing depending on enabled components. Before
distributing a binary bundle, include that exact build's license notices and meet
its source distribution requirements. See https://ffmpeg.org/legal.html. A local
development build is not a prepared public binary release. Python wheels also
include their own dependency notices; preserve these when packaging them.

Protocol research references (not bundled dependencies):

- https://github.com/lr-m/Yihaw
- https://github.com/MuhammedKalkan/Anyka-Camera-Firmware

The command layouts in PROTOCOL.md were checked against the owned test camera;
support is not inferred for every product using the YI IoT name.
