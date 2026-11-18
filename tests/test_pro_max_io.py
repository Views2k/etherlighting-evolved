"""Compile the portable native transport core and exercise injected syscall results."""

import ctypes
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

from etherlighting_evolved.pro_max import PRO_MAX_LAYOUTS, crc8, fade_packet, scoped_commit_packet, steady_packet

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "tools/device-helpers/pro-max-io.c"
LAYOUTS = tuple(PRO_MAX_LAYOUTS.values())
RGBW = (0x1234, 0xabcd, 0, 0xffff)
OK, INVALID, IO_ERROR, INCOMPLETE = 0, -1, -2, -3


class Command(ctypes.Structure):
    _fields_ = [("sysid", ctypes.c_uint16), ("board", ctypes.c_uint8),
                ("request", ctypes.c_uint8 * 16), ("request_length", ctypes.c_uint8),
                ("read_length", ctypes.c_uint8)]


class Message(ctypes.Structure):
    _fields_ = [("address", ctypes.c_uint16), ("flags", ctypes.c_uint16),
                ("length", ctypes.c_uint16), ("data", ctypes.POINTER(ctypes.c_uint8))]


Sink = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.POINTER(Message), ctypes.c_uint)


def run_process(command, **kwargs):
    return subprocess.run(command, capture_output=True, text=True, timeout=60, check=False,
                          creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0, **kwargs)


def windows_compiler():
    compiler = shutil.which("cl")
    if compiler:
        return compiler, os.environ.copy()
    vswhere = (Path(os.environ.get("PROGRAMFILES(X86)", "C:/Program Files (x86)"))
               / "Microsoft Visual Studio/Installer/vswhere.exe")
    if not vswhere.is_file():
        pytest.fail("The native Pro Max fixtures require an existing MSVC C compiler; no tools are installed by tests")
    found = run_process([str(vswhere), "-latest", "-products", "*", "-requires",
                         "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"])
    if found.returncode or not found.stdout.strip():
        pytest.fail("No installed MSVC x64 toolchain was found for the native Pro Max fixtures")
    setup = Path(found.stdout.strip()) / "VC/Auxiliary/Build/vcvars64.bat"
    if not setup.is_file() or '"' in str(setup):
        pytest.fail("MSVC's existing environment setup is unavailable")
    shell = os.environ.get("COMSPEC", "cmd.exe")
    if '"' in shell:
        pytest.fail("The Windows command processor path is invalid")
    setup_env = os.environ.copy()
    setup_env["PATH"] = str(Path(os.environ["SYSTEMROOT"]) / "System32")
    loaded = run_process(f'"{shell}" /d /s /c "call "{setup}" >nul && set"', env=setup_env)
    if loaded.returncode:
        pytest.fail("MSVC environment setup failed")
    env = os.environ.copy()
    for line in loaded.stdout.splitlines():
        name, separator, value = line.partition("=")
        if separator and name.upper() in {"PATH", "INCLUDE", "LIB", "LIBPATH"}:
            env[name.upper()] = value
    compiler = shutil.which("cl", path=env.get("PATH"))
    if not compiler:
        pytest.fail("MSVC setup did not provide the existing C compiler")
    return compiler, env


@pytest.fixture(scope="session")
def native_io(tmp_path_factory):
    directory = tmp_path_factory.mktemp("pro-max-native-io")
    if os.name == "nt":
        compiler, env = windows_compiler()
        library = directory / "pro-max-io.dll"
        command = [compiler, "/nologo", "/std:c11", "/W4", "/WX", "/LD", "/MT",
                   "/DEE_PRO_MAX_TESTING=1", str(SOURCE), f"/Fe:{library}",
                   f"/Fo:{directory / 'pro-max-io.obj'}", "/link", "/NOLOGO",
                   f"/IMPLIB:{directory / 'pro-max-io.lib'}"]
    else:
        compiler = shutil.which("cc") or shutil.which("clang") or shutil.which("gcc")
        if compiler is None:
            pytest.fail("The native Pro Max fixtures require an existing C compiler; no tools are installed by tests")
        env = os.environ.copy()
        library = directory / ("pro-max-io.dylib" if sys.platform == "darwin" else "pro-max-io.so")
        shared = "-dynamiclib" if sys.platform == "darwin" else "-shared"
        command = [compiler, "-std=c11", "-Wall", "-Wextra", "-Werror", "-fPIC", shared,
                   "-DEE_PRO_MAX_TESTING=1", str(SOURCE), "-o", str(library)]
    result = run_process(command, env=env, cwd=directory)
    assert result.returncode == 0, result.stdout + result.stderr
    module = ctypes.CDLL(str(library))
    module.ee_pm_validate.argtypes = [ctypes.POINTER(Command)]
    module.ee_pm_validate.restype = ctypes.c_int
    module.ee_pm_parse.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_char_p), ctypes.POINTER(Command)]
    module.ee_pm_parse.restype = ctypes.c_int
    module.ee_pm_transfer.argtypes = [ctypes.POINTER(Command), ctypes.POINTER(ctypes.c_uint8), ctypes.c_size_t,
                                     Sink, ctypes.c_void_p, ctypes.POINTER(ctypes.c_int)]
    module.ee_pm_transfer.restype = ctypes.c_int
    return module


