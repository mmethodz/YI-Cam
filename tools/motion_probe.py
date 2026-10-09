"""Observe local alert history without changing motion, tracking or recording settings."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import struct
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from yicam.credentials import load_device
from yicam.protocol import Camera


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profile", type=Path, help="Existing encrypted device profile")
    parser.add_argument("--seconds", type=int, default=90)
    args = parser.parse_args()
    if not 10 <= args.seconds <= 600:
        parser.error("Observation must be between 10 and 600 seconds.")
    profile = load_device(args.profile)
    started = time.monotonic()
    with Camera(profile["ip"], profile["password"], profile.get("uid")) as camera:
        camera.firmware()
        camera.start_video(1)
        while time.monotonic() - started < args.seconds:
            camera_time = int(time.time())
            while not camera.frames.empty():
                camera_time = camera.frames.get_nowait().seconds
            info = camera.device_info()
            reply = camera.command(0x5c06, struct.pack(">III", 0, max(0, camera_time - 600), min(0xffffffff, camera_time + 60)), response=0x5c07)
            data = reply.data
            if len(data) < 4:
                raise ValueError("Missing alert count")
            count = struct.unpack_from(">I", data)[0]
            if count > 2048 or len(data) != 4 + count * 12:
                raise ValueError("Invalid or truncated alert history")
            events = [dict(zip(("category", "started", "duration"), struct.unpack_from(">III", data, 4 + i * 12))) for i in range(count)]
            sample = {"elapsed": round(time.monotonic() - started, 1), "events": events}
            if len(info) > 93 and info[8] == 253:
                sample.update(sensitivity_byte=info[11], tracking=info[68], alarm_state_byte=info[93])
            print(json.dumps(sample), flush=True)
            time.sleep(2)


if __name__ == "__main__":
    main()
