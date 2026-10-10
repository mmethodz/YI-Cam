import copy
import hashlib
import io
from pathlib import Path
import struct
import sys
import tarfile
import tempfile
import unittest
from contextlib import ExitStack, redirect_stdout
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import workshop as w
import wifi_install as wifi
from test_inspection import filesystem


def binary_fixture():
    data = bytearray(256)
    data[:7] = b'\x7fELF\x01\x01\x01'
    struct.pack_into('<H', data, 18, 40)
    struct.pack_into('<I', data, 28, 52)
    struct.pack_into('<HH', data, 42, 32, 1)
    struct.pack_into('<8I', data, 52, 1, 0, 0x8000, 0, 256, 256, 5, 4096)
    data[128:136] = bytes.fromhex('0c002de9f0402de9')
    result = bytearray(data)
    result[128:136] = bytes.fromhex('0000a0e31eff2fe1')
    spec = {'source_sha256': w.digest(data), 'result_sha256': w.digest(result), 'patches': [
        {'mode': 'A32', 'address': '0x8080', 'offset': '0x80', 'before': data[128:136].hex(),
         'after': result[128:136].hex(), 'context_offset': '0x70', 'context': data[112:152].hex()}]}
    return bytes(data), bytes(result), spec


class PatchTests(unittest.TestCase):
    def test_exact_patch_and_only_intended_bytes(self):
        before, after, spec = binary_fixture()
        self.assertEqual(w.patched_binary(before, spec), after)

    def test_wrong_source_digest(self):
        before, _, spec = binary_fixture()
        with self.assertRaisesRegex(ValueError, 'source SHA'):
            w.patched_binary(before[:-1] + b'x', spec)

    def test_context_or_original_bytes_must_match(self):
        before, _, spec = binary_fixture()
        for key in ('before', 'context'):
            broken = copy.deepcopy(spec)
            broken['patches'][0][key] = 'ff' * (len(broken['patches'][0][key]) // 2)
            with self.assertRaises(ValueError):
                w.patched_binary(before, broken)

    def test_wrong_instruction_state_or_alignment(self):
        before, _, spec = binary_fixture()
        for key, value in [('mode', 'Thumb'), ('address', '0x8081'), ('offset', '0x84')]:
            broken = copy.deepcopy(spec); broken['patches'][0][key] = value
            with self.assertRaises(ValueError):
                w.patched_binary(before, broken)

    def test_overlapping_or_different_length_patches(self):
        before, _, spec = binary_fixture()
        spec['patches'].append(copy.deepcopy(spec['patches'][0]))
        with self.assertRaisesRegex(ValueError, 'Overlapping'):
            w.patched_binary(before, spec)
        spec['patches'].pop(); spec['patches'][0]['after'] = '00'
        with self.assertRaisesRegex(ValueError, 'equal-size'):
            w.patched_binary(before, spec)

    def test_unexpected_result_hash(self):
        before, _, spec = binary_fixture(); spec['result_sha256'] = '0' * 64
        with self.assertRaisesRegex(ValueError, 'Patched binary'):
            w.patched_binary(before, spec)

    def test_non_executable_segment_rejected(self):
        before, _, _ = binary_fixture()
        data = bytearray(before); struct.pack_into('<I', data, 52 + 24, 4)
        with self.assertRaisesRegex(ValueError, 'executable segment'):
            w.arm_offset(data, 0x8080, 8)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.profile = {'target_version': '6.0.24.10_test', 'epoch': 1704770025}
        self.data = w.package(filesystem(), self.profile['target_version'], self.profile['epoch'])

    def test_canonical_package_deterministic(self):
        self.assertEqual(self.data, w.package(filesystem(), self.profile['target_version'], self.profile['epoch']))
        report, _ = w.checked_package(self.data, self.profile)
        self.assertTrue(report['filesystem']['md5_matches'])

    def test_changed_outer_header_and_trailing_data_rejected(self):
        for data in (self.data + b'\0' * 512, self.data + b'other data'):
            with self.assertRaisesRegex(ValueError, 'Noncanonical'):
                w.checked_package(data, self.profile)

    def test_extra_flash_partition_rejected(self):
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode='w') as output, tarfile.open(fileobj=io.BytesIO(self.data)) as source:
            for item in source:
                output.addfile(item, source.extractfile(item))
            item = tarfile.TarInfo('root.sqsh4'); item.size = 1
            output.addfile(item, io.BytesIO(b'x'))
        with self.assertRaisesRegex(ValueError, 'Unexpected'):
            w.checked_package(stream.getvalue(), self.profile)

    def test_capacity_and_nonzero_padding_rejected(self):
        with self.assertRaisesRegex(ValueError, 'capacity'):
            w.package(b'x' * (w.CAPACITY + 1), 'test', 1)
        fs = bytearray(filesystem()); fs[-1] = 1
        with self.assertRaisesRegex(ValueError, 'padding'):
            w.checked_package(w.package(fs, self.profile['target_version'], self.profile['epoch']), self.profile)

    def test_wrong_version_rejected(self):
        self.profile['target_version'] = 'wrong'
        with self.assertRaisesRegex(ValueError, 'version'):
            w.checked_package(self.data, self.profile)

    def test_unsafe_artifact_paths(self):
        for name in ('/tmp/x', '../x', 'files/../../x', 'C:/x', 'files\\x', 'files//x'):
            with self.assertRaises(ValueError):
                w.safe_name(name)