def operation(layout, request, read_length=0):
    assert len(request) <= 16
    value = Command()
    value.sysid, value.board = int(layout.sysid, 16), layout.board_id
    value.request_length, value.read_length = len(request), read_length
    value.request[:len(request)] = request
    return value


class MockTransport:
    def __init__(self, native, result=None, reply=bytes(range(16))):
        self.native, self.result, self.reply = native, result, reply
        self.calls = []
        self.callback = Sink(self.dispatch)

    def dispatch(self, _context, messages, count):
        self.calls.append([(message.address, message.flags, message.length,
                            ctypes.string_at(message.data, message.length)) for message in messages[:count]])
        if count == 2:
            ctypes.memmove(messages[1].data, self.reply, min(messages[1].length, len(self.reply)))
        return count if self.result is None else self.result

    def transfer(self, command, capacity=16):
        reply = (ctypes.c_uint8 * 16)(*[0xa5] * 16)
        returned = ctypes.c_int(999)
        result = self.native.ee_pm_transfer(ctypes.byref(command), reply, capacity, self.callback,
                                            None, ctypes.byref(returned))
        return result, bytes(reply), returned.value


@pytest.mark.parametrize("layout", LAYOUTS, ids=lambda value: value.key)
def test_native_steady_transfer_matches_all_connector_packets(native_io, layout):
    transport = MockTransport(native_io)
    for port in range(1, layout.port_count + 1):
        for apply in (False, True):
            packet = steady_packet(layout, port, RGBW, apply=apply)
            result, reply, returned = transport.transfer(operation(layout, packet))
            assert result == OK and returned == 1 and reply == bytes([0xa5] * 16)
            assert transport.calls[-1] == [(0x66, 0, 13, packet)]
    assert len(transport.calls) == layout.port_count * 2


@pytest.mark.parametrize("layout", LAYOUTS, ids=lambda value: value.key)
def test_native_masked_and_fade_packets_preserve_exact_frame(native_io, layout):
    ports = tuple(range(1, layout.port_count + 1))
    packets = [scoped_commit_packet(layout, ports)]
    packets += [fade_packet(layout, ports, seconds=seconds, mode=mode)
                for seconds in (1, 2, 3) for mode in (0, 1)]
    transport = MockTransport(native_io)
    for packet in packets:
        assert transport.transfer(operation(layout, packet))[0] == OK
        assert transport.calls[-1] == [(0x66, 0, 16, packet)]


@pytest.mark.parametrize("register,length", [(0x00, 1), (0x10, 9), (0x24, 15), (0x52, 2)])
def test_pointer_read_is_one_two_message_transfer(native_io, register, length):
    transport = MockTransport(native_io)
    result, reply, returned = transport.transfer(operation(LAYOUTS[0], bytes((register,)), length))
    assert result == OK and returned == 2
    assert len(transport.calls) == 1
    assert transport.calls[0] == [(0x66, 0, 1, bytes((register,))), (0x66, 1, length, bytes(length))]
    assert reply[:length] == bytes(range(length)) and reply[length:] == bytes([0xa5] * (16 - length))


def test_selection_stays_separate_from_snapshot_pointer_read(native_io):
    transport = MockTransport(native_io)
    assert transport.transfer(operation(LAYOUTS[0], b"\x10\x11"))[0] == OK
    assert transport.transfer(operation(LAYOUTS[0], b"\x10", 9))[0] == OK
    assert transport.calls[0] == [(0x66, 0, 2, b"\x10\x11")]
    assert len(transport.calls[1]) == 2
    assert transport.transfer(operation(LAYOUTS[0], b"\x10\x11", 9))[0] == INVALID
    assert len(transport.calls) == 2


