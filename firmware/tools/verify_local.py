"""Offline execution checks for the hash-pinned local01/local02 candidates.

Unicorn runs the actual ARM bytes. Only documented libc/file/socket boundaries
are modeled; no emulated operation reaches a host file, network, or camera.
This is not a kernel, driver, boot or physical camera test.
"""
from __future__ import annotations

import base64
import hashlib
import hmac
import io
import ipaddress
from pathlib import Path
import struct

from elftools.elf.elffile import ELFFile
from keystone import Ks, KS_ARCH_ARM, KS_MODE_ARM
from unicorn import Uc, UC_ARCH_ARM, UC_MODE_ARM, UC_HOOK_CODE, UC_HOOK_INTR
from unicorn.arm_const import (UC_CPU_ARM_926, UC_ARM_REG_R0, UC_ARM_REG_R1,
    UC_ARM_REG_R2, UC_ARM_REG_R3, UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6,
    UC_ARM_REG_R7, UC_ARM_REG_R8, UC_ARM_REG_R9, UC_ARM_REG_R10, UC_ARM_REG_R11,
    UC_ARM_REG_R12, UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC)

from workshop import HOME, digest, require, safe_name

REGS = [UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
        UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7,
        UC_ARM_REG_R8, UC_ARM_REG_R9, UC_ARM_REG_R10, UC_ARM_REG_R11, UC_ARM_REG_R12]
RAM, STACK, STOP = 0x70000000, 0x711f0000, 0x600ff000
KEY, CACHED, SDK = 0x51b1dc, 0x4b2004, 0x4e054
KEY_PATH = b'/etc/jffs2/openyi.key'
TEST_KEY = b'Owner_TestKey15'  # synthetic, exactly 15 bytes; never a product default


