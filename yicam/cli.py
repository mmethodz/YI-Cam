"""Local, scriptable access: python -m yicam.cli --help."""
import argparse
from dataclasses import asdict
from pathlib import Path
import queue
import time

from .credentials import load_device
from .protocol import Camera
from .recording import SegmentRecorder
from .storage import StoragePolicy
from .video import FrameClock, FrameOrder


def main():
    parser = argparse.ArgumentParser(description='OpenYI: use the saved pairing key to operate your LAN camera.')
    sub = parser.add_subparsers(dest='action', required=True)
    sub.add_parser('status')
    record = sub.add_parser('record')
    record.add_argument('--seconds', type=float, default=60)
    record.add_argument('--folder', type=Path, default=Path.home() / 'Videos' / 'YI Local')
    record.add_argument('--quality', choices=('hd', 'sd'), default='hd')
    record.add_argument('--clip-seconds', type=float, default=600)
    night = sub.add_parser('night')
    night.add_argument('mode', choices=('infrared', 'colour', 'auto'))
    tracking = sub.add_parser('tracking')
    tracking.add_argument('state', choices=('on', 'off'))
    move = sub.add_parser('move')
    move.add_argument('direction', choices=('up', 'down', 'left', 'right'))
    args = parser.parse_args()
    device = load_device()
    with Camera(device['ip'], device['password'], device.get('uid')) as camera:
        if args.action == 'status':
            data = camera.device_info()
            print('Firmware:', camera.firmware())
            print('Hardware:', data[8], '| Light mode:', data[91], '| Motion tracking:', data[68])
        elif args.action == 'night':
            camera.set_night_vision({'infrared': 0, 'colour': 1, 'auto': 2}[args.mode])
            print('Light mode:', camera.device_info()[91])
        elif args.action == 'tracking':
            camera.set_tracking(args.state == 'on')
            print('Motion tracking:', camera.device_info()[68])
        elif args.action == 'move':
            camera.move({'up': 1, 'down': 2, 'left': 3, 'right': 4}[args.direction], .12)
        elif args.action == 'record':
            if args.seconds <= 0:
                parser.error('--seconds must be positive')
            policy = StoragePolicy(segment_minutes=args.clip_seconds / 60).validate()
            recorder = SegmentRecorder(args.folder, policy=policy)
            order, clock = FrameOrder(), FrameClock()
            try:
                camera.start_video(1 if args.quality == 'hd' else 2)
                end = time.monotonic() + args.seconds
                while time.monotonic() < end:
                    if not camera.running:
                        raise camera.error or ConnectionError('Camera disconnected.')
                    try:
                        frame = camera.frames.get(timeout=.5)
                    except queue.Empty:
                        continue
                    for frame in order.feed(frame):
                        recorder.write(frame.data, clock.time(frame), (order.epoch, frame.generation))
            finally:
                recorder.close()
            print(f'Saved {recorder.frames} frames in {len(recorder.files)} clip(s).')
            for file in recorder.files:
                print(file)


if __name__ == '__main__':
    main()
