"""Internal offline build/verification engine for openyi_fw.py. Never uses a camera."""
from __future__ import annotations

import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import struct
import subprocess
import sys
import tarfile
import tempfile

from inspect_update import inspect_bytes

HOME = Path(__file__).resolve().parents[1]
CAPACITY = 3_100_672  # This board's B partition, not the whole flash.


def execution_checks(tree, profile):
    if 'offline_checks' not in profile:
        return None
    require(profile['offline_checks'] in ('local01', 'local02'), 'Unknown mandatory execution checks')
    # An optional per-repository Linux wheel directory supports WSL without
    # altering system Python. Never load these Linux wheels in Windows.
    runtime = HOME / '.local' / 'python-runtime'
    if os.name == 'posix' and runtime.is_dir():
        sys.path.insert(0, str(runtime))
    try:
        from verify_local import verify_tree
    except ImportError as error:
        raise ValueError('Mandatory ARM verification dependencies are missing; install firmware/requirements-analysis.txt in the build Python environment') from error
    return verify_tree(tree, profile)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def json_bytes(value):
    return (json.dumps(value, indent=2, sort_keys=True) + "\n").encode()


def read_profile(name):
    require(name in {p.stem for p in (HOME / 'profiles').glob('*.json')}, 'Unknown patch profile')
    return json.loads((HOME / 'profiles' / (name + '.json')).read_text(encoding='utf-8'))


def safe_name(name):
    p = PurePosixPath(name)
    require(name and not p.is_absolute() and '\\' not in name and ':' not in name
            and all(part not in ('', '.', '..') for part in name.split('/')), 'Unsafe relative path')
    return p


def arm_offset(data, address, length):
    require(data[:7] == b'\x7fELF\x01\x01\x01' and len(data) >= 52, 'Expected ELF32 little-endian')
    require(struct.unpack_from('<H', data, 18)[0] == 40, 'Expected ARM ELF')
    require(address % 4 == 0 and length % 4 == 0, 'A32 alignment required')
    phoff = struct.unpack_from('<I', data, 28)[0]
    entsize, count = struct.unpack_from('<HH', data, 42)
    require(entsize == 32 and 0 < count <= 64 and phoff + entsize * count <= len(data), 'Bad ELF program headers')
    for i in range(count):
        kind, offset, va, _, size, _, flags, _ = struct.unpack_from('<8I', data, phoff + i * entsize)
        relative = address - va
        if kind == 1 and flags & 1 and 0 <= relative and relative + length <= size:
            require(offset + relative + length <= len(data), 'Truncated executable segment')
            return offset + relative
    raise ValueError('Patch is outside a file-backed executable segment')


def patched_binary(data, spec):
    require(digest(data) == spec['source_sha256'], 'Binary source SHA-256 mismatch')
    changes, occupied = [], set()
    for patch in spec['patches']:
        before, after = bytes.fromhex(patch['before']), bytes.fromhex(patch['after'])
        require(patch['mode'] == 'A32' and before and len(before) == len(after), 'Invalid equal-size A32 patch')
        offset = arm_offset(data, int(patch['address'], 0), len(before))
        require(offset == int(patch['offset'], 0), 'VA/file-offset disagreement')
        area = set(range(offset, offset + len(before)))
        require(not (area & occupied), 'Overlapping patches')
        occupied |= area
        require(data[offset:offset + len(before)] == before, 'Original patch bytes mismatch')
        start = int(patch['context_offset'], 0)
        context = bytes.fromhex(patch['context'])
        require(start >= 0 and data[start:start + len(context)] == context, 'Patch context mismatch')
        changes.append((offset, after))
    result = bytearray(data)
    for offset, after in changes:
        result[offset:offset + len(after)] = after
    require(digest(result) == spec['result_sha256'], 'Patched binary SHA-256 mismatch')
    return bytes(result)


