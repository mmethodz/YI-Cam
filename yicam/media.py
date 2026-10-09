"""Export original video, or an explicitly labelled 4K software upscale."""
from fractions import Fraction
from pathlib import Path
import shutil
import av


def export_video(source: Path, target: Path, upscale=False):
    if source.resolve() == target.resolve() or target.exists():
        raise FileExistsError('Choose a new export filename.')
    temporary = target.with_name(target.stem + '.partial.mp4')
    with temporary.open('xb'):
        pass
    try:
        if not upscale:
            shutil.copyfile(source, temporary)
        else:
            with av.open(str(source)) as incoming, av.open(str(temporary), 'w', format='mp4', options={'movflags': '+faststart'}) as outgoing:
                input_stream = incoming.streams.video[0]
                stream = outgoing.add_stream('libx264', rate=input_stream.average_rate or Fraction(15))
                stream.width, stream.height, stream.pix_fmt = 3840, 2160, 'yuv420p'
                stream.time_base = Fraction(1, 1000)
                stream.codec_context.time_base = Fraction(1, 1000)
                stream.codec_context.max_b_frames = 0
                stream.options = {'crf': '20', 'preset': 'veryfast'}
                stream.codec_context.thread_count = 2
                for frame in incoming.decode(video=0):
                    scaled = frame.reformat(width=3840, height=2160, format='yuv420p', interpolation='LANCZOS')
                    scaled.pts = round(float(frame.pts * frame.time_base) * 1000)
                    scaled.time_base = Fraction(1, 1000)
                    for packet in stream.encode(scaled):
                        outgoing.mux(packet)
                for packet in stream.encode():
                    outgoing.mux(packet)
        temporary.rename(target)
    except BaseException:
        temporary.unlink(missing_ok=True)
        raise