class Arm:
    """Restricted ELF loader and ABI boundary model; unexpected calls fail."""
    def __init__(self, data, base=0):
        self.u = Uc(UC_ARCH_ARM, UC_MODE_ARM)
        self.u.ctl_set_cpu_model(UC_CPU_ARM_926)
        self.base, self.handlers, self.symbols, self.imports, self.got = base, {}, {}, {}, {}
        self.calls = []
        elf = ELFFile(io.BytesIO(data))
        segments = [p for p in elf.iter_segments() if p['p_type'] == 'PT_LOAD']
        lo = min(p['p_vaddr'] for p in segments) & ~4095
        hi = (max(p['p_vaddr'] + p['p_memsz'] for p in segments) + 4095) & ~4095
        self.u.mem_map(base + lo, hi - lo)
        for p in segments:
            self.u.mem_write(base + p['p_vaddr'], p.data())
        for address, size in [(0x60000000, 0x100000), (RAM, 0x100000), (0x71000000, 0x200000)]:
            self.u.mem_map(address, size)
        self.errno = RAM + 0x100
        symbols = elf.get_section_by_name('.dynsym')
        for symbol in symbols.iter_symbols():
            if symbol['st_value'] and symbol['st_shndx'] != 'SHN_UNDEF':
                self.symbols[symbol.name] = base + symbol['st_value']
        extern = {}
        for section in elf.iter_sections():
            if section['sh_type'] != 'SHT_REL':
                continue
            syms = elf.get_section(section['sh_link'])
            for r in section.iter_relocations():
                target, kind = base + r['r_offset'], r['r_info_type']
                if kind == 20:  # R_ARM_COPY: these unused libc pointer slots start null.
                    sym = syms.get_symbol(r['r_info_sym'])
                    require(sym.name in ('stdout', 'stderr', 'program_invocation_short_name', '__ctype_tolower')
                            and sym['st_size'] == 4, 'Unhandled copied ELF object')
                    self.word(target, 0)
                elif kind == 23:  # R_ARM_RELATIVE
                    self.word(target, self.word(target) + base)
                elif kind in (2, 21, 22):  # ABS32 / GLOB_DAT / JUMP_SLOT
                    sym = syms.get_symbol(r['r_info_sym']); name = sym.name
                    if sym['st_shndx'] != 'SHN_UNDEF':
                        value = base + sym['st_value']
                    else:
                        if name not in extern:
                            extern[name] = 0x60000000 + 4 * len(extern)
                        value = extern[name]
                        self.imports[value] = name
                    self.word(target, value + (self.word(target) if kind == 2 else 0))
                    self.got[name] = target
                else:
                    raise ValueError(f'Unhandled ELF relocation {kind}')
        self.u.hook_add(UC_HOOK_CODE, self._code)
        self.u.hook_add(UC_HOOK_INTR, self._interrupt)

    def reg(self, n):
        return self.u.reg_read(REGS[n])

    def word(self, address, value=None):
        if value is None:
            return struct.unpack('<I', self.u.mem_read(address, 4))[0]
        self.u.mem_write(address, struct.pack('<I', value & 0xffffffff))

    def cstr(self, address, maximum=4096):
        out = bytearray()
        for offset in range(maximum):
            b = self.u.mem_read(address + offset, 1)[0]
            if b == 0:
                return bytes(out)
            out.append(b)
        raise ValueError('Unterminated modeled C string')

    def put(self, address, data):
        self.u.mem_write(address, bytes(data))
        return address

    def ret(self, value):
        self.u.reg_write(UC_ARM_REG_R0, value & 0xffffffff)
        self.u.reg_write(UC_ARM_REG_PC, self.u.reg_read(UC_ARM_REG_LR))

    def _interrupt(self, *_):
        raise ValueError('Emulated syscall/interrupt is forbidden')

    def _code(self, uc, address, size, _):
        if address == STOP:
            uc.emu_stop()
        elif address in self.handlers:
            self.handlers[address](self)
        elif address in self.imports:
            name = self.imports[address]
            self.calls.append(name)
            if name in self.handlers:
                self.ret(self.handlers[name](self))
            else:
                self.ret(self.libc(name))

    def libc(self, name):
        a, b, c, d = [self.reg(n) for n in range(4)]
        if name == '__errno_location': return self.errno
        if name in ('memcpy', 'memmove'):
            require(c <= 1024 * 1024, 'Unbounded memcpy in ARM model')
            self.put(a, self.u.mem_read(b, c)); return a
        if name == 'memset':
            require(c <= 1024 * 1024, 'Unbounded memset in ARM model')
            self.put(a, bytes([b & 255]) * c); return a
        if name == 'strlen': return len(self.cstr(a))
        if name in ('strcmp', 'strncmp', 'memcmp'):
            if name == 'memcmp':
                x, y = bytes(self.u.mem_read(a, c)), bytes(self.u.mem_read(b, c))
            else:
                x, y = self.cstr(a), self.cstr(b)
                if name == 'strncmp': x, y = x[:c], y[:c]
            return (x > y) - (x < y)
        if name in ('strcpy', 'strncpy'):
            data = self.cstr(b) + b'\0'
            if name == 'strncpy': data = data[:c].ljust(c, b'\0')
            self.put(a, data); return a
        if name == 'snprintf':
            fmt = self.cstr(c)
            require(fmt == b'user=xiaoyiuser&nonce=%s', 'Unmodeled snprintf format: ' + repr(fmt))
            result = fmt.replace(b'%s', self.cstr(d))
            if b: self.put(a, result[:b - 1] + b'\0')
            return len(result)
        if name == 'sscanf':
            fmt = self.cstr(b)
            require(fmt == b'%[0-9A-Za-z],%s', 'Unmodeled sscanf format: ' + repr(fmt))
            parts = self.cstr(a).split(b',', 1)
            if len(parts) != 2 or not parts[0].isalnum(): return 0
            self.put(c, parts[0] + b'\0'); self.put(d, parts[1] + b'\0'); return 2
        if name == 'error_at_line':
            require(a == 0, 'Firmware requested fatal exit'); return 0
        raise ValueError('Unmodeled ARM import: ' + name)

    def run(self, address, args=(), count=2_000_000):
        for n, reg in enumerate(REGS): self.u.reg_write(reg, 0xa0000000 + n)
        preserved = [self.reg(n) for n in range(4, 12)]
        for n, value in enumerate(args):
            if n < 4: self.u.reg_write(REGS[n], value)
            else: self.word(STACK + 4 * (n - 4), value)
        self.u.reg_write(UC_ARM_REG_SP, STACK)
        self.u.reg_write(UC_ARM_REG_LR, STOP)
        self.u.emu_start(self.base + address, STOP + 4, count=count)
        require(self.u.reg_read(UC_ARM_REG_PC) == STOP, 'ARM execution exceeded instruction limit')
        require(self.u.reg_read(UC_ARM_REG_SP) == STACK, 'ARM stack imbalance')
        require([self.reg(n) for n in range(4, 12)] == preserved, 'ARM callee-saved register corruption')
        return self.reg(0)


