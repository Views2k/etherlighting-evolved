import asyncio
import inspect

import pytest

from etherlighting_evolved import engine, switch
from etherlighting_evolved.color import Color
from etherlighting_evolved.hardware import PRO_XG8
from etherlighting_evolved.switch import EtherlightSwitch, PortOutput, port_rgb_command
from etherlighting_evolved.transport import SimulatedTransport, TransportError


def test_command_format():
    assert port_rgb_command(3, Color(255, 0, 128), 70) == (
        """ubus call etherlight.mcu set '{"port_rgb":"3 ff0080 70"}' 2>/dev/null"""
    )
    assert port_rgb_command(1, Color(0, 0, 0), 250).endswith(""""1 000000 100"}' 2>/dev/null""")
    with pytest.raises(ValueError):
        port_rgb_command(0, Color(1, 1, 1), 50)


@pytest.mark.parametrize("level", [-1, 0, 1, 50, 100])
def test_off_is_encoded_as_black_at_a_valid_native_level(level):
    assert PortOutput.off() == PortOutput(Color(0, 0, 0), 100)
    assert PortOutput.off().preview().is_off
    assert '"port_rgb":"1 000000 100"' in port_rgb_command(1, Color(0, 0, 0), level)
    if level <= 0:
        assert '"port_rgb":"1 000000 100"' in port_rgb_command(1, Color(12, 34, 56), level)


def test_never_sends_the_commands_that_can_wedge_the_led_controller():
    source = inspect.getsource(switch) + inspect.getsource(engine)
    code = "\n".join(line for line in source.splitlines() if not line.lstrip().startswith("#"))
    for word in ("led_stop", "reset", "mode_color", "led_mode", "port_pwm", "behavior"):
        assert f'"{word}"' not in code and f"'{word}'" not in code


async def make_switch(**kw):
    sim = SimulatedTransport(latency=0)
    sw = EtherlightSwitch(sim, **kw)
    await sw.connect()
    return sim, sw


@pytest.mark.anyio
async def test_probe():
    _, sw = await make_switch()
    assert await sw.probe() == {"hostname": "USWProXG8PoE-SIM", "etherlight_mcu": True,
                                "port_rgb": True, "sfp_ports": [9, 10],
                                "native_breathing": False, "sfp_direct": True, "sfp_native_gradient": False,
                                "hardware_profile": PRO_XG8.public_dict()}


@pytest.mark.anyio
async def test_only_changes_are_sent_between_keepalives():
    sim, sw = await make_switch(keepalive=60)
    white = [PortOutput(Color(255, 255, 255), 100)] * 8
    assert await sw.write(white) == 8
    assert await sw.write(white) == 0
    changed = list(white)
    changed[2] = PortOutput(Color(255, 0, 0), 100)
    assert await sw.write(changed) == 1
    assert sim.leds[3] == ("ff0000", 100)
    assert sim.leds[4] == ("ffffff", 100)


@pytest.mark.anyio
async def test_keepalive_resends_everything():
    _, sw = await make_switch(keepalive=0)
    frame = [PortOutput.off()] * 8
    assert await sw.write(frame) == 8
    assert await sw.write(frame) == 8


@pytest.mark.anyio
@pytest.mark.parametrize("level", [0, 101])
async def test_simulator_rejects_native_levels_outside_parser_range(level):
    sim, sw = await make_switch()
    await sw.set_port(1, Color(0, 0, 255), 100)
    with pytest.raises(TransportError, match="between 1 and 100"):
        await sim.run(f"""ubus call etherlight.mcu set '{{"port_rgb":"1 000000 {level}"}}'""")
    assert sim.leds[1] == ("0000ff", 100)


@pytest.mark.anyio
@pytest.mark.parametrize("channel", [1, 2])
async def test_black_boundaries_are_sent_even_within_rgb_delta_tolerance(channel):
    sim, sw = await make_switch(num_ports=1, keepalive=60)
    dim = PortOutput(Color(0, 0, channel), 100)
    for output, expected in [
        (dim, (f"0000{channel:02x}", 100)),
        (PortOutput.off(), ("000000", 100)),
        (dim, (f"0000{channel:02x}", 100)),
    ]:
        assert await sw.write([output]) == 1
        assert sim.leds[1] == expected
        assert await sw.write([output]) == 0


@pytest.mark.anyio
async def test_links_and_poe():
    sim, sw = await make_switch()
    sim.set_link(3, False)
    sim.set_anomaly(4, 0x2)
    links = await sw.read_links()
    assert not links[3].link_up and links[1].link_up
    assert links[4].anomaly == 2
    assert links[5].uplink
    poe = await sw.read_poe()
    assert poe[2].tier == "poe+"


def test_changes_too_small_to_see_are_skipped():
    from etherlighting_evolved.switch import looks_the_same

    base = PortOutput(Color(200, 100, 50), 60)
    assert looks_the_same(base, PortOutput(Color(201, 99, 52), 61))
    assert not looks_the_same(base, PortOutput(Color(205, 100, 50), 60))
    assert not looks_the_same(base, PortOutput(Color(200, 100, 50), 62))
    assert not looks_the_same(PortOutput(Color(255, 0, 0), 1), PortOutput.off())  # turning off always goes out
    assert not looks_the_same(None, base)


@pytest.mark.anyio
async def test_slow_fades_still_reach_the_switch():
    sim, sw = await make_switch(keepalive=60)
    await sw.write([PortOutput(Color(255, 0, 0), 50)] * 8)
    sent = 0
    for level in range(51, 60):  # one level per frame: each step alone is too small to send
        sent += await sw.write([PortOutput(Color(255, 0, 0), level)] * 8)
    assert 0 < sent < 9 * 8
    assert sim.leds[1][1] >= 58


@pytest.mark.anyio
async def test_cpu_reading():
    sim, sw = await make_switch()
    first = await sw.read_cpu()
    assert first["idle"] is None and first["load"] == pytest.approx(0.4)
    await asyncio.sleep(0.05)
    second = await sw.read_cpu()
    assert 0.0 <= second["idle"] <= 1.0
