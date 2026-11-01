import asyncio
import struct

import pytest

from etherlighting_evolved.hardware import PRO_XG8
from etherlighting_evolved.ubus import EtherlightUbus, UbusError

# Wire fixtures follow OpenWrt ubusmsg.h, libubus-obj.c, and libubox/blobmsg.h.
# They are literal frames, independent of the client's encoding helpers.
OBJECT_ID = 0x12345678
HELLO = bytes.fromhex("00000000 10203040 00000004")
LOOKUP_REQUEST = bytes.fromhex(
    "00040001 00000000 00000018 02000013 65746865726c696768742e6d637500 00"
)
PATH_ATTRIBUTE = bytes.fromhex("02000013 65746865726c696768742e6d637500 00")
ID_ATTRIBUTE = bytes.fromhex("03000008 12345678")
PORT_SIGNATURE = bytes.fromhex("85000014 0008706f72745f7267620000 00000003")
SET_SIGNATURE = bytes.fromhex("82000020 0003736574000000") + PORT_SIGNATURE
SIGNATURE_ATTRIBUTE = bytes.fromhex("06000024") + SET_SIGNATURE
LOOKUP_REPLY = bytes.fromhex("00020001 00000000 00000044") + (
    PATH_ATTRIBUTE + ID_ATTRIBUTE + SIGNATURE_ATTRIBUTE
)
LOOKUP_OK = bytes.fromhex("00010001 00000000 0000000c 01000008 00000000")
SET_REQUEST = bytes.fromhex(
    "00050002 12345678 00000038 03000008 12345678 04000008 73657400 07000024 "
    "8300001d 0008706f72745f7267620000 31203132616265662031303000 000000"
)
SET_OK_DATA = ID_ATTRIBUTE + bytes.fromhex("07000014 8300000f 0003736574000000 6f6b0000")
STATUS_OK = bytes.fromhex("01000008 00000000")
SET_OK_STATUS = STATUS_OK + ID_ATTRIBUTE


def response(kind, sequence, peer, attributes=b""):
    return struct.pack("!BBHII", 0, kind, sequence, peer, len(attributes) + 4) + attributes


def blob(identifier, contents, *, extended=False):
    length = len(contents) + 4
    header = length | identifier << 24 | (0x80000000 if extended else 0)
    return header.to_bytes(4, "big") + contents + bytes(-length % 4)


def named(identifier, name, value):
    prefix = len(name).to_bytes(2, "big") + name + b"\0"
    return blob(identifier, prefix + bytes(-len(prefix) % 4) + value, extended=True)


class Writer:
    def __init__(self, reader):
        self.reader = reader
        self.writes = []
        self.closed = False
        self.changed = asyncio.Event()

    def write(self, data):
        assert not self.closed
        self.writes.append(bytes(data))
        self.changed.set()

    async def drain(self):
        pass

    def close(self):
        self.closed = True
        self.reader.feed_eof()

    async def wait_closed(self):
        pass

    async def written(self, count):
        async with asyncio.timeout(1):
            while len(self.writes) < count:
                self.changed.clear()
                await self.changed.wait()


class Connection:
    def __init__(self):
        self.channels = []
        self.paths = []
        self.closed = False

    def channel(self, incoming=b""):
        reader = asyncio.StreamReader()
        writer = Writer(reader)
        reader.feed_data(incoming)
        self.channels.append((reader, writer))
        return reader, writer

    async def open_unix_connection(self, path):
        self.paths.append(path)
        return self.channels[len(self.paths) - 1]

    def close(self):
        self.closed = True
        pytest.fail("a lighting channel must not close the shared SSH connection")


async def connected():
    connection = Connection()
    reader, writer = connection.channel(HELLO + LOOKUP_REPLY + LOOKUP_OK)
    client = await EtherlightUbus.open(connection)
    assert client.object_id == OBJECT_ID
    client.bind_profile(PRO_XG8)
    return client, reader, writer, connection