class KeyFiles:
    """In-memory short-I/O/error-injection filesystem, not host filesystem I/O."""
    def __init__(self, files=None, faults=None):
        self.files = dict(files or {})
        self.faults = faults or {}
        self.fds, self.durable, self.modes = {}, set(), {}

    def bind(self, arm):
        for name in ('open', 'read', 'write', 'close', 'fsync'):
            arm.handlers[name] = lambda m, name=name: self.call(m, name)

    def call(self, arm, name):
        a, b, c = [arm.reg(n) for n in range(3)]
        if name == 'open':
            path = arm.cstr(a)
            require(path in (KEY_PATH, b'/dev/urandom'), 'Unexpected key filesystem path')
            if self.faults.get('open_' + path.decode()):
                arm.word(arm.errno, 13); return -1
            if path == b'/dev/urandom':
                self.files[path] = bytes(range(15))
            if b == 193:
                if path in self.files:
                    arm.word(arm.errno, 17); return -1
                self.files[path] = b''; self.modes[path] = c
            else:
                require(b == 0, 'Unexpected file open flags')
            if path not in self.files:
                arm.word(arm.errno, 2); return -1
            fd = max([9] + list(self.fds)) + 1
            self.fds[fd] = [path, 0]; return fd
        require(a in self.fds, 'Invalid modeled fd')
        path, offset = self.fds[a]
        if self.faults.get(name): return -1
        if name == 'close': del self.fds[a]; return 0
        if name == 'fsync': self.durable.add(path); return 0
        if name == 'read':
            if self.faults.get('entropy_eof') and path == b'/dev/urandom': return 0
            data = self.files[path][offset:offset + min(c, self.faults.get('chunk', c))]
            arm.put(b, data); self.fds[a][1] += len(data); return len(data)
        if name == 'write':
            length = min(c, self.faults.get('chunk', c))
            if self.faults.get('write_zero'): return 0
            self.files[path] = self.files[path][:offset] + bytes(arm.u.mem_read(b, length))
            self.fds[a][1] += length; return length
        raise ValueError('Unmodeled file operation')


def assembly_checks(profile):
    assembler = Ks(KS_ARCH_ARM, KS_MODE_ARM)
    count = 0
    for binary in profile['binaries']:
        for patch in binary['patches']:
            if 'assembly_source' in patch:
                path = HOME.joinpath(*safe_name(patch['assembly_source']).parts)
                require(path.resolve().is_relative_to(HOME.resolve()), 'Assembly path escaped repository')
                source = path.read_bytes()
                require(digest(source) == patch['assembly_sha256'], 'Assembly source SHA-256 mismatch')
                code = source.decode('utf-8')
            else:
                code = patch['assembly']
            code = '\n'.join(line.split('//', 1)[0] for line in code.splitlines())
            result, _ = assembler.asm(code, addr=int(patch['address'], 0))
            require(result is not None and bytes(result).hex() == patch['after'], 'Reassembled patch differs: ' + patch['name'])
            count += 1
    return count


def key_checks(data):
    passed = []
    scenarios = [
        ('existing canonical key', {KEY_PATH: TEST_KEY + b'\n'}, b'OldDifferentKey', {}, True),
        ('migrate current client key', {}, TEST_KEY, {}, True),
        ('generate fresh persistent key', {}, b'', {}, True),
        ('short reads and writes', {}, b'', {'chunk': 2}, True),
        ('short canonical reads', {KEY_PATH: TEST_KEY + b'\n'}, b'', {'chunk': 1}, True),
        ('invalid canonical key fails closed', {KEY_PATH: b'bad\n'}, TEST_KEY, {}, False),
        ('oversized canonical key fails closed', {KEY_PATH: TEST_KEY + b'\nX'}, TEST_KEY, {}, False),
        ('non-ASCII key fails closed', {KEY_PATH: b'\xff' * 15 + b'\n'}, TEST_KEY, {}, False),
        ('permission error does not rotate', {}, TEST_KEY, {'open_' + KEY_PATH.decode(): True}, False),
        ('entropy open failure', {}, b'', {'open_/dev/urandom': True}, False),
        ('entropy EOF', {}, b'', {'entropy_eof': True}, False),
        ('read error', {KEY_PATH: TEST_KEY + b'\n'}, b'', {'read': True}, False),
        ('write error', {}, TEST_KEY, {'write': True}, False),
        ('zero write', {}, TEST_KEY, {'write_zero': True}, False),
        ('fsync error', {}, TEST_KEY, {'fsync': True}, False),
        ('close error', {KEY_PATH: TEST_KEY + b'\n'}, b'', {'close': True}, False),
    ]
    for name, initial, cached, faults, success in scenarios:
        m, fs = Arm(data), KeyFiles(initial, faults)
        fs.bind(m)
        m.put(CACHED, cached.ljust(32, b'\0'))
        m.put(KEY, b'X' * 32)
        factory = bytes(range(96)); m.put(0x51b21c, factory)
        result = m.run(0x434bc, [1])
        require(result == int(success), 'Key case failed: ' + name)
        require(bytes(m.u.mem_read(0x51b21c, 96)) == factory, 'Factory transport identity changed')
        if success:
            expected = fs.files[KEY_PATH][:-1]
            require(len(expected) == 15 and m.cstr(KEY) == expected == m.cstr(CACHED), 'Key publish mismatch')
            if KEY_PATH not in initial:
                require(KEY_PATH in fs.durable and fs.modes[KEY_PATH] == 0o600, 'Key published before durable private write')
            # A new process and unrelated cached key must reproduce the canonical key.
            again = Arm(data); fs.bind(again); again.put(CACHED, b'X' * 15 + b'\0')
            require(again.run(0x434bc, [1]) == 1 and again.cstr(KEY) == expected, 'Key changed across simulated reboot')
        else:
            require(bytes(m.u.mem_read(KEY, 32)) == b'\0' * 32, 'Failed initialization exposed a key')
        ready = m.run(0x47000)
        require(ready == int(success) and m.u.mem_read(0x51b538, 1)[0] == int(success), 'Readiness did not follow key state')
        require(bytes(m.u.mem_read(0x51b21c, 96)) == factory, 'Readiness changed factory identity')
        passed.append(name)
    return passed