class WifiTests(unittest.TestCase):
    def test_no_public_hostname_or_loopback(self):
        self.assertEqual(wifi.lan_host('192.168.4.2'), '192.168.4.2')
        for host in ('8.8.8.8', '127.0.0.1', 'camera.example', '169.254.1.3', '::1'):
            with self.assertRaises(ValueError):
                wifi.lan_host(host)

    def test_upload_readback_mismatch(self):
        ftp = Mock()
        with patch.object(wifi, 'absent', return_value=True), patch.object(wifi, 'get', return_value=b'corrupt'):
            with self.assertRaisesRegex(ValueError, 'readback'):
                wifi.upload(ftp, '/tmp/update.tar', b'correct')
        ftp.storbinary.assert_called_once()

    def test_existing_upload_target_is_never_overwritten(self):
        ftp = Mock()
        with patch.object(wifi, 'absent', return_value=False):
            with self.assertRaises(ValueError):
                wifi.upload(ftp, '/tmp/update.tar', b'x')
        ftp.storbinary.assert_not_called()

    def test_mandatory_reverification_before_any_connection(self):
        profile = {'target_version': 'test', 'epoch': 1}
        packet = w.package(filesystem(), 'test', 1)
        with tempfile.TemporaryDirectory() as d, patch.object(wifi, 'connect') as connection:
            with self.assertRaisesRegex(ValueError, 'changed'):
                wifi.flash('192.168.1.2', 'not-a-real-password', Path(d) / 'audit', profile,
                           packet, Mock(), lambda: b'changed')
            connection.assert_not_called()

    def test_launcher_waits_for_explicit_token_and_checks_digest(self):
        script = wifi.launcher('a' * 64, 'b' * 32, '6.0.24.10_test').decode()
        self.assertLess(script.index('while [ ! -f /tmp/openyi-go'), script.index('exec /bin/sh /tmp/openyi-update.sh'))
        self.assertIn('exit 80', script)
        self.assertIn('md5sum /tmp/update.tar', script)
        self.assertIn('/tmp/special.sh', script)
        for token in ('', '";reboot;', 'x' * 64):
            with self.assertRaises(ValueError):
                wifi.launcher(token, 'b' * 32, 'test')

    def test_a32_hook_has_exact_branch_destinations(self):
        code = wifi.stat_hook()
        self.assertEqual(len(code), len(wifi.STAT_ORIGINAL))
        for index, address, target, link in ((1, 0x1db58, 0xbc94, True), (3, 0x1db60, 0x1cb00, True), (4, 0x1db64, 0x1d8c0, False)):
            word = struct.unpack_from('<I', code, index * 4)[0]
            delta = word & 0xffffff
            if delta & 0x800000: delta -= 0x1000000
            self.assertEqual(address + 8 + 4 * delta, target)
            self.assertEqual(word >> 24, 0xeb if link else 0xea)

    def test_worker_is_restored_even_when_launch_fails(self):
        bb = bytearray(700000)
        offset = wifi.STAT_ADDRESS - 0x8000
        bb[offset:offset + 20] = wifi.STAT_ORIGINAL
        literal = wifi.COMMAND_ADDRESS - 0x8000
        bb[literal:literal + len(wifi.COMMAND)] = b'z' * len(wifi.COMMAND)
        ftp = Mock(); ftp.sendcmd.side_effect = OSError('lost response')
        def remote(_, path, *args):
            return {'/proc/self/maps': b'maps', '/proc/self/status': b'Uid:\t0\t0\t0\t0\n',
                    '/proc/self/cmdline': b'ftpd\0-w\0'}[path]
        with patch.object(wifi, 'digest', return_value=wifi.raw.BUSYBOX_SHA256), patch.object(wifi.raw, 'validate_maps'), \
             patch.object(wifi, 'get', side_effect=remote), patch.object(wifi, 'read_memory', side_effect=[wifi.STAT_ORIGINAL, b'z' * len(wifi.COMMAND)]), \
             patch.object(wifi, 'write_memory') as write:
            with self.assertRaises(OSError):
                wifi.launch_waiting_script(ftp, bytes(bb))
            self.assertEqual(write.call_args_list[-2].args[1:], (wifi.STAT_ADDRESS, wifi.STAT_ORIGINAL))
            self.assertEqual(write.call_args_list[-1].args[1:], (wifi.COMMAND_ADDRESS, b'z' * len(wifi.COMMAND)))

    def simulated_install(self, directory, *, approval=True, launch_error=None, commit_error=None, preflight_error=None):
        profile = {'target_version': 'test-new', 'source_version': 'test-old', 'epoch': 1, 'binaries': []}
        packet = w.package(filesystem(), 'test-new', 1)
        remote = {}
        ftp = Mock()
        ftp.delete.side_effect = lambda path: remote.pop(path, None)
        if commit_error:
            ftp.rename.side_effect = commit_error
        def launch(*args):
            if launch_error: raise launch_error
            remote['/tmp/openyi-ready'] = b'READY\n'
        identity = {'mac': '00:11:22:33:44:55', 'firmware': 'test-old', 'board': 'test'}
        with ExitStack() as stack:
            stack.enter_context(redirect_stdout(io.StringIO()))
            def mock(name, **args):
                return stack.enter_context(patch.object(wifi, name, **args))
            mock('connect', return_value=ftp)
            mock('preflight', return_value=(identity, b'bb', b'script'), side_effect=preflight_error)
            stack.enter_context(patch.object(wifi.raw, 'acquire'))
            mock('validate_backup', return_value=b'backup')
            mock('ram_updater', return_value=b'updater')
            mock('upload', side_effect=lambda ftp, path, data: remote.__setitem__(path, data))
            mock('absent', side_effect=lambda ftp, path: path not in remote)
            mock('get', side_effect=lambda ftp, path, *args: remote[path])
            hook = mock('launch_waiting_script', side_effect=launch)
            try:
                result = wifi.flash('192.168.1.2', 'unused', directory, profile, packet,
                                    lambda *args: approval, lambda: packet)
                return result, ftp, hook
            finally:
                self.last_ftp, self.last_hook = ftp, hook

    def test_cancel_after_inspection_never_launches_or_commits(self):
        with tempfile.TemporaryDirectory() as d:
            result, ftp, hook = self.simulated_install(Path(d) / 'audit', approval=False)
            self.assertFalse(result['flash_committed'])
            hook.assert_not_called(); ftp.rename.assert_not_called()
            self.assertEqual({c.args[0] for c in ftp.delete.call_args_list},
                             {'/tmp/update.tar', '/tmp/openyi-run.sh', '/tmp/openyi-update.sh'})

    def test_preflight_error_does_not_delete_existing_remote_files(self):
        with tempfile.TemporaryDirectory() as d:
            with self.assertRaises(ValueError):
                self.simulated_install(Path(d) / 'audit', preflight_error=ValueError('existing update'))
            self.last_ftp.delete.assert_not_called()
            self.last_ftp.rename.assert_not_called()

    def test_launch_or_restore_failure_never_releases_commit_token(self):
        with tempfile.TemporaryDirectory() as d:
            with self.assertRaises(ValueError):
                self.simulated_install(Path(d) / 'audit', launch_error=ValueError('restoration failed'))
            self.last_ftp.rename.assert_not_called()

    def test_ambiguous_commit_is_recorded_and_never_retried_or_cleaned(self):
        import json
        with tempfile.TemporaryDirectory() as d:
            target = Path(d) / 'audit'
            with self.assertRaises(OSError):
                self.simulated_install(target, commit_error=OSError('connection lost after commit'))
            self.last_ftp.rename.assert_called_once_with('/tmp/openyi-go.pending', '/tmp/openyi-go')
            self.last_ftp.delete.assert_not_called()
            self.assertTrue(json.loads((target / 'installation.json').read_text())['flash_committed'])

    def test_postflash_full_payload_match_and_worker_restoration(self):
        reference = b'x' * wifi.raw.WINDOW_BYTES
        patched = bytearray(reference)
        offset = wifi.raw.PATCH_ADDRESS - wifi.raw.MEMORY_START
        patched[offset:offset + 4] = wifi.raw.PATCHED
        flash = bytearray(wifi.raw.FLASH_BYTES)
        image = b'full image payload'
        flash[0x2b3000:0x2b3000 + len(image)] = image
        def remote(ftp, path, *args):
            return {'/bin/busybox': b'busybox', '/proc/self/maps': b'maps', '/dev/mtd0ro': bytes(flash)}[path]
        with tempfile.TemporaryDirectory() as d, patch.object(wifi, 'get', side_effect=remote), \
             patch.object(wifi, 'digest', side_effect=lambda data: wifi.raw.BUSYBOX_SHA256 if data == b'busybox' else w.digest(data)), \
             patch.object(wifi.raw, 'validate_maps'), patch.object(wifi.raw, 'validate_reference', return_value=reference), \
             patch.object(wifi.raw, 'read_code_window', side_effect=[reference, bytes(patched), reference]), \
             patch.object(wifi.raw, 'write_worker_instruction') as write:
            self.assertEqual(wifi.verify_flash_readback(Mock(), b'busybox', image, Path(d)), w.digest(flash))
            self.assertEqual((Path(d) / 'post-flash.bin').stat().st_size, wifi.raw.FLASH_BYTES)
            self.assertEqual(write.call_args_list[-1].args[1], wifi.raw.ORIGINAL)

    def test_postflash_mismatch_never_counts_as_verified(self):
        reference = b'x' * wifi.raw.WINDOW_BYTES
        patched = bytearray(reference)
        offset = wifi.raw.PATCH_ADDRESS - wifi.raw.MEMORY_START
        patched[offset:offset + 4] = wifi.raw.PATCHED
        def remote(ftp, path, *args):
            return {'/bin/busybox': b'busybox', '/proc/self/maps': b'maps', '/dev/mtd0ro': b'\0' * wifi.raw.FLASH_BYTES}[path]
        with tempfile.TemporaryDirectory() as d, patch.object(wifi, 'get', side_effect=remote), \
             patch.object(wifi, 'digest', return_value=wifi.raw.BUSYBOX_SHA256), patch.object(wifi.raw, 'validate_maps'), \
             patch.object(wifi.raw, 'validate_reference', return_value=reference), \
             patch.object(wifi.raw, 'read_code_window', side_effect=[reference, bytes(patched), reference]), \
             patch.object(wifi.raw, 'write_worker_instruction') as write:
            with self.assertRaisesRegex(ValueError, 'Physical B payload'):
                wifi.verify_flash_readback(Mock(), b'busybox', b'expected-image', Path(d))
            self.assertEqual(write.call_args_list[-1].args[1], wifi.raw.ORIGINAL)


if __name__ == '__main__':
    unittest.main()