@pytest.mark.parametrize("read_length", [0, 2, 9, 15])
@pytest.mark.parametrize("returned", [-145, -122, -5, -4, -1, 0, 3])
def test_uncertain_syscall_never_retries_or_releases_reply(native_io, read_length, returned):
    request = {0: steady_packet(LAYOUTS[0], 1, RGBW), 2: b"\x52", 9: b"\x10", 15: b"\x24"}[read_length]
    transport = MockTransport(native_io, result=returned)
    status, reply, actual = transport.transfer(operation(LAYOUTS[0], request, read_length))
    assert status == (IO_ERROR if returned < 0 else INCOMPLETE)
    assert actual == returned and reply == bytes([0xa5] * 16) and len(transport.calls) == 1


def test_one_of_two_messages_is_not_a_successful_read(native_io):
    transport = MockTransport(native_io, result=1)
    result, reply, returned = transport.transfer(operation(LAYOUTS[0], b"\x10", 9))
    assert result == INCOMPLETE and returned == 1 and reply == bytes([0xa5] * 16)
    assert len(transport.calls) == 1


@pytest.mark.parametrize("layout", LAYOUTS, ids=lambda value: value.key)
def test_invalid_records_masks_and_board_pairs_never_call_sink(native_io, layout):
    packets = [bytes((0x10, layout.port_count))]
    bad_record = bytearray(steady_packet(layout, 1, RGBW))
    bad_record[1] = layout.port_count
    bad_record[-1] = crc8(bytes(bad_record[1:-1]))
    packets.append(bytes(bad_record))
    for mask in (0, 1 << layout.port_count, 1 << 55):
        packet = b"\x25" + bytes(7) + mask.to_bytes(7, "big")
        packets.append(packet + bytes((crc8(packet[1:]),)))
    transport = MockTransport(native_io)
    for packet in packets:
        assert transport.transfer(operation(layout, packet))[0] == INVALID
    value = operation(layout, steady_packet(layout, 1, RGBW))
    value.board = (layout.board_id % 3) + 1
    assert transport.transfer(value)[0] == INVALID
    value.board, value.sysid = layout.board_id, 0xed76
    assert transport.transfer(value)[0] == INVALID
    assert not transport.calls


@pytest.mark.parametrize("register", [0x01, 0x11, 0x18, 0x19, 0x1a, 0x1f, 0x20, 0x25, 0xff])
def test_arbitrary_registers_and_global_commit_never_call_sink(native_io, register):
    transport = MockTransport(native_io)
    for length in (0, 1, 2, 9, 16, 17, 255):
        assert transport.transfer(operation(LAYOUTS[0], bytes((register,)), length))[0] == INVALID
    assert not transport.calls


def test_crc_reserved_fields_behavior_apply_and_lengths_are_checked_in_core(native_io):
    transport = MockTransport(native_io)
    packet = steady_packet(LAYOUTS[0], 1, RGBW)
    for index, value in ((2, 9), (11, 2), (0, 0x21)):
        invalid = bytearray(packet)
        invalid[index] = value
        invalid[-1] = crc8(bytes(invalid[1:-1]))
        assert transport.transfer(operation(LAYOUTS[0], bytes(invalid)))[0] == INVALID
    for source in (packet, scoped_commit_packet(LAYOUTS[0], (1,)), fade_packet(LAYOUTS[0], (1,), seconds=1)):
        invalid = bytearray(source)
        invalid[-1] ^= 1
        assert transport.transfer(operation(LAYOUTS[0], bytes(invalid)))[0] == INVALID
        for length in (0, 1, 2, 12, 14, 17, 255):
            value = operation(LAYOUTS[0], source)
            value.request_length = length
            assert transport.transfer(value)[0] == INVALID
    for source, index, changed in ((scoped_commit_packet(LAYOUTS[0], (1,)), 1, 1),
                                    (fade_packet(LAYOUTS[0], (1,), seconds=1), 1, 0),
                                    (fade_packet(LAYOUTS[0], (1,), seconds=1), 2, 8),
                                    (fade_packet(LAYOUTS[0], (1,), seconds=1), 3, 1)):
        invalid = bytearray(source)
        invalid[index] = changed
        invalid[-1] = crc8(bytes(invalid[1:-1]))
        assert transport.transfer(operation(LAYOUTS[0], bytes(invalid)))[0] == INVALID
    assert not transport.calls