def acknowledge(reader, sequence=2):
    reader.feed_data(response(2, sequence, OBJECT_ID, SET_OK_DATA))
    reader.feed_data(response(1, sequence, OBJECT_ID, SET_OK_STATUS))


@pytest.mark.anyio
async def test_open_socket_cannot_write_without_a_qualified_profile():
    connection = Connection()
    _, writer = connection.channel(HELLO + LOOKUP_REPLY + LOOKUP_OK)
    client = await EtherlightUbus.open(connection)
    assert client.profile is None
    for operation in (
        lambda: client.set_port(1, "ffffff", 100),
        lambda: client.set_precise_port(1, (1, 2, 3)),
        lambda: client.set_sfp_precise_port(9, (1, 2, 3)),
        lambda: client.set_behavior("steady"),
        lambda: client.set_sfp_mode(9, True),
    ):
        with pytest.raises(UbusError, match="qualified"):
            await operation()
        assert writer.writes == [LOOKUP_REQUEST]
    await client.close()


@pytest.mark.anyio
async def test_rebinding_revokes_already_queued_writes():
    client, reader, writer, _ = await connected()
    first = asyncio.create_task(client.set_port(1, "12abef", 100))
    await writer.written(2)
    queued = asyncio.create_task(client.set_port(2, "ffffff", 100))
    await asyncio.sleep(0)
    client.revoke_profile()
    client.bind_profile(PRO_XG8)
    acknowledge(reader)
    await first
    with pytest.raises(UbusError, match="changed"):
        await queued
    assert len(writer.writes) == 2
    await client.close()


@pytest.mark.anyio
async def test_exact_lookup_and_invoke_frames_match_openwrt_wire_format():
    client, reader, writer, connection = await connected()
    assert connection.paths == ["/var/run/ubus/ubus.sock"]
    assert writer.writes == [LOOKUP_REQUEST]
    acknowledge(reader)
    await client.set_port(1, "12AbEF", 100)
    assert writer.writes == [LOOKUP_REQUEST, SET_REQUEST]
    await client.close()
    assert not connection.closed


@pytest.mark.anyio
@pytest.mark.parametrize("chunk_size", [1, 3, 7, 11, 19])
async def test_frame_headers_and_payloads_can_arrive_in_separate_stream_reads(chunk_size):
    connection = Connection()
    reader, writer = connection.channel()

    async def feed():
        incoming = HELLO + LOOKUP_REPLY + LOOKUP_OK
        for offset in range(0, len(incoming), chunk_size):
            reader.feed_data(incoming[offset:offset + chunk_size])
            await asyncio.sleep(0)

    feeder = asyncio.create_task(feed())
    client = await EtherlightUbus.open(connection)
    await feeder
    assert client.object_id == OBJECT_ID and writer.writes == [LOOKUP_REQUEST]
    await client.close()


@pytest.mark.anyio
@pytest.mark.parametrize("port,rgb,level", [
    (0, "123456", 100), (65, "123456", 100), (True, "123456", 100),
    (1.0, "123456", 100), ("1", "123456", 100), (1, "#123456", 100),
    (1, "12345", 100), (1, "1234567", 100), (1, "gg1234", 100),
    (1, "123456\n", 100), (1, b"123456", 100), (1, "123456", 0),
    (1, "123456", 101), (1, "123456", True), (1, "123456", 100.0),
])
async def test_invalid_color_request_writes_no_bytes_and_leaves_channel_usable(port, rgb, level):
    client, reader, writer, _ = await connected()
    with pytest.raises(ValueError):
        await client.set_port(port, rgb, level)
    assert writer.writes == [LOOKUP_REQUEST] and not writer.closed
    acknowledge(reader)
    await client.set_port(1, "12abef", 100)
    assert writer.writes[-1] == SET_REQUEST
    await client.close()


