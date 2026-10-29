import inspect

import pytest

from etherlighting_evolved import engine, switch
from etherlighting_evolved.color import Color
from etherlighting_evolved.switch import EtherlightSwitch, PortOutput, port_rgb_command
from etherlighting_evolved.transport import SimulatedTransport


def test_command_format():
    assert port_rgb_command(3, Color(255, 0, 128), 70) == (
        """ubus call etherlight.mcu set '{"port_rgb":"3 ff0080 70"}' >/dev/null 2>&1"""
    )
    assert port_rgb_command(1, Color(0, 0, 0), 250).endswith(""""1 000000 100"}' >/dev/null 2>&1""")
    with pytest.raises(ValueError):
        port_rgb_command(0, Color(1, 1, 1), 50)


def test_never_sends_the_commands_that_can_wedge_the_led_controller():
    source = inspect.getsource(switch) + inspect.getsource(engine)
    code = "\n".join(line for line in source.splitlines() if not line.lstrip().startswith("#"))
    for word in ("led_stop", "reset", "mode_color", "led_mode", "port_pwm"):
        assert f'"{word}"' not in code and f"'{word}'" not in code


async def make_switch(**kw):
    sim = SimulatedTransport(latency=0)
    sw = EtherlightSwitch(sim, **kw)
    await sw.connect()
    return sim, sw


@pytest.mark.anyio
async def test_probe():
    _, sw = await make_switch()
    assert await sw.probe() == {"hostname": "USWProXG8PoE-SIM", "etherlight_mcu": True, "port_rgb": True}


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