def auth_checks(data):
    """Run stock HMAC, Base64, authentication and session-cache ARM code."""
    base, nonce = 0x10000000, b'ABCDEFG01234567'
    message = b'user=xiaoyiuser&nonce=' + nonce
    expected = base64.b64encode(hmac.new(TEST_KEY, message, hashlib.sha1).digest())
    m = Arm(data, base)
    m.put(RAM + 0x1000, TEST_KEY + b'\0'); m.put(RAM + 0x1100, message + b'\0')
    m.run(0xe8d4, [RAM + 0x1000, RAM + 0x1100, RAM + 0x1200])
    require(m.cstr(RAM + 0x1200) == expected, 'Camera HMAC/Base64 disagrees with independent Python result')
    passed = ['actual ARM HMAC-SHA1/Base64 matches Python']
    for label, supplied, fallback, expected_result in [
        ('correct key', TEST_KEY, b'', 0),
        ('wrong key rejected', b'Wrong_TestKey15', b'', 1),
        ('legacy alternate key accepted in stock SDK', b'Other_TestKey15', b'Other_TestKey15', 0),
    ]:
        m = Arm(data, base)
        signature = base64.b64encode(hmac.new(supplied, message, hashlib.sha1).digest())[:15]
        m.put(RAM + 0x1000, nonce + b',' + signature + b'\0')
        m.put(RAM + 0x1100, TEST_KEY + b'\0')
        m.put(RAM + 0x1200, fallback + b'\0')
        m.put(RAM + 0x1300, b'\0')
        result = m.run(0x10838, [0, RAM + 0x1000, RAM + 0x1100, RAM + 0x1200, RAM + 0x1300, RAM + 0x1400])
        require(result == expected_result, f'Authentication case {label}: got {result}')
        if result == 0:
            require(m.cstr(base + SDK + 0x49) == supplied, 'Authenticated key not copied into session')
        passed.append(label)
    # Normal (version 2) sessions pin the authenticated key and reject nonce reuse.
    m = Arm(data, base)
    m.put(base + SDK + 0x40, b'\x02')
    args = [0, RAM + 0x1000, RAM + 0x1100, RAM + 0x1100, RAM + 0x1100, RAM + 0x1400]
    m.put(args[2], TEST_KEY + b'\0')
    for sequence in range(2):
        n = b'ABCDEFG' + f'{sequence:08d}'.encode()
        sig = base64.b64encode(hmac.new(TEST_KEY, b'user=xiaoyiuser&nonce=' + n, hashlib.sha1).digest())[:15]
        m.put(args[1], n + b',' + sig + b'\0')
        require(m.run(0x10838, args) == 0, 'Authenticated session command failed')
    require(m.run(0x10838, args) == 2, 'Repeated nonce was not rejected')
    passed += ['version 2 authenticated session uses cached key', 'nonce replay rejected']
    return passed


def callback_checks(data):
    m = Arm(data)
    m.put(KEY, TEST_KEY + b'\0')
    m.put(0x4b2314, b'Other_TestKey15\0')
    m.put(0x4b2334, b'Wrong_TestKey15\0')
    seen = []
    def auth(arm):
        args = [arm.reg(2), arm.reg(3), arm.word(arm.u.reg_read(UC_ARM_REG_SP))]
        require(args == [KEY] * 3, 'Callback still exposes an alternate password')
        seen.append(True)
        return 1  # rejection must propagate unchanged
    m.handlers['yi_p2p_do_auth'] = auth
    require(m.run(0x2d3e4, [3, RAM + 0x1000]) == 1 and seen, 'Authentication callback result changed')
    return ['all three credentials use canonical key', 'verification result propagated unchanged']


