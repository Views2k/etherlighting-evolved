"""A bounded Etherlighting client for the existing ubus socket over SSH."""

from __future__ import annotations

import asyncio
import contextlib
import math
import re
import struct

from .hardware import PRO_XG8, HardwareProfile
from .transport import TransportError

SOCKET_PATH = "/var/run/ubus/ubus.sock"
OBJECT_PATH = b"etherlight.mcu\0"
MAX_MESSAGE = 65536


class UbusError(TransportError):
    """The lighting socket rejected a request or returned an invalid response."""


def _attribute(kind: int, data: bytes, *, extended: bool = False) -> bytes:
    length = 4 + len(data)
    header = (kind << 24) | length | (0x80000000 if extended else 0)
    raw = struct.pack("!I", header) + data
    return raw + b"\0" * (-length % 4)


def _attributes(data: bytes):
    offset = 0
    count = 0
    while offset < len(data):
        count += 1
        if count > 256 or len(data) - offset < 4:
            raise UbusError("Invalid lighting response attributes")
        header, = struct.unpack_from("!I", data, offset)
        length = header & 0x00ffffff
        padded = (length + 3) & ~3
        if length < 4 or offset + padded > len(data):
            raise UbusError("Invalid lighting response length")
        yield (header >> 24) & 0x7f, bool(header & 0x80000000), data[offset + 4:offset + length]
        offset += padded


def _plain_attributes(data: bytes) -> dict[int, bytes]:
    result = {}
    for kind, extended, value in _attributes(data):
        if extended or kind in result:
            raise UbusError("Ambiguous lighting response")
        result[kind] = value
    return result


def _named_attributes(data: bytes):
    names = set()
    for kind, extended, value in _attributes(data):
        if not extended or len(value) < 3:
            raise UbusError("Invalid lighting method schema")
        name_length, = struct.unpack_from("!H", value)
        value_start = (2 + name_length + 1 + 3) & ~3
        if value_start > len(value) or value[2 + name_length] != 0:
            raise UbusError("Invalid lighting method name")
        name = value[2:2 + name_length]
        if b"\0" in name or name in names:
            raise UbusError("Ambiguous lighting method name")
        names.add(name)
        yield name, kind, value[value_start:]


def _supports_port_rgb(signature: bytes, field: bytes = b"port_rgb") -> bool:
    for name, kind, value in list(_named_attributes(signature)):
        if name == b"set" and kind == 2:
            for argument, argument_kind, argument_value in list(_named_attributes(value)):
                if argument == field and argument_kind == 5 and argument_value == b"\0\0\0\3":
                    return True
    return False


def _set_fields(signature: bytes) -> dict[str, int] | None:
    """The arguments of the set method and their blobmsg types (3 string, 5 int32, 7 int8), or None without one."""
    for name, kind, value in list(_named_attributes(signature)):
        if name == b"set" and kind == 2:
            fields = {}
            for argument, argument_kind, argument_value in list(_named_attributes(value)):
                typed = argument_kind == 5 and len(argument_value) == 4
                fields[argument.decode("utf-8", "replace")] = struct.unpack("!I", argument_value)[0] if typed else -1
            return fields
    return None


def _port_data(port: int, rgb: str, level: int) -> bytes:
    if type(port) is not int or not 1 <= port <= 64:
        raise ValueError("port must be an integer from 1 to 64")
    if not isinstance(rgb, str) or re.fullmatch(r"[0-9a-fA-F]{6}", rgb) is None:
        raise ValueError("color must contain six RGB hex digits")
    if type(level) is not int or not 1 <= level <= 100:
        raise ValueError("native brightness must be an integer from 1 to 100")
    name = b"port_rgb"
    prefix = struct.pack("!H", len(name)) + name + b"\0"
    prefix += b"\0" * (-len(prefix) % 4)
    value = f"{port} {rgb.lower()} {level}".encode("ascii") + b"\0"
    return _attribute(3, prefix + value, extended=True)


def validate_precise_rgb(port: int, channels: tuple[int, int, int], *, profile: HardwareProfile = PRO_XG8) -> None:
    profile.validate_port(port)
    _validate_components(channels)


def validate_sfp_precise_rgb(port: int, channels: tuple[int, int, int], *, profile: HardwareProfile = PRO_XG8) -> None:
    profile.validate_port(port, optical=True)
    _validate_components(channels)


def _validate_components(channels: tuple[int, int, int]) -> None:
    if (type(channels) is not tuple or len(channels) != 3
            or any(type(c) is not int or not 0 <= c <= 65280 for c in channels)):
        raise ValueError("precise RGB requires three integers from 0 to 65280")


def _precise_port_data(port: int, channels: tuple[int, int, int], *, profile: HardwareProfile = PRO_XG8) -> bytes:
    validate_precise_rgb(port, channels, profile=profile)
    return _pwm_data(port, channels)


