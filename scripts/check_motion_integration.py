"""Synthetic local-motion recording, pre-roll and AAC clock checks; no camera/private footage."""
from pathlib import Path
from contextlib import closing
import json
import sqlite3
import subprocess
import tempfile

import av
import imageio_ffmpeg


def run(*args):
    result = subprocess.run([str(a) for a in args], capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout


def main():
    root = Path(__file__).resolve().parents[1]
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    run("dotnet", "build", root / "tests/YiLocal.Checks", "-c", "Release")
    dll = root / "tests/YiLocal.Checks/bin/Release/net10.0/YiLocal.Checks.dll"
    with tempfile.TemporaryDirectory(prefix="openyi-motion-check-") as temporary:
        work = Path(temporary)
        source = ("color=c=gray:s=160x90:r=15:d=30[bg];"
                  "testsrc2=size=160x90:rate=15:duration=30[moving];"
                  "[bg][moving]overlay=enable='between(t,5,10)+between(t,20,23)'")
        run(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", source,
            "-c:v", "libx264", "-bf", "0", "-g", "30", "-sc_threshold", "0",
            "-x264-params", "aud=1:repeat-headers=1", "-f", "h264", work / "video.h264")
        run(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            "sine=frequency=440:sample_rate=16000", "-t", "30", "-ac", "1", "-c:a", "aac",
            "-f", "adts", work / "audio.aac")
        originals = {}
        for mode in ("Original", "Balanced"):
            folder = work / mode
            print(run("dotnet", dll, "--motion-fixture", work / "video.h264", work / "audio.aac", folder, ffmpeg, mode).strip())
            events = json.loads((folder / "events.json").read_text())
            assert [e["active"] for e in events] == [True, False, True, False], events
            assert 12000 <= events[1]["milliseconds"] < 14000, events
            assert 25000 <= events[3]["milliseconds"] < 27000, events
            with closing(sqlite3.connect(folder / ".yi-library.sqlite")) as db:
                rows = db.execute("SELECT c.name,c.complete,d.kind FROM clips c JOIN clip_details d ON c.name=d.name ORDER BY c.started,c.rowid").fetchall()
            assert len(rows) == 2, rows
            for i, (name, complete, kind) in enumerate(rows):
                assert complete and kind == "Motion", rows
                path = folder / name
                with av.open(str(path)) as container:
                    frames = list(container.decode(video=0))
                    assert frames and frames[0].pts == 0
                    count = len(frames)
                    # Independent video decode also proves the buffered start is a complete GOP.
                    assert (count > 30 if mode == "Original" else 2 <= count <= 8), count
                with av.open(str(path)) as container:
                    packets = [(round(float(p.pts * p.time_base), 3), bytes(p))
                               for p in container.demux(audio=0) if p.size]
                    assert len(packets) > 20
                    assert 0 <= packets[0][0] <= .07, packets[0][0]
                    assert all(abs(b[0] - a[0] - .064) < .002 for a, b in zip(packets, packets[1:])), "AAC gaps/duplicates in pre-roll"
                if mode == "Original":
                    originals[i] = packets
                else:
                    # Start/end may differ by one feed frame due to asynchronous decoding.
                    common = {payload: stamp for stamp, payload in originals[i]}
                    overlap = [(stamp, common[payload]) for stamp, payload in packets if payload in common]
                    assert len(overlap) > 20
                    shift = overlap[0][0] - overlap[0][1]
                    assert all(abs(a - b - shift) < .002 for a, b in overlap), "Low-rate encoding changed the AAC clock"
                print(f"  Clip {i + 1}: {count} decoded video frames, {len(packets)} AAC packets; bounded pre-roll and audio timing intact.")


if __name__ == "__main__":
    main()