def package(filesystem, version, epoch):
    require(96 <= len(filesystem) <= CAPACITY, 'Application image exceeds B partition capacity')
    content = {'usr.sqsh4': filesystem, 'fw_version': version.encode('ascii') + b'\n',
               'usr.sqsh4.md5': (hashlib.md5(filesystem).hexdigest() + '  usr.sqsh4\n').encode()}
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode='w', format=tarfile.USTAR_FORMAT) as tar:
        for name, data in content.items():
            entry = tarfile.TarInfo(name)
            entry.size, entry.mtime, entry.mode = len(data), epoch, 0o644
            entry.uid = entry.gid = 0
            tar.addfile(entry, io.BytesIO(data))
    result = output.getvalue()
    inspect_bytes(result)
    return result


def checked_package(data, profile):
    report, files = inspect_bytes(data)
    require(report['version'] == profile['target_version'], 'Wrong target version')
    fs = files['usr.sqsh4']
    require(len(fs) <= CAPACITY, 'Application image exceeds B partition capacity')
    used = report['filesystem']['bytes_used']
    require(not any(fs[used:]), 'Nonzero SquashFS padding')
    require(data == package(fs, profile['target_version'], profile['epoch']), 'Noncanonical TAR, header or trailing data')
    return report, files


def run(argv):
    env = dict(os.environ)
    env.pop('SOURCE_DATE_EPOCH', None)  # Do not silently clamp original file times.
    env['LC_ALL'] = 'C'
    result = subprocess.run(argv, capture_output=True, text=True, env=env)
    if result.returncode:
        raise ValueError(f'{Path(argv[0]).name} failed: {result.stderr[-1800:]} {result.stdout[-600:]}')
    return result.stdout + result.stderr


def linux_required():
    require(os.name == 'posix' and os.environ.get('FAKEROOTKEY'), 'Build/verify must run inside Linux fakeroot')
    for name in ('mksquashfs', 'unsquashfs'):
        require(shutil.which(name), f'Install squashfs-tools ({name} missing)')


def tool_version(name):
    # unsquashfs 4.6.1 prints its version successfully but exits with status 1.
    result = subprocess.run([name, '-version'], capture_output=True, text=True)
    text = result.stdout + result.stderr
    require(text.startswith(name + ' version '), 'Unrecognized tool version response')
    return text.splitlines()[0]


def extract(image, directory):
    # Input is hash-pinned before extraction. Use Linux temporary storage, never NTFS.
    run(['unsquashfs', '-no-progress', '-processors', '1', '-d', str(directory), str(image)])


def inventory(root):
    entries, links = {}, {}
    paths = [root] + sorted(root.rglob('*'))
    for path in paths:
        info = path.lstat()
        name = '.' if path == root else path.relative_to(root).as_posix()
        mode = info.st_mode
        row = {'mode': stat.S_IMODE(mode), 'uid': info.st_uid, 'gid': info.st_gid,
               'mtime': int(info.st_mtime), 'xattrs': {}}
        for key in sorted(os.listxattr(path, follow_symlinks=False)):
            row['xattrs'][key] = os.getxattr(path, key, follow_symlinks=False).hex()
        if stat.S_ISLNK(mode):
            row.update(kind='link', target=os.readlink(path))
        elif stat.S_ISDIR(mode):
            row.update(kind='directory')
        elif stat.S_ISREG(mode):
            row.update(kind='file', size=info.st_size, sha256=digest(path.read_bytes()))
            links.setdefault((info.st_dev, info.st_ino), []).append(name)
        else:
            raise ValueError('Unexpected special filesystem entry: ' + name)
        entries[name] = row
    for group in links.values():
        if len(group) > 1:
            for name in group:
                entries[name]['hardlinks'] = sorted(group)
    return entries


def replace_preserving_metadata(path, data):
    before = path.stat()
    require(stat.S_ISREG(path.lstat().st_mode) and before.st_nlink == 1, 'Patch target must be a unique regular file')
    path.write_bytes(data)
    os.utime(path, ns=(before.st_atime_ns, before.st_mtime_ns))


