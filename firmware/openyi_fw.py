#!/usr/bin/env python3
"""OpenYI firmware workshop: build, mandatory verification, inspect, then flash.

Run without arguments for the interactive interface. Building never uses a camera.
"""
from __future__ import annotations

import argparse
from datetime import datetime
import ftplib
import getpass
import json
import os
from pathlib import Path
import subprocess
import sys

HOME = Path(__file__).resolve().parent
sys.path.insert(0, str(HOME / 'tools'))
from workshop import build, verify, read_profile, digest, require


def linux_path(path):
    value = str(Path(path).resolve())
    result = subprocess.run(['wsl.exe', '-d', 'Ubuntu', '--exec', 'wslpath', '-a', value],
                            capture_output=True, text=True, check=True)
    return result.stdout.strip()


def engine(action, output, source=None, profile='ak3918e-local01'):
    if os.name == 'nt':
        argv = ['wsl.exe', '-d', 'Ubuntu', '--exec', 'fakeroot', '--', 'python3', '-B',
                linux_path(__file__), '--engine', action, '--output', linux_path(output)]
        if source:
            argv.extend(['--source', linux_path(source), '--profile', profile])
        subprocess.run(argv, check=True)
    elif not os.environ.get('FAKEROOTKEY'):
        argv = ['fakeroot', '--', sys.executable, '-B', str(Path(__file__).resolve()),
                '--engine', action, '--output', str(output)]
        if source:
            argv.extend(['--source', str(source), '--profile', profile])
        subprocess.run(argv, check=True)
    elif action == 'build':
        build(Path(source), output, profile)
    else:
        verify(output)


def inspect(output):
    engine('verify', output)
    report = json.loads((output / 'verification.json').read_text(encoding='utf-8'))
    profile = read_profile(report['profile'])
    print('\n' + profile['label'])
    print(profile['qualification'])
    print(f"Application partition B: {report['filesystem_bytes']:,} / {report['capacity']:,} bytes")
    print('Image SHA-256: ' + digest((output / 'update.tar').read_bytes()))
    print('Changed files: ' + ', '.join(report['changed_files']))
    print('\nInspect this directory: ' + str(output.resolve()))
    print('  update.tar        Final installer image (not home.bin)')
    print('  usr.sqsh4         Rebuilt application filesystem')
    print('  files/            Extracted regular files for inspection')
    print('  contents.json     Complete file/link/ownership/mode/time inventory')
    print('  patches.txt       Exact before/after bytes and assembly')
    print('  verification.json + SHA256SUMS')
    if profile.get('offline_checks'):
        print('  execution-checks.json  Mandatory ARM/key/auth/media/network checks')
    print('\nOffline verification passed. Flashing is a separate action.')
    (HOME / '.local').mkdir(exist_ok=True)
    (HOME / '.local' / 'last-build.json').write_text(json.dumps({'path': str(output.resolve())}), encoding='utf-8')
    return profile


def ask_path(prompt, default=None):
    value = input(prompt + (f' [{default}]' if default else '') + ': ').strip().strip('"')
    require(value or default, 'A path is required')
    return Path(value or default).expanduser().resolve()


def install(output):
    from wifi_install import flash, lan_host
    profile = inspect(output)
    host = lan_host(input('\nCamera LAN address: ').strip())
    password = getpass.getpass('Camera root FTP password (not saved): ')
    packet = (output / 'update.tar').read_bytes()
    session = HOME / '.local' / 'installations' / datetime.now().strftime('%Y%m%d-%H%M%S-%f')
    def reverify():
        engine('verify', output)
        return (output / 'update.tar').read_bytes()
    def confirm(identity, sha, directory):
        print(f"\nCamera: {host} / {identity['mac']} / {identity['firmware']}")
        print('Exact image: ' + sha)
        print('Fresh backups and the exact RAM installer are available in: ' + str(directory))
        print('This experimental flash changes B and reboots the camera. Bootloader, kernel, identity and ISP calibration are preserved by this installer.')
        print('No boot/recovery qualification is implied by passing checksums. Keep power connected.')
        phrase = 'FLASH ' + sha[:12]
        return input(f'Type {phrase} to commit, or Enter to cancel: ').strip() == phrase
    flash(host, password, session, profile, packet, confirm, reverify)


