#!/usr/bin/env python3
"""Check ELF/HEX/BIN flash layout without third-party Python packages."""
import argparse
from pathlib import Path
import struct

MEMORY = {
    "F401C": (256 * 1024, 64 * 1024),
    "F401E": (512 * 1024, 96 * 1024),
    "F411E": (512 * 1024, 128 * 1024),
}
FLASH_BASE = 0x08000000
NVS_START, NVS_END = 0x08004000, 0x08008000
RAM_BASE = 0x20000000


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_hex(path):
    memory, upper, eof = {}, 0, False
    for line in path.read_text().splitlines():
        if not line.strip():
            continue
        require(not eof and line.startswith(":"), "Invalid HEX record/record after EOF")
        record = bytes.fromhex(line[1:])
        require(len(record) >= 5 and len(record) == record[0] + 5,
                "Invalid HEX record length")
        require(sum(record) % 256 == 0, "HEX checksum mismatch")
        kind, address, data = record[3], int.from_bytes(record[1:3], "big"), record[4:-1]
        if kind == 0:
            for offset, byte in enumerate(data):
                absolute = upper + address + offset
                require(absolute not in memory, "Overlapping HEX records")
                memory[absolute] = byte
        elif kind == 1:
            require(not data and address == 0, "Invalid HEX EOF")
            eof = True
        elif kind in (2, 4):
            require(len(data) == 2 and address == 0, "Invalid HEX address record")
            upper = int.from_bytes(data, "big") << (4 if kind == 2 else 16)
        elif kind in (3, 5):
            require(len(data) == 4, "Invalid HEX entry record")
        else:
            raise ValueError(f"Unsupported HEX record type {kind}")
    require(eof and memory, "Empty/incomplete HEX")
    return memory


def check(args):
    flash_size, ram_size = MEMORY[args.chip]
    image = args.elf.read_bytes()
    require(image[:7] == b"\x7fELF\x01\x01\x01", "Expected little-endian ELF32")
    header = struct.unpack_from("<HHIIIIIHHHHHH", image, 16)
    require(header[1] == 40, "Expected ARM ELF")
    entry, phoff, phsize, phcount = header[3], header[4], header[8], header[9]
    require(phsize == 32, "Unexpected program header size")
    memory = {}
    for index in range(phcount):
        kind, offset, vaddr, paddr, filesz, memsz, flags, alignment = struct.unpack_from(
            "<IIIIIIII", image, phoff + index * phsize)
        if kind != 1 or not memsz:
            continue
        require(filesz <= memsz and offset + filesz <= len(image), "Invalid ELF segment")
        require((FLASH_BASE <= vaddr and vaddr + memsz <= FLASH_BASE + flash_size)
                or (RAM_BASE <= vaddr and vaddr + memsz <= RAM_BASE + ram_size),
                f"ELF segment outside device memory: {vaddr:#x}")
        if not filesz:
            continue
        require(FLASH_BASE <= paddr and paddr + filesz <= FLASH_BASE + flash_size,
                "ELF load image outside flash")
        require(paddr + filesz <= NVS_START or paddr >= NVS_END,
                "ELF load segment overlaps settings sector")
        for i, byte in enumerate(image[offset:offset + filesz]):
            absolute = paddr + i
            require(absolute not in memory, "Overlapping ELF load segments")
            memory[absolute] = byte
    require(memory and min(memory) == FLASH_BASE, "Missing vector table at flash base")
    hex_memory = read_hex(args.hex)
    require(memory == hex_memory, "HEX differs from ELF load image")
    vector = bytes(memory[FLASH_BASE + i] for i in range(8))
    stack, reset = struct.unpack("<II", vector)
    require(stack == RAM_BASE + ram_size, "Initial stack pointer does not match chip")
    require(reset & 1 and reset == entry, "Reset vector/ELF entry mismatch or missing Thumb bit")
    require((reset & ~1) in memory, "Reset vector points outside load image")
    binary = args.bin.read_bytes()
    require(len(binary) == max(memory) + 1 - FLASH_BASE, "Unexpected BIN size")
    require(all(byte == memory.get(FLASH_BASE + i, 0xFF) for i, byte in enumerate(binary)),
            "BIN differs from ELF or gaps are not filled with 0xff")
    print(f"OK {args.chip}: vectors, device limits, ELF/HEX/BIN agree; "
          f"HEX leaves NVS sector untouched ({len(memory)} load bytes).")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--chip", choices=MEMORY, required=True)
    for name in ("elf", "hex", "bin"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    try:
        check(args)
    except (ValueError, KeyError, OSError, struct.error) as error:
        parser.exit(1, f"Firmware validation failed: {error}\n")


if __name__ == "__main__":
    main()
