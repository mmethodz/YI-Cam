"""Synthetic/native alarm checks; never opens a microphone, camera or audible output."""
import os
from pathlib import Path
import subprocess
import tempfile

import av
import imageio_ffmpeg

if os.name != "nt":
    raise SystemExit("The alarm output checks target the native Windows application.")

root = Path(__file__).resolve().parent.parent
with tempfile.TemporaryDirectory(prefix="openyi-alarm-checks-") as temporary:
    subprocess.run([
        "dotnet", "run", "--project", str(root / "tests/YiLocal.Windows.Checks"),
        "-c", "Release", "--", "--alarm-audio", imageio_ffmpeg.get_ffmpeg_exe(), temporary,
    ], cwd=root, check=True)
    files = list(Path(temporary).glob("*.aac"))
    assert len(files) >= 3, "Missing converted fixtures/default siren"
    for path in files:
        with av.open(str(path)) as media:
            stream = media.streams.audio[0]
            frames = list(media.decode(stream))
            assert frames and all(f.sample_rate == 16000 and len(f.layout.channels) == 1 for f in frames)
            seconds = sum(f.samples for f in frames) / 16000
            assert 0.5 <= seconds <= 60.8
    print(f"Independent decode: {len(files)} alarm samples are valid mono 16 kHz AAC; no audible playback.")
