"""Exercise the actual Windows player with synthetic encoded clips and a silent, paced audio sink."""
from pathlib import Path
import subprocess
import tempfile
import sys

import imageio_ffmpeg


def run(*args):
    result = subprocess.run([str(a) for a in args], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=45)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout.strip()


def main():
    if sys.platform != 'win32':
        print('Windows player integration requires Windows; portable media checks run separately.')
        return
    root = Path(__file__).resolve().parents[1]
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    run('dotnet', 'build', root / 'tests/YiLocal.Windows.Checks', '-c', 'Release')
    checks = root / 'tests/YiLocal.Windows.Checks/bin/Release/net10.0-windows/YiLocal.Windows.Checks.dll'
    with tempfile.TemporaryDirectory(prefix='openyi-player-') as temporary:
        work = Path(temporary)
        for rate in (15, 5, 1, .5):
            path = work / f'encoded-{rate}.mp4'
            run(ffmpeg, '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', f'testsrc2=size=160x90:rate={rate}',
                '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=16000', '-t', '8',
                '-c:v', 'libx264', '-crf', '28', '-bf', '0', '-c:a', 'aac', '-ac', '1',
                '-movflags', '+frag_keyframe+delay_moov+default_base_moof', '-frag_duration', '1000000', path)
            for seek, sound in ((0, True), (2.5, True), (0, False)):
                print(f'{rate} fps:', run('dotnet', checks, ffmpeg, path, 8, seek, str(sound)))
        # A damaged/missing decoder input must fail instead of hanging indefinitely.
        result = subprocess.run(['dotnet', str(checks), ffmpeg, str(work / 'missing.mp4'), '8'],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=12)
        assert result.returncode != 0 and 'decoder' in result.stderr.lower(), result.stderr
    print('Encoded source/5/1/0.5 fps playback, sound, seek, silent playback and decoder failure checks passed.')


if __name__ == '__main__':
    main()