def prepare(source, destination, profile):
    extract(source, destination)
    before = inventory(destination)
    version = destination / 'fw_version'
    require(version.read_bytes() == profile['source_version'].encode() + b'\n', 'Installed filesystem version mismatch')
    changed = {'fw_version'}
    for spec in profile['binaries']:
        relative = safe_name(spec['path'])
        require(relative.as_posix() not in changed, 'Duplicate patch target')
        changed.add(relative.as_posix())
        target = destination.joinpath(*relative.parts)
        # A parent symlink would escape the extraction root.
        require(target.resolve().is_relative_to(destination.resolve()), 'Patch target escapes filesystem')
        replace_preserving_metadata(target, patched_binary(target.read_bytes(), spec))
    replace_preserving_metadata(version, profile['target_version'].encode() + b'\n')
    after = inventory(destination)
    require(set(before) == set(after), 'Unexpected filesystem entries changed')
    for name in before:
        allowed = {'sha256', 'size'} if name in changed else set()
        require({k: v for k, v in before[name].items() if k not in allowed} ==
                {k: v for k, v in after[name].items() if k not in allowed}, 'Metadata/content drift: ' + name)
    require({name for name in before if before[name] != after[name]} == changed, 'Unexpected changed-file set')
    return before, after


def compile_one(source, work, profile):
    tree, image, unpacked = work / 'source', work / 'usr.sqsh4', work / 'roundtrip'
    before, expected = prepare(source, tree, profile)
    run(['mksquashfs', str(tree), str(image), '-comp', 'xz', '-b', '131072',
         '-processors', '1', '-noappend', '-no-recovery', '-no-progress',
         '-mkfs-time', str(profile['epoch'])])
    require(image.stat().st_size <= CAPACITY, 'Rebuilt filesystem exceeds B partition capacity')
    extract(image, unpacked)
    require(inventory(unpacked) == expected, 'Rebuilt filesystem round-trip mismatch (content or metadata)')
    return image.read_bytes(), before, expected, tree


def patch_report(profile):
    lines = [profile['label'], profile['qualification'], '']
    for spec in profile['binaries']:
        lines.extend([spec['path'], 'source SHA256: ' + spec['source_sha256'], 'result SHA256: ' + spec['result_sha256']])
        for p in spec['patches']:
            lines.extend([f"  {p['name']} ({p['mode']}) VA {p['address']}, file {p['offset']}",
                          '  - ' + p['before'], '  + ' + p['after'], '  ' + p['assembly'], '  ' + p['reason']])
    lines.extend(['', 'fw_version: ' + profile['source_version'] + ' -> ' + profile['target_version'],
                  'Only the listed bytes and fw_version may change. Boot/hardware behavior is not established.'])
    return '\n'.join(lines) + '\n'


def save_inspection_files(tree, output, expected):
    # Materialize regular files only for Windows browsing; symlinks/metadata are in contents.json.
    for name, row in expected.items():
        if row['kind'] == 'file':
            target = output / 'files' / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((tree / name).read_bytes())


def save_receipt(output, report):
    (output / 'verification.json').write_bytes(json_bytes(report))
    lines = [f'{value}  {name}' for name, value in sorted(report['sha256'].items())]
    lines.append(f"{digest((output / 'verification.json').read_bytes())}  verification.json")
    (output / 'SHA256SUMS').write_text('\n'.join(lines) + '\n', encoding='ascii', newline='\n')


