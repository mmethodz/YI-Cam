"""Independent decode, timestamps, frame counts, rotation and metadata checks for native recording profiles."""
from pathlib import Path
from contextlib import closing
import json
import sqlite3
import subprocess
import tempfile

import av
import imageio_ffmpeg
from PIL import Image, ImageChops

root = Path(__file__).resolve().parents[1]
ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
subprocess.run(["dotnet", "build", str(root / "tests/YiLocal.Checks"), "-c", "Release", "--nologo"], check=True)
checks = root / "tests/YiLocal.Checks/bin/Release/net10.0/YiLocal.Checks.dll"

with tempfile.TemporaryDirectory(prefix="openyi-profiles-") as temporary:
    directory = Path(temporary)
    source = directory / "pattern.h264"
    subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=15",
                    "-frames:v", "150", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-x264-params",
                    "keyint=30:min-keyint=30:scenecut=0:bframes=0:aud=1", "-f", "h264", str(source)], check=True)
    results = []
    for profile, fps, mode in [("Original", "source", "Continuous"), ("Balanced", "source", "Continuous"),
                               ("Balanced", "5", "Continuous"), ("Small", "1", "Continuous"),
                               ("Small", "0.5", "Continuous"), ("Small", "0.5", "Timelapse")]:
        folder = directory / f"{profile}-{fps}-{mode}"
        subprocess.run(["dotnet", str(checks), "--encode-fixture", str(source), str(folder), ffmpeg,
                        profile, fps, mode, "160", "90", "67"], check=True)
        if profile == "Original":
            snapshot = folder / "snapshot.png"
            index = int((folder / "snapshot-index.txt").read_text())
            subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "mp4", "-i", str(folder / "snapshot-input.mp4"),
                            "-vf", f"select=eq(n\\,{index})", "-frames:v", "1", "-fps_mode", "passthrough", str(snapshot)], check=True)
            with av.open(str(source)) as container:
                original_frames = list(container.decode(video=0))
            with Image.open(snapshot) as image:
                assert image.size == (160, 90)
                assert ImageChops.difference(image.convert("RGB"), original_frames[-1].to_image()).getbbox() is None
        total_frames, total_bytes, playback = 0, 0, 0
        with closing(sqlite3.connect(folder / ".yi-library.sqlite")) as database:
            rows = database.execute("SELECT c.name,c.duration,c.complete,d.capture_duration,d.frames,d.kind FROM clips c JOIN clip_details d USING(name) ORDER BY c.name").fetchall()
        assert len(rows) == 2, rows
        for name, duration, complete, capture_duration, recorded_frames, kind in rows:
            path = folder / name
            with av.open(str(path)) as container:
                video = container.streams.video[0]
                decoded = list(container.decode(video))
                stamps = [float(frame.pts * frame.time_base) for frame in decoded]
                assert len(stamps) > 0 and all(right > left for left, right in zip(stamps, stamps[1:])), stamps
                assert all(frame.width == 160 and frame.height == 90 for frame in decoded)
                measured = float(video.duration * video.time_base) if video.duration else float(container.duration / av.time_base)
                assert abs(duration - measured) < 0.08, (profile, fps, mode, duration, measured)
            assert complete and capture_duration > 0
            assert recorded_frames == len(decoded), (recorded_frames, len(decoded))
            total_frames += len(decoded)
            total_bytes += path.stat().st_size
            playback += measured
        capture = sum(row[3] for row in rows)
        assert abs(capture - 10.05) < 0.01, capture
        if fps == "source":
            assert total_frames == 150, total_frames
        else:
            # Each independently decodable segment starts a new time bucket.
            assert abs(total_frames - capture * float(fps)) <= len(rows), total_frames
        if mode == "Timelapse":
            assert abs(playback - total_frames / 25) < 0.01, playback
        else:
            assert abs(playback - capture) < (2 / float(fps) if fps != "source" else 0.15), playback
        results.append(dict(profile=profile, fps=fps, mode=mode, frames=total_frames, bytes=total_bytes,
                            capture_seconds=round(capture, 3), playback_seconds=round(playback, 3)))
    print(json.dumps(results, indent=2))
    print("All recording profiles passed independent decode, timing, frame-count, rotation and catalogue checks.")