@pytest.mark.anyio
async def test_concurrent_updates_wait_for_both_data_and_status_acknowledgment():
    client, reader, writer, _ = await connected()
    first = asyncio.create_task(client.set_port(1, "12abef", 100))
    await writer.written(2)
    second = asyncio.create_task(client.set_port(2, "000000", 100))
    await asyncio.sleep(0)
    assert len(writer.writes) == 2
    reader.feed_data(response(2, 2, OBJECT_ID, SET_OK_DATA))
    await asyncio.sleep(0)
    assert not first.done() and len(writer.writes) == 2
    reader.feed_data(response(1, 2, OBJECT_ID, SET_OK_STATUS))
    await writer.written(3)
    assert first.done() and not second.done()
    acknowledge(reader, 3)
    await asyncio.gather(first, second)
    await client.close()


@pytest.mark.anyio
@pytest.mark.parametrize("mode", ["cancel", "timeout", "eof"])
async def test_interrupted_request_closes_only_its_channel(mode):
    client, reader, writer, connection = await connected()
    _, other_writer = connection.channel()
    client.timeout = 0.02
    pending = asyncio.create_task(client.set_port(1, "12abef", 100))
    await writer.written(2)
    if mode == "cancel":
        pending.cancel()
        error = asyncio.CancelledError
    else:
        error = UbusError
        if mode == "eof":
            reader.feed_eof()
    with pytest.raises(error):
        await pending
    assert writer.closed and client.object_id is None
    assert not other_writer.closed and not connection.closed
    with pytest.raises(UbusError):
        await client.set_port(1, "12abef", 100)
    assert len(writer.writes) == 2


@pytest.mark.anyio
@pytest.mark.parametrize("mode", ["cancel", "timeout"])
async def test_interrupted_greeting_closes_its_channel_without_touching_ssh(mode):
    connection = Connection()
    _, writer = connection.channel()
    _, other_writer = connection.channel()
    pending = asyncio.create_task(EtherlightUbus.open(connection, timeout=0.02))
    await asyncio.sleep(0)
    if mode == "cancel":
        pending.cancel()
        error = asyncio.CancelledError
    else:
        error = TimeoutError
    with pytest.raises(error):
        await pending
    assert writer.closed and not writer.writes
    assert not other_writer.closed and not connection.closed


@pytest.mark.anyio
async def test_canceling_a_queued_call_preserves_the_inflight_channel():
    client, reader, writer, _ = await connected()
    first = asyncio.create_task(client.set_port(1, "12abef", 100))
    await writer.written(2)
    queued = asyncio.create_task(client.set_port(2, "000000", 100))
    await asyncio.sleep(0)
    queued.cancel()
    with pytest.raises(asyncio.CancelledError):
        await queued
    assert not writer.closed and len(writer.writes) == 2
    acknowledge(reader)
    await first
    await client.close()


@pytest.mark.anyio
async def test_reconnect_uses_a_new_channel_and_reverifies_the_object():
    first, reader, writer, connection = await connected()
    reader.feed_eof()
    with pytest.raises(UbusError):
        await first.set_port(1, "12abef", 100)
    replacement_reader, replacement_writer = connection.channel(HELLO + LOOKUP_REPLY + LOOKUP_OK)
    second = await EtherlightUbus.open(connection)
    assert second is not first and writer.closed
    assert replacement_writer.writes == [LOOKUP_REQUEST]
    assert second.profile is None
    second.bind_profile(PRO_XG8)
    acknowledge(replacement_reader)
    await second.set_port(1, "12abef", 100)
    assert replacement_writer.writes[-1] == SET_REQUEST
    await second.close()


