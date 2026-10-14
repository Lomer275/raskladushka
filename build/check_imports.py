import sys

sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")

import argparse
import mmap
import os
from pathlib import Path
import struct
from typing import NamedTuple


class PEError(ValueError):
    pass


class PEImports(NamedTuple):
    imports: list[str]
    delay_imports: list[str]
    managed: bool


def read_pe(path: Path) -> PEImports:
    with path.open("rb") as source:
        if os.fstat(source.fileno()).st_size == 0:
            raise PEError("empty file")
        # Mapping avoids copying large CUDA libraries into Python memory.
        with mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_READ) as data:
            def require(offset: int, size: int) -> None:
                if offset < 0 or size < 0 or offset + size > len(data):
                    raise PEError("truncated PE data")

            def unpack(fmt: str, offset: int) -> tuple:
                require(offset, struct.calcsize(fmt))
                return struct.unpack_from(fmt, data, offset)

            require(0, 64)
            if data[:2] != b"MZ":
                raise PEError("missing MZ signature")
            pe = unpack("<I", 60)[0]
            require(pe, 24)
            if data[pe:pe + 4] != b"PE\0\0":
                raise PEError("missing PE signature")
            section_count = unpack("<H", pe + 6)[0]
            optional_size = unpack("<H", pe + 20)[0]
            optional = pe + 24
            require(optional, optional_size)

            def optional_unpack(fmt: str, offset: int) -> tuple:
                if offset + struct.calcsize(fmt) > optional_size:
                    raise PEError("truncated optional header")
                return unpack(fmt, optional + offset)

            magic = optional_unpack("<H", 0)[0]
            if magic == 0x10B:
                image_base = optional_unpack("<I", 28)[0]
                directory_start = 96
            elif magic == 0x20B:
                image_base = optional_unpack("<Q", 24)[0]
                directory_start = 112
            else:
                raise PEError("unsupported optional header magic")
            directory_count = optional_unpack("<I", directory_start - 4)[0]
            if directory_start + directory_count * 8 > optional_size:
                raise PEError("truncated data directories")

            def directory(index: int) -> tuple[int, int]:
                if index >= directory_count:
                    return 0, 0
                return optional_unpack("<II", directory_start + index * 8)

            sections = []
            table = optional + optional_size
            require(table, section_count * 40)
            for index in range(section_count):
                virtual_size, rva, raw_size, raw_offset = unpack(
                    "<IIII", table + index * 40 + 8
                )
                require(raw_offset, raw_size)
                sections.append((rva, max(virtual_size, raw_size), raw_offset, raw_size))

            def locate(rva: int, size: int) -> tuple[int, int]:
                for start, span, raw, raw_size in sections:
                    if start <= rva < start + span:
                        delta = rva - start
                        if delta + size > raw_size:
                            raise PEError("RVA points outside section file data")
                        require(raw + delta, size)
                        return raw + delta, raw + raw_size
                raise PEError("RVA outside sections")

            def dll_name(rva: int) -> str:
                offset, end = locate(rva, 1)
                terminator = data.find(b"\0", offset, end)
                if terminator == -1:
                    raise PEError("unterminated DLL name")
                try:
                    name = data[offset:terminator].decode("ascii")
                except UnicodeDecodeError as error:
                    raise PEError("non-ASCII DLL name") from error
                if not name or any(ord(char) < 32 or ord(char) == 127 for char in name):
                    raise PEError("invalid DLL name")
                return name.lower()

            clr_rva, clr_size = directory(14)
            if clr_rva:
                if clr_size < 20:
                    raise PEError("truncated CLR header")
                clr_offset, _ = locate(clr_rva, clr_size)
                if unpack("<I", clr_offset + 16)[0] & 1:
                    return PEImports([], [], True)
            elif clr_size:
                raise PEError("CLR directory has no RVA")

            def imports(index: int, descriptor_size: int) -> list[str]:
                rva, size = directory(index)
                if not rva and not size:
                    return []
                if not rva:
                    raise PEError("import directory has no RVA")
                offset, _ = locate(rva, size)
                names = []
                for position in range(offset, offset + size - descriptor_size + 1,
                                      descriptor_size):
                    descriptor = unpack("<" + "I" * (descriptor_size // 4), position)
                    if not any(descriptor):
                        return names
                    if index == 13:
                        name_rva = descriptor[1]
                        if not descriptor[0] & 1:
                            name_rva -= image_base
                    else:
                        name_rva = descriptor[3]
                    names.append(dll_name(name_rva))
                raise PEError("unterminated import directory")

            return PEImports(imports(1, 20), imports(13, 32), False)


def check_folder(folder: Path, allowed: set[str]) -> tuple[list[str], int, int]:
    violations = []
    checked = 0
    skipped = 0

    def traversal_error(error: OSError) -> None:
        raise error

    for directory, _, filenames in os.walk(folder, onerror=traversal_error):
        parent = Path(directory)
        local_files = {name.lower() for name in filenames if (parent / name).is_file()}
        for filename in filenames:
            path = parent / filename
            if path.suffix.lower() not in {".exe", ".dll"}:
                continue
            relative = path.relative_to(folder).as_posix()
            try:
                pe = read_pe(path)
            except (OSError, ValueError) as error:
                reason = " ".join(str(error).splitlines())
                violations.append(f"{relative} -> unreadable: {reason}")
                continue
            if pe.managed:
                skipped += 1
                continue
            checked += 1
            for name in sorted(set(pe.imports + pe.delay_imports)):
                if (name not in local_files and name not in allowed
                        and not name.startswith(("api-ms-win-", "ext-ms-win-"))):
                    violations.append(f"{relative} -> {name}")
    return sorted(violations), checked, skipped


def main() -> int:
    parser = argparse.ArgumentParser(description="Check Windows PE DLL dependencies.")
    parser.add_argument("folder", type=Path)
    parser.add_argument("--allow", action="append", default=[], metavar="NAME")
    args = parser.parse_args()
    if not args.folder.is_dir():
        parser.error(f"folder does not exist or is not a directory: {args.folder}")
    system_list = Path(__file__).with_name("win10-system-dlls.txt")
    try:
        lines = system_list.read_text(encoding="utf-8").splitlines()
        allowed = {line.strip().lower() for line in lines
                   if line.strip() and not line.strip().startswith("#")}
        allowed.update(name.lower() for name in args.allow)
        violations, checked, skipped = check_folder(args.folder, allowed)
    except (OSError, UnicodeError) as error:
        parser.error(str(error))
    if violations:
        for violation in violations:
            print(violation)
        print(f"violations: {len(violations)}")
        return 1
    print(f"ok: {checked} files, {skipped} managed skipped")
    return 0


if __name__ == "__main__":
    sys.exit(main())