def interactive():
    current = None
    try:
        saved = json.loads((HOME / '.local' / 'last-build.json').read_text(encoding='utf-8'))
        candidate = Path(saved['path'])
        if (candidate / 'verification.json').is_file():
            current = candidate
    except (OSError, ValueError, KeyError, TypeError):
        pass
    print('OpenYI firmware workshop')
    print('Build -> mandatory verification -> inspection -> explicit Wi-Fi flash')
    while True:
        print('\n1  Build and verify\n2  Inspect / reverify a build\n3  Flash an inspected build over Wi-Fi\n4  Export local01 camera key for OpenYI (Windows)\n5  Save local setup QR (local01/local02; experimental)\n0  Exit')
        choice = input('Choose: ').strip()
        if choice == '0':
            return
        try:
            if choice == '1':
                print('Profile: ak3918e-local01 — local key, cloud removal; hardware testing pending.')
                print('Optional ak3918e-local02: keyless plaintext LAN; matching client mode required.')
                profile_id = input('Profile [ak3918e-local01]: ').strip() or 'ak3918e-local01'
                require(profile_id in ('ak3918e-local01', 'ak3918e-local02'), 'Choose local01 or local02 by full profile name')
                source = ask_path('Original application SquashFS', HOME / '.local' / 'inputs' / 'usr-installed.squashfs')
                stamp = datetime.now().strftime('%Y%m%d-%H%M%S-%f')
                output = ask_path('New build directory', HOME / 'build' / stamp)
                engine('build', output, source, profile_id)
                current = output
                inspect(current)
            elif choice in ('2', '3'):
                current = ask_path('Build directory', current)
                if choice == '2':
                    inspect(current)
                else:
                    install(current)
            elif choice == '4':
                from wifi_install import connect, lan_host
                from local_pairing import read_owner_profile, save_windows_profile
                require(os.name == 'nt', 'This export uses Windows encrypted pairing files')
                host = lan_host(input('Camera LAN address: ').strip())
                name = input('Camera name: ').strip() or 'OpenYI Camera'
                stamp = datetime.now().strftime('%Y%m%d-%H%M%S-%f')
                target = ask_path('New encrypted pairing file', HOME / '.local' / ('camera-' + stamp + '.dpapi'))
                require(not target.exists(), 'Choose a new export filename')
                password = getpass.getpass('Camera root FTP password (not saved): ')
                ftp = connect(host, password)
                try:
                    device = read_owner_profile(ftp, host, name)
                finally:
                    ftp.close()
                save_windows_profile(device, target)
                print('Saved encrypted pairing file: ' + str(target))
                print('In OpenYI Camera setup, choose Import encrypted pairing profile. The key was not printed or changed.')
            elif choice == '5':
                from local_setup import setup_payload, save_setup_qr
                print('For patched local01/local02. Local01 QR/reset setup passed on one camera with Internet available; local02 is untested on hardware.')
                print('This creates a file offline; it does not reset or contact the camera.')
                ssid = input('Wi-Fi network name (SSID): ')
                password = getpass.getpass('Wi-Fi password (empty for an open network): ')
                region = input('Region EU / US / CN [EU]: ').strip().upper() or 'EU'
                payload = setup_payload(ssid, password, region)
                stamp = datetime.now().strftime('%Y%m%d-%H%M%S-%f')
                target = ask_path('New QR image', HOME / '.local' / ('setup-' + stamp + '.png'))
                save_setup_qr(payload, target)
                print('Saved and decode-verified: ' + str(target))
                print('The image contains recoverable Wi-Fi credentials. Display it on your phone when testing camera setup.')
                print('Local01: export the local camera key and import it in OpenYI.')
                print('Local02: select keyless LAN mode in OpenYI Camera setup and verify/save its LAN address.')
        except (ValueError, OSError, ftplib.Error, subprocess.SubprocessError) as error:
            print('\nStopped: ' + str(error))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build', type=Path, metavar='SOURCE_SQUASHFS')
    parser.add_argument('--inspect', type=Path, metavar='BUILD_DIRECTORY')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--profile', default='ak3918e-local01')
    parser.add_argument('--engine', choices=('build', 'verify'), help=argparse.SUPPRESS)
    parser.add_argument('--source', type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.engine:
        require(args.output is not None, 'Output required')
        if args.engine == 'build':
            build(args.source, args.output, args.profile)
        else:
            verify(args.output)
    elif args.build:
        require(args.output is not None, '--output must be a new directory')
        engine('build', args.output.resolve(), args.build.resolve(), args.profile)
        inspect(args.output.resolve())
    elif args.inspect:
        inspect(args.inspect.resolve())
    else:
        interactive()


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, ftplib.Error, subprocess.SubprocessError) as error:
        raise SystemExit('Stopped: ' + str(error)) from None
    except (KeyboardInterrupt, EOFError):
        raise SystemExit('\nStopped. A committed flash must not be power-interrupted.') from None