def test_insufficient_reply_capacity_and_null_inputs_never_call_sink(native_io):
    transport = MockTransport(native_io)
    value = operation(LAYOUTS[0], b"\x10", 9)
    assert transport.transfer(value, capacity=8)[0] == INVALID
    returned = ctypes.c_int(99)
    assert native_io.ee_pm_transfer(ctypes.byref(value), None, 16, transport.callback,
                                    None, ctypes.byref(returned)) == INVALID
    assert returned.value == 0
    assert native_io.ee_pm_transfer(None, None, 0, transport.callback, None, None) == INVALID
    assert native_io.ee_pm_validate(None) == INVALID
    value = operation(LAYOUTS[0], b"\x10\x00")
    assert native_io.ee_pm_transfer(ctypes.byref(value), None, 0, Sink(), None, None) == INVALID
    assert not transport.calls


def parse(native, args):
    argv = (ctypes.c_char_p * len(args))(*[value.encode("ascii") for value in args])
    result = Command()
    status = native.ee_pm_parse(len(args), argv, ctypes.byref(result))
    return status, result


@pytest.mark.parametrize("layout", LAYOUTS, ids=lambda value: value.key)
def test_cli_only_constructs_allowed_operations(native_io, layout):
    prefix = ["pro-max-io", layout.sysid, str(layout.board_id)]
    for port in (1, layout.port_count):
        status, value = parse(native_io, [*prefix, "select", str(port)])
        assert status == OK and value.request_length == 2 and value.read_length == 0
        assert bytes(value.request[:2]) == bytes((0x10, port - 1))
    for name, register, length in (("snapshot", 0x10, 9), ("acceptance", 0x52, 2),
                                   ("masked-acceptance", 0x24, 15), ("mode-observation", 0x00, 1)):
        status, value = parse(native_io, [*prefix, name])
        assert status == OK and value.request_length == 1 and value.read_length == length
        assert value.request[0] == register
    packet = steady_packet(layout, layout.port_count, RGBW)
    status, value = parse(native_io, [*prefix, "packet", packet.hex()])
    assert status == OK and bytes(value.request[:value.request_length]) == packet


@pytest.mark.parametrize("args", [[], ["tool"], ["tool", "ed71", "3"],
                                  ["tool", "ed71", "3", "snapshot", "extra"],
                                  ["tool", "ed71", "3", "select", "1", "extra"],
                                  ["tool", "ed71", "3", "packet"],
                                  ["tool", "ed71", "3", "packet", "1f00"],
                                  ["tool", "ed71", "3", "packet", "g" * 26],
                                  ["tool", "ed71", "3", "packet", "0" * 33],
                                  ["tool", "ed71", "3", "read", "10"],
                                  ["tool", "ed71", "3", "snapshot; reboot"],
                                  ["tool", "ed71", "3", "--device=/dev/i2c-0"],
                                  ["tool", "ed71", "3", "--address=0x67"],
                                  ["tool", "ed71", "3", "select", "0"],
                                  ["tool", "ed71", "3", "select", "01"],
                                  ["tool", "ed71", "3", "select", "19"],
                                  ["tool", "ed71", "3", "select", "1x"],
                                  ["tool", "ed71", "3", "select", "-1"],
                                  ["tool", "ed71", "3", "select", "999"],
                                  ["tool", "0xed71", "3", "snapshot"],
                                  ["tool", "ed71", "03", "snapshot"],
                                  ["tool", "ed71", "2", "snapshot"],
                                  ["tool", "ed76", "3", "snapshot"]])
def test_cli_rejects_malformed_or_excess_fields(native_io, args):
    assert parse(native_io, args)[0] == INVALID


def test_cli_null_fields_leave_output_unchanged(native_io):
    output = Command()
    ctypes.memset(ctypes.byref(output), 0xa5, ctypes.sizeof(output))
    expected = bytes(output)
    valid = [b"pro-max-io", b"ed71", b"3", b"snapshot"]
    for index in range(len(valid)):
        values = valid.copy()
        values[index] = None
        argv = (ctypes.c_char_p * len(values))(*values)
        assert native_io.ee_pm_parse(len(values), argv, ctypes.byref(output)) == INVALID
        assert bytes(output) == expected
    assert native_io.ee_pm_parse(4, None, ctypes.byref(output)) == INVALID
    assert bytes(output) == expected
    argv = (ctypes.c_char_p * len(valid))(*valid)
    assert native_io.ee_pm_parse(4, argv, None) == INVALID


def test_test_library_has_no_device_entrypoint(native_io):
    with pytest.raises(AttributeError):
        _ = native_io.main
    with pytest.raises(AttributeError):
        _ = native_io.linux_transfer