def _sfp_precise_data(port: int, channels: tuple[int, int, int], *, profile: HardwareProfile = PRO_XG8) -> bytes:
    validate_sfp_precise_rgb(port, channels, profile=profile)
    return _pwm_data(port, channels)


def _pwm_data(port: int, channels: tuple[int, int, int]) -> bytes:
    name = b"port_pwm"
    prefix = struct.pack("!H", len(name)) + name + b"\0"
    prefix += b"\0" * (-len(prefix) % 4)
    value = f"{port} {channels[0]} {channels[1]} {channels[2]} 0".encode("ascii") + b"\0"
    return _attribute(3, prefix + value, extended=True)


BREATH_BEHAVIORS = frozenset({"breath_1s", "breath_2s", "breath_3s"})
GRADIENT_BEHAVIORS = frozenset({"gradient_1s", "gradient_2s", "gradient_3s"})


def _string_data(name: bytes, value: str) -> bytes:
    prefix = struct.pack("!H", len(name)) + name + b"\0"
    prefix += b"\0" * (-len(prefix) % 4)
    return _attribute(3, prefix + value.encode("ascii") + b"\0", extended=True)


def _behavior_data(behavior: str) -> bytes:
    if not isinstance(behavior, str) or behavior not in BREATH_BEHAVIORS | GRADIENT_BEHAVIORS | {"steady"}:
        raise ValueError("unsupported lighting behavior")
    return _string_data(b"behavior", behavior)


def _sfp_mode_data(port: int, direct: bool, *, profile: HardwareProfile = PRO_XG8) -> bytes:
    mask = int(profile.sfp_mask(port), 16)
    if type(direct) is not bool:
        raise ValueError("SFP mode requires a Boolean ownership value")
    mode = "soc_port_map" if direct else "blink_port_map"
    return _string_data(b"mcu_mode", f"{mode} steady {mask >> 32:x} {mask & 0xffffffff:x}")