def build(source, output, profile_name):
    linux_required()
    profile = read_profile(profile_name)
    data = source.read_bytes()
    require(digest(data) == profile['source_squashfs_sha256'], 'Source filesystem SHA-256 mismatch')
    require(not output.exists(), 'Output directory already exists; choose a new build directory')
    output.mkdir(parents=True)
    (output / 'source.squashfs').write_bytes(data)
    (output / 'profile.json').write_bytes(json_bytes(profile))
    with tempfile.TemporaryDirectory(prefix='openyi-fw-') as directory:
        work = Path(directory)
        copies = []
        for number in (1, 2):
            print(f'Build {number}/2: patch, repack, fully extract and compare...', flush=True)
            stage = work / str(number)
            stage.mkdir()
            image, original, contents, tree = compile_one(output / 'source.squashfs', stage, profile)
            copies.append(package(image, profile['target_version'], profile['epoch']))
        require(copies[0] == copies[1], 'Independent builds are not byte-identical')
        print('Mandatory ARM execution and source assembly checks...', flush=True)
        checks = execution_checks(tree, profile)
        if checks is not None:
            (output / 'execution-checks.json').write_bytes(json_bytes(checks))
        (output / 'update.tar').write_bytes(copies[0])
        (output / 'usr.sqsh4').write_bytes(image)
        (output / 'contents.json').write_bytes(json_bytes(contents))
        (output / 'patches.txt').write_text(patch_report(profile), encoding='utf-8', newline='\n')
        save_inspection_files(tree, output, contents)
    hashes = {path.relative_to(output).as_posix(): digest(path.read_bytes())
              for path in sorted(output.rglob('*')) if path.is_file()}
    report = {'schema': 1, 'profile': profile_name, 'offline_verified': False,
              'hardware_tested': False, 'two_identical_builds': [digest(c) for c in copies],
              'partition': 'B', 'capacity': CAPACITY, 'filesystem_bytes': len(image),
              'changed_files': [n for n in original if original[n] != contents[n]],
              'tool_versions': {n: tool_version(n) for n in ('mksquashfs', 'unsquashfs')},
              'sha256': hashes}
    save_receipt(output, report)
    print('Mandatory independent verification pass...', flush=True)
    verify(output)
    report['offline_verified'] = True
    save_receipt(output, report)
    print('OFFLINE VERIFIED. Paused for inspection. No camera contacted.', flush=True)


def verify(output):
    linux_required()
    report = json.loads((output / 'verification.json').read_text(encoding='utf-8'))
    # Never trust a saved "passed" flag. All content checks run again, including
    # during the build's mandatory pass before its success flag can be written.
    require(report['schema'] == 1 and type(report['offline_verified']) is bool, 'Invalid verification record')
    profile = read_profile(report['profile'])
    require((output / 'profile.json').read_bytes() == json_bytes(profile), 'Build profile differs from reviewed repository profile')
    for name, wanted in report['sha256'].items():
        relative = safe_name(name)
        path = output.joinpath(*relative.parts)
        require(not path.is_symlink() and path.resolve().is_relative_to(output.resolve()), 'Artifact path escapes build')
        require(digest(path.read_bytes()) == wanted, 'Artifact SHA-256 mismatch: ' + name)
    require(digest((output / 'source.squashfs').read_bytes()) == profile['source_squashfs_sha256'], 'Source changed')
    packet = (output / 'update.tar').read_bytes()
    require(report['two_identical_builds'] == [digest(packet)] * 2, 'Reproducibility record mismatch')
    _, files = checked_package(packet, profile)
    require(files['usr.sqsh4'] == (output / 'usr.sqsh4').read_bytes(), 'Package and loose filesystem disagree')
    expected_sums = [f'{value}  {name}' for name, value in sorted(report['sha256'].items())]
    expected_sums.append(f"{digest((output / 'verification.json').read_bytes())}  verification.json")
    require((output / 'SHA256SUMS').read_text(encoding='ascii') == '\n'.join(expected_sums) + '\n', 'SHA256SUMS mismatch')
    with tempfile.TemporaryDirectory(prefix='openyi-verify-') as directory:
        work = Path(directory)
        _, expected = prepare(output / 'source.squashfs', work / 'expected', profile)
        extract(output / 'usr.sqsh4', work / 'actual')
        require(inventory(work / 'actual') == expected, 'Final image differs from independently patched source')
        checks = execution_checks(work / 'actual', profile)
        if checks is not None:
            require((output / 'execution-checks.json').read_bytes() == json_bytes(checks), 'Execution verification record differs from rerun')
        require((output / 'contents.json').read_bytes() == json_bytes(expected), 'Inspection manifest differs from image')
        for name, row in expected.items():
            if row['kind'] == 'file':
                require(digest((output / 'files' / name).read_bytes()) == row['sha256'], 'Inspection file differs: ' + name)
    require((output / 'patches.txt').read_text(encoding='utf-8') == patch_report(profile), 'Patch report mismatch')
    return report
