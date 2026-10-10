"""Read a local01 owner's key and export an existing OpenYI pairing format.

This is an explicit, read-only LAN action. It never rotates a key or runs a
vendor app. Importing the result in OpenYI performs normal camera verification.
"""
from __future__ import annotations

import os
from pathlib import Path
import re
import struct
import sys

from workshop import HOME, digest, read_profile, require
from wifi_install import get, lan_host


def parse_key(data):
    require(len(data) == 16 and data[-1:] == b'\n' and all(33 <= c <= 126 for c in data[:15]),
            'The camera has no valid canonical OpenYI key; no profile was exported')
    return data[:15].decode('ascii')


def parse_uid(data):
    matches = re.findall(rb'^\s*p2pid\s*=\s*(\S+)\s*$', data, re.M)
    require(len(matches) == 1, 'Camera transport identity is missing or ambiguous')
    match = re.fullmatch(rb'(TNP[A-Za-z0-9]{0,5})-(\d{6,10})-([A-Za-z0-9]{1,8})', matches[0])
    require(match is not None, 'Unsupported camera transport identity')
    prefix, serial, suffix = match.groups()
    number = int(serial)
    require(number <= 0xffffffff and serial.decode() == f'{number:06d}', 'Invalid transport serial')
    return (prefix.ljust(8, b'\0') + struct.pack('>I', number) + suffix.ljust(8, b'\0')).hex()


def read_owner_profile(ftp, host, name):
    host = lan_host(host)
    require(0 < len(name.strip()) <= 100, 'Use a camera name of 1–100 characters')
    profile = read_profile('ak3918e-local01')
    require(get(ftp, '/usr/fw_version').decode().strip() == profile['target_version'],
            'This key export requires the local01 camera firmware')
    binary = next(s for s in profile['binaries'] if s['path'] == 'bin/anyka_ipc')
    require(digest(get(ftp, '/usr/bin/anyka_ipc', binary['source_size'] + 1)) == binary['result_sha256'],
            'Camera key-handling binary differs from the reviewed local01 profile')
    key = parse_key(get(ftp, '/etc/jffs2/openyi.key', 17))
    uid = parse_uid(get(ftp, '/etc/jffs2/yi.conf', 16384))
    return {'ip': host, 'name': name.strip(), 'uid': uid, 'password': key}


def save_windows_profile(device, path):
    require(os.name == 'nt', 'Encrypted file export uses Windows DPAPI; build/inspection still support Linux')
    path = Path(path)
    require(not path.exists(), 'Choose a new pairing export filename')
    sys.path.insert(0, str(HOME.parent))
    from yicam.credentials import _crypt
    import json
    encrypted = _crypt(json.dumps(device).encode('utf-8'), True)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('xb') as stream:
        stream.write(encrypted)
        stream.flush()
        os.fsync(stream.fileno())
    require(_crypt(path.read_bytes(), False) == json.dumps(device).encode('utf-8'), 'Encrypted export readback differs')
