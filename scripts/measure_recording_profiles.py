"""Compare native recording profiles using the same private capture; publish measurements, not footage."""
from pathlib import Path
from contextlib import closing
import argparse
import json
import sqlite3
import subprocess

import av
import imageio_ffmpeg

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("input", type=Path, help="Annex-B stream with an AUD before each source frame")
parser.add_argument("timestamps", type=Path, help="JSON array of ordered camera timestamps in milliseconds")
parser.add_argument("output", type=Path, help="New private output directory")
parser.add_argument("--width", type=int, required=True)
parser.add_argument("--height", type=int, required=True)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=False)
root = Path(__file__).resolve().parents[1]
subprocess.run(["dotnet", "build", str(root / "tests/YiLocal.Checks"), "-c", "Release", "--nologo"], check=True)
checks = root / "tests/YiLocal.Checks/bin/Release/net10.0/YiLocal.Checks.dll"
results = []
for encoding, fps, mode in [("Original", "source", "Continuous"), ("Balanced", "source", "Continuous"),
                             ("Balanced", "5", "Continuous"), ("Small", "1", "Continuous"),
                             ("Small", "0.5", "Continuous"), ("Small", "0.5", "Timelapse")]:
    folder = args.output / f"{encoding}-{fps}-{mode}"
    subprocess.run(["dotnet", str(checks), "--measure-recording", str(args.input), str(folder), imageio_ffmpeg.get_ffmpeg_exe(),
                    encoding, fps, mode, str(args.width), str(args.height), "67", str(args.timestamps)], check=True)
    with closing(sqlite3.connect(folder / ".yi-library.sqlite")) as database:
        rows = database.execute("SELECT c.name,c.bytes,c.duration,d.capture_duration,d.frames FROM clips c JOIN clip_details d USING(name) WHERE c.complete=1").fetchall()
    assert rows, "No completed clips"
    for name, size, duration, capture_duration, frame_count in rows:
        with av.open(str(folder / name)) as container:
            frames = list(container.decode(video=0))
            assert len(frames) == frame_count and not any(frame.is_corrupt for frame in frames)
            assert all((frame.width, frame.height) == (args.width, args.height) for frame in frames)
    seconds = sum(row[3] for row in rows)
    size = sum(row[1] for row in rows)
    results.append(dict(encoding=encoding, fps=fps, mode=mode, bytes=size, capture_seconds=round(seconds, 3),
                        playback_seconds=round(sum(row[2] for row in rows), 3), frames=sum(row[4] for row in rows),
                        megabytes_per_hour=round(size / seconds * 3600 / 1_000_000, 2)))
    (args.output / "measurements.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
print(json.dumps(results, indent=2))
