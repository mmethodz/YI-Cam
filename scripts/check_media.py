"""Independent decode check for native C# muxer output (synthetic test pattern only)."""
from pathlib import Path
import sys
import av

files = sorted(Path(sys.argv[1]).glob('YI_*.mp4'))
assert len(files) >= 2, 'Expected keyframe-based clip rotation'
total = 0
for path in files:
    with av.open(str(path)) as container:
        stream = container.streams.video[0]
        assert (stream.width, stream.height) == (160, 90)
        frames = list(container.decode(video=0))
        assert frames and not any(frame.is_corrupt for frame in frames)
        times = [float(frame.pts * frame.time_base) for frame in frames]
        assert times[0] == 0
        assert all(abs(b - a - .067) < 1e-6 for a, b in zip(times, times[1:]))
        assert abs(float(stream.duration * stream.time_base) - len(frames) * .067) < 1e-6
        total += len(frames)
        print(f'{len(frames)} frames; {stream.width}x{stream.height}; duration {len(frames)*.067:.3f}s')
assert total == 150, total
print('Native MP4 clips decoded cleanly with all 150 frames and original timestamps.')