class EtherlightUbus:
    """One acknowledged request at a time; bounded per-port RGB output only."""

    def __init__(self, reader, writer, *, timeout: float = 3.0) -> None:
        if isinstance(timeout, bool) or not math.isfinite(timeout) or not 0 < timeout <= 10:
            raise ValueError("lighting timeout must be from 0 to 10 seconds")
        self.reader = reader
        self.writer = writer
        self.timeout = timeout
        self.object_id: int | None = None
        self.precise_rgb_supported = False
        self.behavior_supported = False
        self.sfp_mode_supported = False
        self.set_fields: dict[str, int] | None = None
        self.profile: HardwareProfile | None = None
        self._binding = 0
        self._sequence = 0
        self._closed = False
        self._lock = asyncio.Lock()

    def bind_profile(self, profile: HardwareProfile) -> None:
        profile.require_output()
        self._binding += 1
        self.profile = profile

    def revoke_profile(self) -> None:
        self._binding += 1
        self.profile = None

    def _require_profile(self) -> HardwareProfile:
        if self.profile is None:
            raise UbusError("The lighting connection has no qualified hardware profile")
        self.profile.require_output()
        return self.profile

    @classmethod
    async def open(cls, connection, *, timeout: float = 3.0) -> EtherlightUbus:
        if isinstance(timeout, bool) or not math.isfinite(timeout) or not 0 < timeout <= 10:
            raise ValueError("lighting timeout must be from 0 to 10 seconds")
        client = None
        try:
            async with asyncio.timeout(timeout):
                reader, writer = await connection.open_unix_connection(SOCKET_PATH)
                client = cls(reader, writer, timeout=timeout)
                kind, sequence, peer, _ = await client._receive()
                if kind != 0 or sequence != 0 or peer <= 1:
                    raise UbusError("Invalid lighting socket greeting")
                await client.lookup()
                return client
        except BaseException:
            if client is not None:
                await client.close()
            raise

    async def close(self) -> None:
        self.revoke_profile()
        self._closed = True
        self.object_id = None
        self.precise_rgb_supported = False
        self.behavior_supported = False
        self.sfp_mode_supported = False
        self.set_fields = None
        self.writer.close()
        with contextlib.suppress(Exception):
            await asyncio.wait_for(self.writer.wait_closed(), 1)

    async def _receive(self) -> tuple[int, int, int, dict[int, bytes]]:
        header = await self.reader.readexactly(12)
        version, kind, sequence, peer, blob = struct.unpack("!BBHII", header)
        length = blob & 0x00ffffff
        if version != 0 or blob >> 24 or not 4 <= length <= MAX_MESSAGE:
            raise UbusError("Invalid lighting socket frame")
        payload = await self.reader.readexactly(length - 4)
        return kind, sequence, peer, _plain_attributes(payload)

    async def _request(self, kind: int, peer: int, attributes: bytes,
                       *, binding: int | None = None) -> list[dict[int, bytes]]:
        async with self._lock:
            if self._closed:
                raise UbusError("Lighting socket is closed")
            if kind == 5:
                self._require_profile()
                if binding != self._binding:
                    raise UbusError("The lighting profile changed before the write")
            try:
                async with asyncio.timeout(self.timeout):
                    self._sequence = (self._sequence % 65535) + 1
                    header = struct.pack("!BBHI", 0, kind, self._sequence, peer)
                    self.writer.write(header + _attribute(0, attributes))
                    await self.writer.drain()
                    replies = []
                    total = 0
                    for _ in range(32):
                        response_kind, sequence, response_peer, response = await self._receive()
                        if sequence != self._sequence or response_peer != peer:
                            raise UbusError("Unexpected lighting response identity")
                        total += sum(len(value) + 4 for value in response.values())
                        if total > MAX_MESSAGE:
                            raise UbusError("Lighting response is too large")
                        if response_kind == 1:
                            status = response.get(1)
                            if status != b"\0\0\0\0":
                                raise UbusError("Lighting request was rejected")
                            return replies
                        if response_kind != 2:
                            raise UbusError("Unexpected lighting response type")
                        replies.append(response)
                    raise UbusError("Too many lighting response frames")
            except asyncio.CancelledError:
                await self.close()
                raise
            except Exception as exc:
                await self.close()
                if isinstance(exc, UbusError):
                    raise
                raise UbusError("Lighting socket did not answer") from exc

    async def lookup(self) -> None:
        replies = await self._request(4, 0, _attribute(2, OBJECT_PATH))
        if len(replies) != 1:
            await self.close()
            raise UbusError("Lighting object lookup was ambiguous")
        result = replies[0]
        object_id = result.get(3, b"")
        try:
            supported = _supports_port_rgb(result.get(6, b""))
        except UbusError:
            await self.close()
            raise
        if result.get(2) != OBJECT_PATH or len(object_id) != 4 or not supported:
            await self.close()
            raise UbusError("Per-port lighting is unavailable on this socket")
        self.object_id, = struct.unpack("!I", object_id)
        if self.object_id <= 1024:
            await self.close()
            raise UbusError("Invalid lighting object identifier")
        self.precise_rgb_supported = _supports_port_rgb(result.get(6, b""), b"port_pwm")
        self.behavior_supported = _supports_port_rgb(result.get(6, b""), b"behavior")
        self.sfp_mode_supported = _supports_port_rgb(result.get(6, b""), b"mcu_mode")
        self.set_fields = _set_fields(result.get(6, b""))

    async def set_port(self, port: int, rgb: str, level: int) -> None:
        profile = self._require_profile()
        profile.validate_port(port, optical=port in profile.optical_ports)
        data = _port_data(port, rgb, level)
        await self._set_data(data)

    async def set_precise_port(self, port: int, channels: tuple[int, int, int]) -> None:
        data = _precise_port_data(port, channels, profile=self._require_profile())
        if not self.precise_rgb_supported:
            raise UbusError("The lighting socket has not advertised precise RGB input")
        await self._set_data(data)

    async def set_sfp_precise_port(self, port: int, channels: tuple[int, int, int]) -> None:
        data = _sfp_precise_data(port, channels, profile=self._require_profile())
        if not self.precise_rgb_supported or not self.sfp_mode_supported:
            raise UbusError("The lighting socket has not advertised precise SFP input")
        await self._set_data(data)

    async def set_behavior(self, behavior: str) -> None:
        self._require_profile()
        data = _behavior_data(behavior)
        if not self.behavior_supported:
            raise UbusError("The lighting socket has not advertised behavior input")
        await self._set_data(data)

    async def set_sfp_mode(self, port: int, direct: bool) -> None:
        data = _sfp_mode_data(port, direct, profile=self._require_profile())
        if not self.sfp_mode_supported:
            raise UbusError("The lighting socket has not advertised scoped SFP input")
        await self._set_data(data)

    async def _set_data(self, data: bytes) -> None:
        if self.object_id is None:
            raise UbusError("Lighting object has not been verified")
        attributes = (_attribute(3, struct.pack("!I", self.object_id))
                      + _attribute(4, b"set\0") + _attribute(7, data))
        self._require_profile()
        replies = await self._request(5, self.object_id, attributes, binding=self._binding)
        try:
            if len(replies) != 1:
                raise UbusError("Lighting update was not acknowledged")
            reply = replies[0]
            if reply.get(3) != struct.pack("!I", self.object_id):
                raise UbusError("Lighting acknowledgment has the wrong object")
            fields = list(_named_attributes(reply.get(7, b"")))
            if fields != [(b"set", 3, b"ok\0")]:
                raise UbusError("The switch rejected the lighting update")
        except UbusError:
            await self.close()
            raise
