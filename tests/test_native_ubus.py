import pytest
from test_ubus import (
    HELLO,
    ID_ATTRIBUTE,
    LOOKUP_OK,
    OBJECT_ID,
    PATH_ATTRIBUTE,
    Connection,
    acknowledge,
    blob,
    named,
    response,
)

from etherlighting_evolved.hardware import PRO_XG8
from etherlighting_evolved.transport import SSHTransport, TransportError
from etherlighting_evolved.ubus import (
    EtherlightUbus,
    UbusError,
    _behavior_data,
    _named_attributes,
    _sfp_mode_data,
)


@pytest.mark.parametrize("port,direct,payload", [
    (9, True, b"soc_port_map steady 0 100\0"),
    (10, True, b"soc_port_map steady 0 200\0"),
    (9, False, b"blink_port_map steady 0 100\0"),
    (10, False, b"blink_port_map steady 0 200\0"),
])
def test_sfp_request_contains_only_the_explicit_single_record_mask(port, direct, payload):
    assert list(_named_attributes(_sfp_mode_data(port, direct))) == [(b"mcu_mode", 3, payload)]


@pytest.mark.parametrize("port,direct", [
    (0, True), (1, True), (8, True), (11, True), (True, True),
    (9.0, True), ("9", True), (9, 1), (9, None), (9, "true"),
])
def test_sfp_request_rejects_other_masks_and_untyped_input(port, direct):
    with pytest.raises(ValueError):
        _sfp_mode_data(port, direct)


@pytest.mark.parametrize("value", ["steady", "breath_1s", "breath_2s", "breath_3s",
                                  "gradient_1s", "gradient_2s", "gradient_3s"])
def test_behavior_setter_is_narrow(value):
    assert list(_named_attributes(_behavior_data(value))) == [(b"behavior", 3, value.encode() + b"\0")]


@pytest.mark.parametrize("value", ["gradient_0s", "gradient_4s", "blink", "steady 0 0", "steady\0reset",
                                  [], None, 8])
def test_behavior_setter_rejects_other_actions(value):
    with pytest.raises(ValueError):
        _behavior_data(value)


async def client_with_modes(kind=3):
    fields = named(5, b"port_rgb", (3).to_bytes(4, "big"))
    fields += named(5, b"behavior", kind.to_bytes(4, "big"))
    fields += named(5, b"mcu_mode", kind.to_bytes(4, "big"))
    connection = Connection()
    signature = named(2, b"set", fields)
    reader, writer = connection.channel(
        HELLO + response(2, 1, 0, PATH_ATTRIBUTE + ID_ATTRIBUTE + blob(6, signature)) + LOOKUP_OK)
    client = await EtherlightUbus.open(connection)
    client.bind_profile(PRO_XG8)
    return client, reader, writer


@pytest.mark.anyio
async def test_scoped_mode_and_behavior_use_same_exact_object_and_ack_contract():
    client, reader, writer = await client_with_modes()
    assert client.behavior_supported and client.sfp_mode_supported
    acknowledge(reader, 2)
    await client.set_sfp_mode(9, True)
    expected = ID_ATTRIBUTE + blob(4, b"set\0") + blob(7, named(3, b"mcu_mode", b"soc_port_map steady 0 100\0"))
    assert writer.writes[-1] == response(5, 2, OBJECT_ID, expected)
    acknowledge(reader, 3)
    await client.set_behavior("steady")
    expected = ID_ATTRIBUTE + blob(4, b"set\0") + blob(7, named(3, b"behavior", b"steady\0"))
    assert writer.writes[-1] == response(5, 3, OBJECT_ID, expected)
    reader.feed_data(response(2, 4, OBJECT_ID, ID_ATTRIBUTE + blob(7, named(3, b"set", b"error\0"))))
    reader.feed_data(response(1, 4, OBJECT_ID, bytes.fromhex("0100000800000000")))
    with pytest.raises(UbusError):
        await client.set_sfp_mode(9, False)
    assert writer.closed and not client.sfp_mode_supported and not client.behavior_supported


@pytest.mark.anyio
async def test_unadvertised_string_schema_sends_no_request():
    client, _, writer = await client_with_modes(5)
    assert not client.behavior_supported and not client.sfp_mode_supported
    for call in (lambda: client.set_behavior("steady"), lambda: client.set_sfp_mode(9, True)):
        with pytest.raises(UbusError):
            await call()
    assert len(writer.writes) == 1
    await client.close()


@pytest.mark.anyio
@pytest.mark.parametrize("mode", [False, True])
async def test_native_failure_closes_color_channel_without_replaying_over_shell(mode):
    client, reader, writer = await client_with_modes()
    transport = SSHTransport("fixture.invalid")
    transport._rgb_client = client
    reader.feed_data(response(1, 2, OBJECT_ID, blob(1, (2).to_bytes(4, "big"))))
    with pytest.raises(TransportError):
        if mode:
            await transport.write_sfp_mode(9, True)
        else:
            await transport.write_behavior("steady")
    assert transport._rgb_client is None and transport._rgb_failed and writer.closed
    assert len(writer.writes) == 2
