"""Exercise the native live talk encoder with synthetic PCM, never a real microphone."""
from pathlib import Path
from array import array
import subprocess
import tempfile

import av
import imageio_ffmpeg

root = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='openyi-talk-') as directory:
    audio = Path(directory) / 'synthetic.aac'
    subprocess.run(['dotnet', 'run', '--project', str(root / 'tests/YiLocal.Windows.Checks'),
                    '-c', 'Release', '--', '--talk-encoder', imageio_ffmpeg.get_ffmpeg_exe(), str(audio)], check=True)
    with av.open(str(audio)) as container:
        stream = container.streams.audio[0]
        assert stream.codec_context.name == 'aac' and stream.codec_context.sample_rate == 16000
        frames = list(container.decode(audio=0))
    assert len(frames) == 24
    assert all(f.samples == 1024 and f.sample_rate == 16000 and f.layout.name == 'mono' for f in frames)
    assert all(f.format.name == 'fltp' for f in frames)
    assert max(abs(sample) for f in frames for sample in array('f', bytes(f.planes[0])[:f.samples * 4])) > .01
    print('Independent AAC decode passed: 24 frames / 24576 mono samples at 16 kHz with a non-silent synthetic signal.')
