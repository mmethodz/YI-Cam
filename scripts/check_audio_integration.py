"""Independent decoding/timestamp checks for native AAC + AVC and encoded low-rate clips."""
from pathlib import Path
import subprocess
import tempfile
import sqlite3
from contextlib import closing
import av
import imageio_ffmpeg


def run(*args):
    subprocess.run([str(a) for a in args], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)


def main():
    root = Path(__file__).resolve().parents[1]
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    run("dotnet", "build", root / "tests/YiLocal.Checks", "-c", "Release")
    dll = root / "tests/YiLocal.Checks/bin/Release/net10.0/YiLocal.Checks.dll"
    with tempfile.TemporaryDirectory(prefix="openyi-audio-check-") as temporary:
        work = Path(temporary)
        run(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=15", "-t", "12", "-c:v", "libx264", "-bf", "0", "-g", "30", "-x264-params", "aud=1:repeat-headers=1", "-f", "h264", work / "video.h264")
        run(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000", "-t", "12", "-ac", "1", "-c:a", "aac", "-f", "adts", work / "audio.aac")
        reference_packets = []
        for mode in ("Original", "Balanced"):
            folder = work / mode
            run("dotnet", dll, "--av-fixture", work / "video.h264", work / "audio.aac", folder, ffmpeg, mode)
            with closing(sqlite3.connect(folder / ".yi-library.sqlite")) as db:
                clips = [folder / row[0] for row in db.execute("SELECT name FROM clips ORDER BY started,rowid")]
            assert len(clips) == 2, (mode, len(clips))
            video_count = 0
            packets = []
            for index, path in enumerate(clips):
                # Segment boundaries are 0 and 6030 ms in the source.
                base = index * 6.030
                with av.open(str(path)) as container:
                    video, audio = container.streams.video[0], container.streams.audio[0]
                    assert audio.codec_context.sample_rate == 16000 and audio.codec_context.channels == 1
                    for packet in container.demux(audio):
                        if packet.size:
                            packets.append((round(base + float(packet.pts * packet.time_base), 3), bytes(packet)))
                with av.open(str(path)) as container:
                    frames = list(container.decode(audio=0))
                    assert frames and all(f.samples == 1024 for f in frames)
                with av.open(str(path)) as container:
                    frames = list(container.decode(video=0))
                    video_count += len(frames)
                    assert frames[0].width == 160 and frames[0].height == 90
            assert abs(packets[0][0] - .250) < .002, (mode, packets[0][0])
            assert all(abs(stamp - (.250 + n * .064 + n // 50 * .010)) < .002 for n, (stamp, _) in enumerate(packets)), mode
            if mode == "Original":
                assert video_count == 180, video_count
                reference_packets = packets
            else:
                assert 10 <= video_count <= 14, video_count
                assert packets == reference_packets, "Encoding altered AAC payloads or their common-clock timestamps"
            print(f"{mode}: {video_count} decoded video frames, {len(packets)} unchanged AAC packets; delayed start and clock gaps preserved.")


if __name__ == "__main__":
    main()