def startup_checks(data, keyless=False):
    passed = []
    cases = ((False, False), (True, False)) if keyless else ((False, True), (True, True), (True, False))
    for fresh, key_ready in cases:
        m = Arm(data)
        m.put(KEY, TEST_KEY + b'\0' if key_ready else b'\0' * 16)
        m.word(0x4b3a48, int(fresh))
        m.put(0x51b53d, bytes([int(fresh)]))  # pending QR binding
        m.put(0x51b008, b'TestSSID\0'); m.put(0x51b048, b'TestWifiPassword\0')
        m.put(0x51b088, b'20LocallyMadeToken123\0')
        m.handlers['prctl'] = lambda a: 0
        m.handlers['sysTime'] = lambda a: 10000
        m.handlers[m.symbols['sysTime']] = lambda a: a.ret(10000)
        m.handlers['gettimeofday'] = lambda a: (a.put(a.reg(0), struct.pack('<II', 10000, 0)) and 0)
        m.handlers['getpid'] = lambda a: 123
        m.handlers['printf'] = lambda a: 0
        def syscall(arm):
            require(arm.reg(0) == 224, 'Unexpected syscall request in startup model')
            return 123  # gettid, modeled here; no host syscall
        m.handlers['syscall'] = syscall
        calls = []
        for address, name in [(0x5e48c, 'thread name'), (0x4b3f0, 'voice'), (0x3f3c8, 'camera state'),
                              (0x26da0, 'Wi-Fi ready'), (0x26de0, 'saved Wi-Fi ready'),
                              (0x48ac4, 'save Wi-Fi'), (0x26f38, 'save binding text'), (0x281c0, 'save settings')]:
            def boundary(arm, name=name):
                calls.append((name, arm.reg(0)))
                arm.ret(1 if name.startswith('save') else 0)
            m.handlers[address] = boundary
        m.handlers[0x45e00] = lambda _: (_ for _ in ()).throw(ValueError('Local key failure entered vendor configuration recovery'))
        require(m.run(0x478cc) == 0, 'Local registration worker did not terminate')
        names = [name for name, _ in calls]
        if fresh and (key_ready or keyless):
            require(('voice', 8) in calls and ('Wi-Fi ready', 1) in calls and
                    names.count('save Wi-Fi') == 1 and names.count('save settings') == 1,
                    'Fresh local binding omitted Wi-Fi persistence/success path')
            require(m.u.mem_read(0x51b53d, 1) == b'\0', 'Pending binding was not cleared')
        else:
            require(names == ['thread name'], 'Saved/failing boot unexpectedly modified configuration')
        passed.append(('fresh binding' if fresh else 'saved Wi-Fi') +
                      (' completes without a key' if keyless else ' completes locally' if key_ready else ' preserves configuration on key failure'))
    return passed


def keyless_checks(ipc, tnp):
    from yicam.protocol_modes import LOCAL_PLAIN_MARKER
    require(LOCAL_PLAIN_MARKER == b'OpenYI-LAN-v1'.ljust(32, b'\0'), 'Python plaintext marker differs')
    passed = []
    for key in (b'\0' * 16, TEST_KEY + b'\0'):
        m = Arm(ipc); m.put(KEY, key); m.put(CACHED, key)
        require(m.run(0x434bc) == 1 and m.cstr(KEY) == b'' and m.cstr(CACHED) == b'',
                'Keyless initialization depends on a credential')
        require(m.run(0x47000) == 1 and m.cstr(0x51b480) == b'' and
                m.cstr(0x51b460) == b'LOCAL0' and m.u.mem_read(0x51b538, 1) == b'\1',
                'Keyless local readiness failed')
        passed.append('keyless bootstrap ' + ('clears cached key' if key[0] else 'with no cached key'))
    for base in (0x10000000, 0x20000000):
        for name, value, result in [('public marker', LOCAL_PLAIN_MARKER, 0),
                                   ('stock HMAC', b'0123456789abcde,wrongsignature!\0'.ljust(32, b'\0'), 1),
                                   ('empty marker', bytes(32), 1),
                                   ('wrong marker version', b'OpenYI-LAN-v2'.ljust(32, b'\0'), 1)]:
            m = Arm(tnp, base); m.put(RAM + 0x1000, value); m.put(RAM + 0x2000, b'\xff')
            require(m.run(0x10838, [0, RAM + 0x1000, 0, 0, 0, RAM + 0x2000]) == result,
                    'Keyless marker gate differs: ' + name)
            require(m.u.mem_read(RAM + 0x2000, 1) == b'\0', 'Auth status output was not cleared')
            passed.append(name + ' at ' + hex(base))
    return passed


