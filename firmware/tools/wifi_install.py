"""Internal exact-build installer. Build/inspection never import or call it.

A temporary change to one authenticated FTP worker launches a fixed RAM script.
The handler is restored before a separate commit token permits a flash. This is
not a generic command runner and is qualified only for the pinned BusyBox build.
"""
from __future__ import annotations

import ftplib
import hashlib
import io
import ipaddress
from pathlib import Path
import re
import secrets
import struct
import time
import zlib

import dump_flash_ftp as raw
from workshop import checked_package, digest, json_bytes, require

UPDATER_SHA = '9af22977a03eabd73aab6a188c360c48ecfc7a48b0659974f54b7e2fd395e86d'
SCRIPT_SHA = 'cdc61349a0db8b7deedd9e7ab14574c1fd31874f28bf9ad01362c87007beeb6d'
STAT_ADDRESS = 0x1DB54
STAT_ORIGINAL = bytes.fromhex('9c079f05a901000a0300a0e316feffeb55ffffea')
COMMAND_ADDRESS = 0xA35E8
COMMAND = b'/bin/sh /tmp/openyi-run.sh </dev/null &\0'
STAGED = ('/tmp/update.tar', '/tmp/openyi-run.sh', '/tmp/openyi-update.sh',
          '/tmp/openyi-go', '/tmp/openyi-go.pending', '/tmp/openyi-ready', '/tmp/openyi-flash.log')
FORBIDDEN = ('/mnt/update/update.tar', '/tmp/uImage', '/tmp/root.sqsh4', '/tmp/usr.jffs2',
             '/tmp/audio_update.tgz', '/tmp/special.sh', '/tmp/usr.sqsh4',
             '/tmp/fw_version', '/tmp/usr.sqsh4.md5')


def lan_host(value):
    address = ipaddress.IPv4Address(value)
    require(any(address in ipaddress.IPv4Network(n) for n in
                ('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')), 'Enter a private IPv4 camera address')
    return str(address)


def connect(host, password):
    ftp = ftplib.FTP(timeout=30)
    try:
        ftp.connect(lan_host(host), 21)
        ftp.login('root', password)
        return ftp
    except BaseException:
        ftp.close()
        raise


def get(ftp, path, limit=1024 * 1024):
    return raw.read_bounded(ftp, path, limit)[0]


def absent(ftp, path):
    # SIZE would miss dangling symlinks. The pinned BusyBox returns an empty LIST.
    rows = []
    ftp.retrlines('LIST ' + path, rows.append)
    return not rows


def preflight(ftp, profile):
    require(get(ftp, '/usr/fw_version').decode().strip() == profile['source_version'], 'Camera firmware differs from source')
    require(raw.BOARD in get(ftp, '/proc/cpuinfo').decode(), 'Unqualified camera board')
    require(get(ftp, '/sys/class/mtd/mtd0/type').strip() == b'nor', 'Not NOR flash')
    require(int(get(ftp, '/sys/class/mtd/mtd0/size')) == raw.FLASH_BYTES, 'Wrong flash capacity')
    require(re.search(r'^mtd5: 002f5000 00001000 "B"$', get(ftp, '/proc/mtd').decode(), re.M), 'Wrong B partition map')
    busybox = get(ftp, '/bin/busybox')
    require(digest(busybox) == raw.BUSYBOX_SHA256, 'Unqualified BusyBox build')
    require(digest(get(ftp, '/sbin/updater')) == UPDATER_SHA, 'Unqualified native updater')
    script = get(ftp, '/usr/sbin/update.sh')
    require(digest(script) == SCRIPT_SHA, 'Unqualified installed update script')
    for spec in profile['binaries']:
        require(digest(get(ftp, '/usr/' + spec['path'])) == spec['source_sha256'], 'Live binary differs from source')
    for path in STAGED + FORBIDDEN:
        require(absent(ftp, path), 'An update/staging file already exists: ' + path)
    mac = get(ftp, '/sys/class/net/wlan0/address').decode().strip()
    require(re.fullmatch(r'(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}', mac), 'Cannot identify Wi-Fi interface')
    fields = {k: int(v) for k, v in re.findall(r'^(MemFree|Buffers|Cached):\s+(\d+) kB$', get(ftp, '/proc/meminfo').decode(), re.M)}
    require(sum(fields.values()) >= 16 * 1024, 'Insufficient free/reclaimable RAM for staging')
    return {'mac': mac, 'firmware': profile['source_version'], 'board': raw.BOARD}, busybox, script


