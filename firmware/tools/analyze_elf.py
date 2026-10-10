"""Offline ARM ELF symbol, literal and direct-call inspection; never executes it."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import struct

from capstone import Cs, CS_ARCH_ARM, CS_MODE_ARM, CS_MODE_THUMB
from capstone.arm import ARM_OP_IMM, ARM_OP_MEM, ARM_REG_PC
from elftools.elf.elffile import ELFFile


class Analyzer:
    def __init__(self, path: Path):
        self.data = path.read_bytes()
        with path.open('rb') as stream:
            elf = ELFFile(stream)
            if elf['e_machine'] != 'EM_ARM' or not elf.little_endian or elf.elfclass != 32:
                raise ValueError('Expected 32-bit little-endian ARM ELF')
            self.segments = [dict(segment.header) for segment in elf.iter_segments()
                             if segment['p_type'] == 'PT_LOAD']
            self.symbols = {}
            for name in ('.dynsym', '.symtab'):
                section = elf.get_section_by_name(name)
                if section:
                    for symbol in section.iter_symbols():
                        if symbol['st_info']['type'] == 'STT_FUNC' and symbol['st_value']:
                            self.symbols[symbol.name] = {'address': symbol['st_value'], 'size': symbol['st_size'],
                                                        'defined': symbol['st_shndx'] != 'SHN_UNDEF'}
            text = elf.get_section_by_name('.text')
            self.text_address, self.text_data = text['sh_addr'], text.data()
            self.plt = {}
            relocations = elf.get_section_by_name('.rel.plt')
            plt = elf.get_section_by_name('.plt')
            if relocations and plt:
                symbols = elf.get_section(relocations['sh_link'])
                # GNU ARM PLT: 20-byte resolver, then 12-byte entries. Check the
                # actual extent so another linker layout cannot be mislabeled.
                if plt['sh_size'] == 20 + 12 * relocations.num_relocations():
                    for index, relocation in enumerate(relocations.iter_relocations()):
                        name = symbols.get_symbol(relocation['r_info_sym']).name
                        self.plt[plt['sh_addr'] + 20 + 12 * index] = name
        self.by_address = {s['address'] & ~1: name for name, s in self.symbols.items()}
        self.by_address.update(self.plt)
        self.arm = Cs(CS_ARCH_ARM, CS_MODE_ARM)
        self.arm.detail = True
        self.arm.skipdata = True

    def offset(self, address, size=1):
        for segment in self.segments:
            relative = address - segment['p_vaddr']
            if relative >= 0 and relative + size <= segment['p_filesz']:
                return segment['p_offset'] + relative
        raise ValueError(f'Address outside file-backed load segments: {address:#x}')

    def literal(self, address):
        try:
            offset = self.offset(address)
            value = self.data[offset:offset + 160].split(b'\0', 1)[0]
            if len(value) >= 3 and all(byte in (9, 10, 13) or 32 <= byte < 127 for byte in value):
                return repr(value.decode())
        except ValueError:
            pass
        return ''

    def describe(self, instruction, thumb=False):
        notes = []
        if instruction.id:
            for operand in instruction.operands:
                if operand.type == ARM_OP_IMM and instruction.mnemonic.startswith(('bl', 'b')):
                    target = operand.imm & ~1
                    if target in self.by_address:
                        notes.append(self.by_address[target])
                if operand.type == ARM_OP_MEM and operand.mem.base == ARM_REG_PC:
                    pc = ((instruction.address + 4) & ~3) if thumb else instruction.address + 8
                    literal_address = pc + operand.mem.disp
                    try:
                        value = struct.unpack_from('<I', self.data, self.offset(literal_address, 4))[0]
                        notes.append(f'[{literal_address:#x}]={value:#x} ' + self.literal(value))
                    except ValueError:
                        pass
        return f'{instruction.address:08x} {instruction.bytes.hex():10s} {instruction.mnemonic:8s} {instruction.op_str}' + (' ; ' + '; '.join(notes) if notes else '')

    def disassemble(self, address, size):
        mode = CS_MODE_THUMB if address & 1 else CS_MODE_ARM
        decoder = Cs(CS_ARCH_ARM, mode)
        decoder.detail, decoder.skipdata = True, True
        address &= ~1
        offset = self.offset(address, size)
        return '\n'.join(self.describe(i, mode == CS_MODE_THUMB) for i in decoder.disasm(self.data[offset:offset + size], address))

    def direct_calls(self, address):
        calls = []
        for instruction in self.arm.disasm(self.text_data, self.text_address):
            if instruction.id and instruction.mnemonic in ('bl', 'blx'):
                if instruction.operands[0].type == ARM_OP_IMM and instruction.operands[0].imm & ~1 == address & ~1:
                    calls.append(instruction.address)
        return calls


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('elf', type=Path)
    parser.add_argument('--function', action='append', default=[])
    parser.add_argument('--address', type=lambda value: int(value, 0))
    parser.add_argument('--size', type=lambda value: int(value, 0), default=128)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    analyzer = Analyzer(args.elf)
    args.output.mkdir(parents=True, exist_ok=False)
    report = {'sha256': hashlib.sha256(analyzer.data).hexdigest(), 'symbols': analyzer.symbols, 'functions': {}}
    for name in args.function:
        symbol = analyzer.symbols[name]
        if not symbol['defined'] or not symbol['size']:
            raise ValueError('Function has no defined extent: ' + name)
        calls = analyzer.direct_calls(symbol['address'])
        filename = name.replace('/', '_')
        (args.output / (filename + '.asm')).write_text(analyzer.disassemble(symbol['address'], symbol['size']), encoding='utf-8')
        snippets = [analyzer.disassemble(site - 40, 104) for site in calls]
        (args.output / (filename + '.callers.asm')).write_text('\n\n'.join(snippets), encoding='utf-8')
        report['functions'][name] = symbol | {'file_offset': analyzer.offset(symbol['address'] & ~1), 'direct_calls': calls}
    if args.address is not None:
        (args.output / 'range.asm').write_text(analyzer.disassemble(args.address, args.size), encoding='utf-8')
    (args.output / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({key: report[key] for key in ('sha256', 'functions')}, indent=2))


if __name__ == '__main__':
    main()