def scanner_payload(data, payload):
    """Execute the real field/Base64/XOR parser after a modeled optical decode."""
    m = Arm(data, 0x10000000)
    require(len(payload) < 512 and payload.isascii(), 'Unbounded scanner fixture')
    source = m.put(RAM + 0x2000, payload.encode('ascii') + b'\0')
    output = RAM + 0x3000
    # ZBar's image/symbol boundary is modeled, not the lens or pixel decoder.
    for name, result in {
        'zbar_image_scanner_create': RAM + 0x4000, 'zbar_image_create': RAM + 0x4100,
        'zbar_image_scanner_set_config': 0, 'zbar_image_set_format': 0,
        'zbar_image_set_size': 0, 'zbar_image_set_data': 0, 'zbar_scan_image': 1,
        'zbar_image_first_symbol': RAM + 0x4200, 'zbar_symbol_get_type': 64,
        'zbar_symbol_get_data': source,
        'zbar_get_symbol_name': m.put(RAM + 0x4300, b'QR-Code\0'),
        'zbar_symbol_next': 0, 'zbar_image_destroy': 0, 'zbar_image_scanner_destroy': 0,
    }.items():
        require(name in m.symbols, 'Unexpected scanner boundary')
        m.handlers[m.symbols[name]] = lambda a, value=result: a.ret(value)
    m.handlers['printf'] = lambda a: 0  # suppress even synthetic credentials
    def strstr(a):
        index = a.cstr(a.reg(0)).find(a.cstr(a.reg(1)))
        return a.reg(0) + index if index >= 0 else 0
    def snprintf(a):
        require(a.cstr(a.reg(2)) == b'%s', 'Unexpected scanner format')
        value, size = a.cstr(a.reg(3)), a.reg(1)
        require(size <= 88, 'Unbounded scanner copy')
        if size: a.put(a.reg(0), value[:size - 1] + b'\0')
        return len(value)
    m.handlers['strstr'], m.handlers['snprintf'] = strstr, snprintf
    result = m.run(0x3438, [RAM + 0x5000, 64, 64, output])
    return result, tuple(m.cstr(output + offset) for offset in (0, 0x40, 0x80))


def provisioning_checks(scanner, ipc):
    from local_setup import setup_payload
    from yicam.provisioning import parse
    require(digest(scanner) == 'baf862649c0a8ffe26861328fdda1a7e19bab9a1409f46f498ec7ec3aee1e288',
            'Unqualified QR scanner binary')
    passed = []
    cases = [(region + ' local marker', 'OpenYI Test', 'OnlyATest123', region)
             for region in ('EU', 'US', 'CN')]
    cases += [('UTF-8 SSID', 'Keittiö caméra', 'OnlyATest123', 'EU'),
              ('maximum SSID and password', 'S' * 32, 'p' * 63, 'EU'),
              ('XOR zero fallback', 'OpenYI Test', '89JFSjo8', 'EU'),
              ('ASCII punctuation and spaces', ' A & B ', ' !"#$%&\'()*+,-./:;<=>?@[\\]^_`{|}~', 'EU'),
              ('open network', 'OpenYI Test', '', 'EU')]
    for label, ssid, password, region in cases:
        payload = setup_payload(ssid, password, region)
        token = parse(payload)['b'].encode('ascii')
        result, values = scanner_payload(scanner, payload)
        require(result == 0 and values == (ssid.encode('utf-8'), password.encode('ascii'), token),
                'Camera parser disagrees with local setup composition: ' + label)
        passed.append(label + ' decodes exactly')
    payload = setup_payload('OpenYI Test', 'OnlyATest123')
    for label, value in [('missing marker', payload.split('&', 1)[1]),
                         ('empty marker', 'b=&' + payload.split('&', 1)[1]),
                         ('missing SSID', 'b=EUOpenYiTest000000001&p='),
                         ('empty SSID', 'b=EUOpenYiTest000000001&s=&p=')]:
        result, _ = scanner_payload(scanner, value)
        require(result == 0xffffffff, 'Camera scanner unexpectedly accepts ' + label)
        passed.append(label + ' rejected by retained scanner')
    for token, expected in [(b'EUOpenYiTest000000001', 16), (b'USOpenYiTest000000001', 17),
                            (b'CNOpenYiTest000000001', 1), (b'EUChangedTail00000001', 16)]:
        m = Arm(ipc)
        m.handlers['sysTime'] = lambda a: 10000
        m.handlers[m.symbols['sysTime']] = lambda a: a.ret(10000)
        m.handlers['getpid'] = lambda a: 123
        def syscall(a):
            require(a.reg(0) == 224, 'Unexpected region-parser syscall')
            return 123
        m.handlers['syscall'], m.handlers['printf'] = syscall, lambda a: 0
        require(m.run(0x234dc, [m.put(RAM + 0x2000, token + b'\0')]) == expected,
                'Local marker region parsing differs')
        passed.append(token[:2].decode() + (' altered tail' if b'Changed' in token else '') + ' selects expected region')
    return passed