def branch(address, target, link=False):
    delta = target - address - 8
    require(delta % 4 == 0 and -(1 << 25) <= delta < (1 << 25), 'A32 branch out of range')
    return struct.pack('<I', (0xEB000000 if link else 0xEA000000) | ((delta // 4) & 0xFFFFFF))


def stat_hook():
    return (bytes.fromhex('9c079fe5') + branch(0x1DB58, 0xBC94, True) +
            bytes.fromhex('1c079fe5') + branch(0x1DB60, 0x1CB00, True) + branch(0x1DB64, 0x1D8C0))


def read_memory(ftp, start, length):
    data, count = bytearray(), 0
    def receive(chunk):
        nonlocal count
        count += len(chunk)
        require(count <= raw.MEMORY_END - start, 'Unexpected worker memory map')
        data.extend(chunk[:max(0, length - len(data))])
    try:
        ftp.retrbinary('RETR /proc/self/mem', receive, blocksize=65536, rest=start)
    except ftplib.error_temp as error:
        require(str(error) == '451 Error' and count == raw.MEMORY_END - start, 'Unexpected memory read termination')
    else:
        raise ValueError('Expected bounded worker mapping termination')
    require(len(data) == length, 'Short worker memory read')
    return bytes(data)


def write_memory(ftp, address, data):
    ftp.storbinary('STOR /proc/self/mem', io.BytesIO(data), rest=address)
    require(read_memory(ftp, address, len(data)) == data, 'Worker memory write did not verify')


def launch_waiting_script(ftp, busybox):
    require(digest(busybox) == raw.BUSYBOX_SHA256, 'Unqualified worker binary')
    raw.validate_maps(get(ftp, '/proc/self/maps').decode())
    require(re.search(r'^Uid:\s+0\s+0\s+0\s+0\s*$', get(ftp, '/proc/self/status').decode(), re.M), 'Root worker required')
    require(get(ftp, '/proc/self/cmdline').split(b'\0')[0].rsplit(b'/', 1)[-1] == b'ftpd', 'Not an isolated FTP worker')
    original = busybox[STAT_ADDRESS - 0x8000:STAT_ADDRESS - 0x8000 + len(STAT_ORIGINAL)]
    string = busybox[COMMAND_ADDRESS - 0x8000:COMMAND_ADDRESS - 0x8000 + len(COMMAND)]
    require(original == STAT_ORIGINAL, 'Unexpected stock STAT instructions')
    require(read_memory(ftp, STAT_ADDRESS, len(original)) == original and
            read_memory(ftp, COMMAND_ADDRESS, len(string)) == string, 'Runtime worker differs from stock')
    try:
        write_memory(ftp, COMMAND_ADDRESS, COMMAND)
        write_memory(ftp, STAT_ADDRESS, stat_hook())
        require(ftp.sendcmd('STAT').startswith('200 '), 'RAM launcher did not acknowledge')
    finally:
        try:
            write_memory(ftp, STAT_ADDRESS, original)
        finally:
            write_memory(ftp, COMMAND_ADDRESS, string)


def upload(ftp, path, data):
    require(absent(ftp, path), 'Refusing to overwrite camera file: ' + path)
    ftp.storbinary('STOR ' + path, io.BytesIO(data), blocksize=65536)
    require(get(ftp, path, len(data) + 1) == data, 'Upload readback differs: ' + path)


def replace_once(data, before, after):
    require(data.count(before) == 1, 'Update-script anchor mismatch')
    return data.replace(before, after, 1)


def ram_updater(script, package_md5, filesystem_md5):
    require(digest(script) == SCRIPT_SHA, 'Unknown updater script')
    require(all(re.fullmatch('[0-9a-f]{32}', s) for s in (package_md5, filesystem_md5)), 'Bad checksum')
    script = replace_once(script, b'\nupdate_ispconfig\n', b'\n# OpenYI: preserve unit ISP calibration.\n')
    script = replace_once(script, b'\nupdate_kernel\nupdate_jffs2\nupdate_squash\nupdate_rootfs_squash\n', b'\nupdate_squash\n')
    script = replace_once(script, b'\t\tupdater local B=${DIR1}/${VAR3}', b'\t\tupdater local B=${DIR1}/${VAR3} || exit 91')
    block = b'\nif [ -d /data ];then\n    update_audio\n    if [ -f $DIR1/special.sh ];then\n        $DIR1/special.sh\n    fi\nfi\n'
    script = replace_once(script, block, b'\n# OpenYI: no audio partition or special script dispatch.\n')
    check = (f'\n[ "$(md5sum /tmp/update.tar | cut -d " " -f 1)" = "{package_md5}" ] || exit 92\n'
             f'[ "$(md5sum /tmp/usr.sqsh4 | cut -d " " -f 1)" = "{filesystem_md5}" ] || exit 93\n').encode()
    return replace_once(script, b'\nkillall -15 syslogd\n', check + b'\nkillall -15 syslogd\n')


def launcher(token, package_md5, source_version):
    require(re.fullmatch('[0-9a-f]{64}', token), 'Bad commit token')
    require(re.fullmatch('[0-9a-f]{32}', package_md5), 'Bad package digest')
    require(re.fullmatch('[0-9._A-Za-z-]+', source_version), 'Bad version')
    return f'''#!/bin/sh
exec >/tmp/openyi-flash.log 2>&1
umask 077
echo READY > /tmp/openyi-ready
i=0
while [ ! -f /tmp/openyi-go ]; do
    sleep 1
    i=$((i+1))
    [ "$i" -lt 180 ] || exit 80
done
[ "$(cat /tmp/openyi-go)" = "{token}" ] || exit 81
[ "$(cat /usr/fw_version)" = "{source_version}" ] || exit 82
[ "$(md5sum /tmp/update.tar | cut -d ' ' -f 1)" = "{package_md5}" ] || exit 83
for p in {' '.join(FORBIDDEN)}; do
    [ ! -e "$p" ] && [ ! -L "$p" ] || exit 84
done
exec /bin/sh /tmp/openyi-update.sh
'''.encode()


def validate_backup(directory, profile):
    first = (directory / 'mtd0-full-pass1.bin').read_bytes()
    second = (directory / 'mtd0-full-pass2.bin').read_bytes()
    require(len(first) == len(second) == raw.FLASH_BYTES, 'Incomplete fresh backup')
    require(first[:0x5A8000] == second[:0x5A8000] and first[0x5B8000:] == second[0x5B8000:],
            'Fresh backup changed outside mutable C partition')
    for data in (first, second):
        part = data[0x2B3000:0x5A8000]
        used = struct.unpack_from('<Q', part, 40)[0]
        require(digest(part[:used]) == profile['source_squashfs_sha256'], 'Flash backup differs from build source')
        header = bytearray(data[0x31000:0x31040])
        magic, hcrc, _, length, _, _, dcrc = struct.unpack_from('>7I', header)
        require(magic == 0x27051956 and 0 < length <= 0x180000 - 64, 'Invalid backup uImage')
        header[4:8] = b'\0' * 4
        require(zlib.crc32(header) == hcrc and zlib.crc32(data[0x31040:0x31040 + length]) == dcrc,
                'Backup kernel checksum mismatch')
    return first


def verify_flash_readback(ftp, busybox, expected_image, directory):
    """Read the complete NOR through the already audited temporary RETR gate."""
    require(digest(get(ftp, '/bin/busybox')) == raw.BUSYBOX_SHA256, 'Post-boot BusyBox changed')
    raw.validate_maps(get(ftp, '/proc/self/maps').decode())
    reference = raw.validate_reference(busybox)
    require(raw.read_code_window(ftp) == reference, 'Post-boot worker differs from reference')
    patched = bytearray(reference)
    offset = raw.PATCH_ADDRESS - raw.MEMORY_START
    patched[offset:offset + 4] = raw.PATCHED
    try:
        raw.write_worker_instruction(ftp, raw.PATCHED)
        require(raw.read_code_window(ftp) == bytes(patched), 'Readback gate did not verify')
        flash_bytes = get(ftp, '/dev/mtd0ro', raw.FLASH_BYTES + 1)
        require(len(flash_bytes) == raw.FLASH_BYTES, 'Short post-flash readback')
    finally:
        raw.write_worker_instruction(ftp, raw.ORIGINAL)
        require(raw.read_code_window(ftp) == reference, 'Readback worker restoration failed')
    (directory / 'post-flash.bin').write_bytes(flash_bytes)
    require(flash_bytes[0x2B3000:0x2B3000 + len(expected_image)] == expected_image,
            'Physical B payload differs from verified image')
    return digest(flash_bytes)


def flash(host, password, directory, profile, packet, confirm, reverify):
    """Reverify callback is mandatory; no force/skip or unattended flash option."""
    checked_package(packet, profile)
    require(reverify() == packet, 'Inspected image changed')
    directory.mkdir(parents=True, exist_ok=False)
    report = {'schema': 1, 'image_sha256': digest(packet), 'flash_committed': False}
    def save(phase):
        report['phase'] = phase
        (directory / 'installation.json').write_bytes(json_bytes(report))
        print(phase, flush=True)
    ftp, owned = None, []
    try:
        ftp = connect(host, password)
        identity, busybox, original_script = preflight(ftp, profile)
        report['camera'] = identity
        save('Backing up current flash twice; no persistent camera changes')
        raw.acquire(ftp, directory / 'backup', busybox)
        ftp = None
        validate_backup(directory / 'backup', profile)
        ftp = connect(host, password)
        again, _, _ = preflight(ftp, profile)
        require(again == identity, 'Camera identity changed')
        require(reverify() == packet, 'Inspected image changed')
        _, members = checked_package(packet, profile)
        pmd5, token = hashlib.md5(packet).hexdigest(), secrets.token_hex(32)
        update = ram_updater(original_script, pmd5, hashlib.md5(members['usr.sqsh4']).hexdigest())
        waiting = launcher(token, pmd5, profile['source_version'])
        (directory / 'ram-update.sh').write_bytes(update)
        (directory / 'ram-launcher.sh').write_bytes(waiting)
        save('Uploading verified image and RAM installer; reading every byte back')
        for path, data in (('/tmp/update.tar', packet), ('/tmp/openyi-update.sh', update), ('/tmp/openyi-run.sh', waiting)):
            require(absent(ftp, path), 'Staging target appeared: ' + path)
            owned.append(path)
            upload(ftp, path, data)
        require(reverify() == packet, 'Inspected image changed during staging')
        if not confirm(identity, digest(packet), directory):
            save('Cancelled before launch; no flash command sent')
            return report
        # Confirmation can take arbitrarily long; re-read all staged data afterwards.
        require(reverify() == packet, 'Inspected image changed during review')
        for path, data in (('/tmp/update.tar', packet), ('/tmp/openyi-update.sh', update), ('/tmp/openyi-run.sh', waiting)):
            require(get(ftp, path, len(data) + 1) == data, 'Staged bytes changed during review')
        require(absent(ftp, '/tmp/openyi-go'), 'Unexpected commit token already present')
        launch_waiting_script(ftp, busybox)
        save('RAM worker restored; waiting for installer readiness')
        for _ in range(15):
            if not absent(ftp, '/tmp/openyi-ready') and get(ftp, '/tmp/openyi-ready') == b'READY\n':
                break
            time.sleep(1)
        else:
            raise ValueError('RAM installer not ready; no flash command sent')
        owned.append('/tmp/openyi-go.pending')
        upload(ftp, '/tmp/openyi-go.pending', token.encode())
        report['flash_committed'] = True  # Set before sending: ambiguous errors must never cause retry.
        save('Committing flash once; camera will disconnect and reboot. Keep power connected')
        ftp.rename('/tmp/openyi-go.pending', '/tmp/openyi-go')
        ftp.close(); ftp = None
        save('Waiting for reboot and installed-file verification')
        deadline = time.monotonic() + 240
        while time.monotonic() < deadline:
            time.sleep(5)
            try:
                ftp = connect(host, password)
                version = get(ftp, '/usr/fw_version').decode().strip()
                require(get(ftp, '/sys/class/net/wlan0/address').decode().strip() == identity['mac'], 'Different camera after reboot')
                if version != profile['target_version']:
                    ftp.close(); ftp = None
                    continue
                for spec in profile['binaries']:
                    require(digest(get(ftp, '/usr/' + spec['path'])) == spec['result_sha256'], 'Installed patch mismatch')
                report['installed_files_verified'] = True
                report['post_flash_sha256'] = verify_flash_readback(ftp, busybox, members['usr.sqsh4'], directory)
                report['B_payload_readback_verified'] = True
                save('Installed version, patch and full B payload verified; functional camera checks still required')
                return report
            except (OSError, EOFError, ftplib.Error):
                if ftp:
                    ftp.close(); ftp = None
        raise ValueError('No verified post-boot result; outcome unknown. Do not reflash automatically')
    except BaseException as error:
        report['error'] = type(error).__name__ + ': ' + str(error)
        save('Flash outcome uncertain: keep power connected; no automatic retry. Inspect installation.json'
             if report['flash_committed'] else 'Stopped before flash commit; inspect installation.json')
        raise
    finally:
        if ftp:
            if not report['flash_committed']:
                for path in owned:
                    try:
                        ftp.delete(path)
                    except (OSError, EOFError, ftplib.Error):
                        pass
            ftp.close()