@pytest.mark.anyio
@pytest.mark.parametrize("reply", [
    response(1, 2, OBJECT_ID, STATUS_OK),  # STATUS alone does not confirm the MCU handler accepted the color.
    response(2, 2, OBJECT_ID, ID_ATTRIBUTE + blob(7, named(3, b"set", b"failed\0")))
    + response(1, 2, OBJECT_ID, STATUS_OK),
    response(2, 2, OBJECT_ID, ID_ATTRIBUTE + blob(7, named(2, b"error", named(5, b"code", b"\0\0\0\2"))))
    + response(1, 2, OBJECT_ID, STATUS_OK),
    response(2, 2, OBJECT_ID, ID_ATTRIBUTE + blob(7, named(3, b"set", b"ok\0") * 2))
    + response(1, 2, OBJECT_ID, STATUS_OK),
    response(2, 2, OBJECT_ID, ID_ATTRIBUTE + blob(7, named(3, b"set", b"ok\0") + b"\0"))
    + response(1, 2, OBJECT_ID, STATUS_OK),
    response(2, 2, OBJECT_ID, blob(3, b"\x12\x34\x56\x79") + SET_OK_DATA[8:])
    + response(1, 2, OBJECT_ID, STATUS_OK),
    response(2, 2, OBJECT_ID, SET_OK_DATA[8:])
    + response(1, 2, OBJECT_ID, STATUS_OK),
    response(2, 2, OBJECT_ID, SET_OK_DATA) + response(1, 2, OBJECT_ID, blob(1, b"\0\0\0\2")),
    response(2, 2, OBJECT_ID, SET_OK_DATA) + response(1, 2, OBJECT_ID),
    response(1, 2, OBJECT_ID, blob(1, b"\0")),
    response(1, 3, OBJECT_ID, STATUS_OK),
    response(1, 2, OBJECT_ID + 1, STATUS_OK),
    response(9, 2, OBJECT_ID, STATUS_OK),
    response(1, 2, OBJECT_ID, STATUS_OK * 2),
    response(1, 2, OBJECT_ID, blob(1, b"\0\0\0\0", extended=True)),
    response(2, 2, OBJECT_ID, SET_OK_DATA) * 32,
])
async def test_rejected_or_ambiguous_reply_fails_closed(reply):
    client, reader, writer, connection = await connected()
    reader.feed_data(reply)
    with pytest.raises(UbusError):
        await client.set_port(1, "12abef", 100)
    assert writer.closed and client.object_id is None and not connection.closed


@pytest.mark.anyio
@pytest.mark.parametrize("header", [
    bytes.fromhex("01010002 12345678 0000000c"),
    bytes.fromhex("00010002 12345678 8000000c"),
    bytes.fromhex("00010002 12345678 0100000c"),
    bytes.fromhex("00010002 12345678 00000003"),
    bytes.fromhex("00010002 12345678 00010001"),
])
async def test_invalid_frame_header_fails_before_reading_its_claimed_payload(header):
    client, reader, writer, _ = await connected()
    reader.feed_data(header)
    with pytest.raises(UbusError, match="frame"):
        await client.set_port(1, "12abef", 100)
    assert writer.closed


@pytest.mark.anyio
@pytest.mark.parametrize("attributes", [
    b"\0", bytes.fromhex("01000003"), bytes.fromhex("01000009 00000000"),
    bytes.fromhex("01000005 0000"), STATUS_OK * 257,
])
async def test_invalid_attribute_lengths_and_counts_fail_closed(attributes):
    client, reader, writer, _ = await connected()
    reader.feed_data(response(1, 2, OBJECT_ID, attributes))
    with pytest.raises(UbusError):
        await client.set_port(1, "12abef", 100)
    assert writer.closed


@pytest.mark.anyio
async def test_response_byte_budget_applies_across_data_frames():
    client, reader, writer, _ = await connected()
    large_data = response(2, 2, OBJECT_ID, blob(7, bytes(32768)))
    reader.feed_data(large_data * 2)
    with pytest.raises(UbusError, match="too large"):
        await client.set_port(1, "12abef", 100)
    assert writer.closed


@pytest.mark.anyio
async def test_sequence_wrap_uses_a_fresh_acknowledged_sequence():
    client, reader, writer, _ = await connected()
    client._sequence = 65534
    acknowledge(reader, 65535)
    await client.set_port(1, "12abef", 100)
    acknowledge(reader, 1)
    await client.set_port(1, "12abef", 100)
    assert [packet[2:4] for packet in writer.writes[-2:]] == [b"\xff\xff", b"\0\1"]
    await client.close()