def network_checks(ipc, tnp):
    results = []
    addresses = [(ip, allowed) for ip, allowed in [
        ('10.0.0.1', True), ('172.16.0.1', True), ('172.31.255.254', True),
        ('192.168.1.9', True), ('127.0.0.1', True), ('169.254.1.1', True),
        ('255.255.255.255', True), ('8.8.8.8', False), ('1.1.1.1', False),
        ('172.15.1.1', False), ('172.32.1.1', False), ('192.169.0.1', False),
        ('0.0.0.0', False), ('224.0.0.1', False)]]
    cases = [(ip + ':1234', struct.pack('<H', 2) + struct.pack('>H', 1234) +
              ipaddress.IPv4Address(ip).packed + b'\0' * 8, allowed) for ip, allowed in addresses]
    for port in (53, 853):
        cases.append((f'private DNS:{port}', struct.pack('<H', 2) + struct.pack('>H', port) +
                      ipaddress.IPv4Address('192.168.1.1').packed + b'\0' * 8, False))
    cases += [('IPv6', struct.pack('<H', 10) + b'\0' * 26, False),
              ('UNIX IPC', struct.pack('<H', 1), True),
              ('netlink IPC', struct.pack('<H', 16), True),
              ('truncated IPv4', struct.pack('<H', 2), False), ('no destination', b'', False)]
    for label, data, base, entry, operation in [
        ('app UDP', ipc, 0, 0x1baac, 'sendto'), ('app TCP', ipc, 0, 0x1b6c8, 'connect'),
        ('SDK UDP base A', tnp, 0x10000000, 0xb5dc, 'sendto'),
        ('SDK UDP base B', tnp, 0x20000000, 0xb5dc, 'sendto')]:
        for case, sockaddr, allowed in cases:
            m = Arm(data, base)
            pointer = m.put(RAM + 0x1000, sockaddr) if sockaddr else 0
            args = [9, RAM + 0x2000, 37, 0, pointer, len(sockaddr)] if operation == 'sendto' else [9, pointer, len(sockaddr)]
            seen = []
            def call(arm):
                values = [arm.reg(i) for i in range(min(4, len(args)))]
                if operation == 'sendto':
                    sp = arm.u.reg_read(UC_ARM_REG_SP)
                    values += [arm.word(sp), arm.word(sp + 4)]
                require(values == args, 'Network guard changed original arguments')
                seen.append(True)
                return 37
            m.handlers[operation] = call
            try:
                result = m.run(entry, args)
            except Exception as error:
                raise ValueError(f'Network case {label} / {case}: PC={m.u.reg_read(UC_ARM_REG_PC):#x}, ip={m.reg(12):#x}: {error}') from error
            require(result == (37 if allowed else 0xffffffff) and bool(seen) == allowed,
                    'Network guard failed: ' + label + ' / ' + case)
            if not allowed: require(m.word(m.errno) == 13, 'Denied socket did not set EACCES')
            results.append(label + ' / ' + case)
    return results


def aes_checks(data):
    # FIPS-197 AES-128 vector, also executed through the firmware's decrypt path.
    m = Arm(data, 0x10000000)
    key = bytes.fromhex('000102030405060708090a0b0c0d0e0f')
    plain = bytes.fromhex('00112233445566778899aabbccddeeff')
    cipher = bytes.fromhex('69c4e0d86a7b0430d8cdb78070b4c55a')
    context, keyp, source, target = [RAM + v for v in (0x1000, 0x2000, 0x2100, 0x2200)]
    m.put(keyp, key); m.put(source, plain)
    m.run(0x3790c, [context, keyp, 16, 0, 0])
    m.run(0x37910, [context, target, source])
    require(bytes(m.u.mem_read(target, 16)) == cipher, 'Firmware AES encryption vector failed')
    m.run(0x3790c, [context, keyp, 16, 0, 1])
    m.run(0x37920, [context, source, target])
    require(bytes(m.u.mem_read(source, 16)) == plain, 'Firmware AES decryption vector failed')
    return ['actual ARM AES-128 encryption/decryption matches FIPS-197 vector']


