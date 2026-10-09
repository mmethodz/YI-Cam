"""Generate a synthetic clip, mux it with C#, then independently decode with PyAV."""
from pathlib import Path
import subprocess
import sys
import tempfile
import imageio_ffmpeg

root = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='yi-local-synthetic-') as folder:
    folder = Path(folder)
    elementary = folder / 'test-pattern.h264'
    subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), '-hide_banner', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=160x90:rate=15', '-frames:v', '150',
        '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-x264-params',
        'keyint=30:min-keyint=30:scenecut=0:bframes=0:aud=1', '-f', 'h264', str(elementary)], check=True)
    subprocess.run(['dotnet', 'run', '--project', str(root / 'tests/YiLocal.Checks'),
        '-c', 'Release', '--', '--mux-fixture', str(elementary), str(folder / 'clips')], check=True)
    subprocess.run([sys.executable, str(root / 'scripts/check_media.py'), str(folder / 'clips')], check=True)