@pytest.mark.anyio
@pytest.mark.parametrize("attributes", [
    blob(2, b"other.mcu\0") + ID_ATTRIBUTE + SIGNATURE_ATTRIBUTE,
    blob(2, b"etherlight.mcu\0extra") + ID_ATTRIBUTE + SIGNATURE_ATTRIBUTE,
    PATH_ATTRIBUTE + blob(3, b"\0\0\0\1") + SIGNATURE_ATTRIBUTE,
    PATH_ATTRIBUTE + blob(3, b"\x12\x34\x56") + SIGNATURE_ATTRIBUTE,
    PATH_ATTRIBUTE + ID_ATTRIBUTE,
    PATH_ATTRIBUTE + ID_ATTRIBUTE + blob(6, named(2, b"set", named(5, b"port_rgb", b"\0\0\0\5"))),
    PATH_ATTRIBUTE + ID_ATTRIBUTE + blob(6, named(2, b"get", PORT_SIGNATURE)),
    PATH_ATTRIBUTE + ID_ATTRIBUTE + blob(6, named(3, b"set", PORT_SIGNATURE)),
    PATH_ATTRIBUTE + ID_ATTRIBUTE + blob(6, named(2, b"set", named(3, b"port_rgb", b"String\0"))),
    PATH_ATTRIBUTE + ID_ATTRIBUTE + blob(6, named(2, b"set", PORT_SIGNATURE * 2)),
    PATH_ATTRIBUTE + ID_ATTRIBUTE + blob(6, SET_SIGNATURE * 2),
    PATH_ATTRIBUTE + ID_ATTRIBUTE + blob(6, SET_SIGNATURE + b"\0"),
    PATH_ATTRIBUTE + ID_ATTRIBUTE + blob(6, named(2, b"set", PORT_SIGNATURE + b"\0")),
    PATH_ATTRIBUTE + ID_ATTRIBUTE + blob(6, bytes.fromhex("82000008 ffff0000")),
])
async def test_lookup_requires_exact_object_name_and_unambiguous_string_method_schema(attributes):
    connection = Connection()
    _, writer = connection.channel(HELLO + response(2, 1, 0, attributes) + LOOKUP_OK)
    with pytest.raises(UbusError):
        await EtherlightUbus.open(connection)
    assert writer.closed and writer.writes == [LOOKUP_REQUEST] and not connection.closed


@pytest.mark.anyio
@pytest.mark.parametrize("lookup_replies", [LOOKUP_OK, LOOKUP_REPLY * 2 + LOOKUP_OK])
async def test_lookup_rejects_missing_or_multiple_object_results(lookup_replies):
    connection = Connection()
    _, writer = connection.channel(HELLO + lookup_replies)
    with pytest.raises(UbusError):
        await EtherlightUbus.open(connection)
    assert writer.closed and writer.writes == [LOOKUP_REQUEST]


@pytest.mark.anyio
@pytest.mark.parametrize("hello", [
    response(1, 0, 0x10203040), response(0, 1, 0x10203040), response(0, 0, 0), response(0, 0, 1),
])
async def test_invalid_greeting_never_sends_lookup(hello):
    connection = Connection()
    _, writer = connection.channel(hello)
    with pytest.raises(UbusError):
        await EtherlightUbus.open(connection)
    assert writer.closed and writer.writes == []


@pytest.mark.anyio
@pytest.mark.parametrize("timeout", [0, -1, 11, True, float("nan"), float("inf")])
async def test_invalid_timeout_does_not_leak_an_open_channel(timeout):
    connection = Connection()
    _, writer = connection.channel(HELLO)
    with pytest.raises(ValueError):
        await EtherlightUbus.open(connection, timeout=timeout)
    assert not connection.paths or writer.closed
    assert not writer.writes