def media_checks(data, plain=False):
    passed = []
    for audio in (True, False):
        m = Arm(data, 0x10000000)
        m.put(m.base + SDK + 0x49, b'\0' * 16 if plain else TEST_KEY + b'\0')
        m.put(m.base + SDK + 0x40, b'\x02\x00\x01\x00\x00')
        m.handlers['gettimeofday'] = lambda a: (a.put(a.reg(0), struct.pack('<II', 10000, 0)) and 0)
        m.handlers[m.symbols['yi_p2p_check_buf']] = lambda a: (a.word(a.reg(2), 0), a.ret(0))
        packets, keys = [], []
        m.handlers[m.symbols['PPPP_Write']] = lambda a: (packets.append(bytes(a.u.mem_read(a.reg(2), a.reg(3)))), a.ret(a.reg(3)))
        for name in ('AesSetKey', 'AesSetKeyDirect'):
            # Observe the real AES entry, then execute it normally.
            m.handlers[m.symbols[name]] = lambda a: keys.append(bytes(a.u.mem_read(a.reg(1), 16)))
        info = bytearray(24); info[12] = 1; info[13] = int(not audio); info[14] = 1
        struct.pack_into('<I', info, 4, 20)
        plain_payload = b'\xff\xf1' + bytes(range(78))
        m.put(RAM + 0x1000, info); m.put(RAM + 0x2000, b'\0' * 32 + plain_payload)
        result = m.run(0xfce8, [0, RAM + 0x1000, RAM + 0x2000, len(plain_payload)])
        require(result == 112 and len(packets) == 1 and len(packets[0]) == 112, 'Media packet framing changed')
        if plain:
            require(not keys and packets[0][32:] == plain_payload, 'Keyless media unexpectedly uses AES or modifies payload')
        else:
            require(keys and all(key == TEST_KEY + b'0' for key in keys), 'Media AES does not use session password plus ASCII zero')
        passed.append(('microphone' if audio else 'video') + (' sender preserves plaintext' if plain else ' sender uses authenticated session key'))
        if audio:
            # Use the sender's encrypted payload with OpenYI's talk-back header.
            pending = bytearray(packets[0])
            pending[8:32] = b'\0' * 24
            struct.pack_into('>H', pending, 8, 138)
            pending[10] = 2
            struct.pack_into('>I', pending, 20, 20)  # nonzero speaker counter
            def read(arm):
                length = arm.word(arm.reg(3))
                require(len(pending) >= length, 'Speaker requested excess packet bytes')
                arm.put(arm.reg(2), pending[:length]); del pending[:length]; arm.ret(0)
            m.handlers[m.symbols['PPPP_Read']] = read
            result = m.run(0x10578, [0, RAM + 0x3000, RAM + 0x4000])
            require(result == len(plain_payload) and not pending and bytes(m.u.mem_read(RAM + 0x3000, result)) == plain_payload,
                    'Speaker decryption does not recover encrypted microphone payload')
            require(not keys if plain else all(key == TEST_KEY + b'0' for key in keys), 'Speaker AES behavior differs')
            passed.append('speaker receive preserves plaintext' if plain else 'speaker receive decrypts with the same session key')
    return passed


def verify_tree(root, profile):
    suite = profile.get('offline_checks')
    require(suite in ('local01', 'local02'), 'Unknown mandatory ARM check suite')
    count = assembly_checks(profile)
    ipc = (root / 'bin/anyka_ipc').read_bytes()
    tnp = (root / 'lib/libYiP2P.so').read_bytes()
    plain = suite == 'local02'
    # Import the reference protocol only for the keyless interoperability checks.
    if plain:
        import sys
        sys.path.insert(0, str(HOME.parent))
    return {'suite': suite, 'reassembled_patches': count,
            'key_cases': keyless_checks(ipc, tnp) if plain else key_checks(ipc),
            'authentication_cases': [] if plain else auth_checks(tnp),
            'callback_cases': callback_checks(ipc), 'network_cases': network_checks(ipc, tnp),
            'startup_cases': startup_checks(ipc, keyless=plain),
            'provisioning_cases': provisioning_checks((root / 'lib/libscanner.so').read_bytes(), ipc),
            'aes_cases': aes_checks(tnp),
            'media_cases': media_checks(tnp, plain=plain),
            'hardware_tested': False}
