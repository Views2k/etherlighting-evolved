import pytest

from etherlighting_evolved import engine as engine_module
from etherlighting_evolved import switch as switch_module
from etherlighting_evolved.color import WHITE
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.switch import EtherlightSwitch, PortOutput
from etherlighting_evolved.transport import SimulatedTransport, TransportError


class RecordingTransport(SimulatedTransport):
    def __init__(self):
        super().__init__(latency=0)
        self.batches = []
        self.fail = False

    async def run(self, command, **kwargs):
        if kwargs.get('channel') == 'leds':
            self.batches.append(command)
            if self.fail:
                return '__EE_LED_WRITE_FAILED__\n'
        return await super().run(command, **kwargs)


@pytest.mark.anyio
async def test_only_port_colors_are_sent(monkeypatch):
    """No other etherlight.mcu settings, and nothing at all while the colors don't change."""
    clock = [10.0]
    monkeypatch.setattr(switch_module.timing, 'monotonic', lambda: clock[0])

    async def sleep(delay, wake=None):
        clock[0] += delay

    monkeypatch.setattr(switch_module.timing, 'wait', sleep)
    transport = RecordingTransport()
    sw = EtherlightSwitch(transport, keepalive=60)
    await sw.connect()
    frame = [PortOutput(WHITE, 100)] * 8
    assert await sw.write(frame) == 8
    for batch in transport.batches:
        for line in batch.splitlines():
            assert '"port_rgb"' in line
    clock[0] += 5
    assert await sw.write(frame) == 0


@pytest.mark.anyio
async def test_failed_led_write_does_not_advance_the_output_cache():
    transport = RecordingTransport()
    sw = EtherlightSwitch(transport)
    await sw.connect()
    transport.fail = True
    frame = [PortOutput(WHITE, 100)] * 8
    with pytest.raises(TransportError):
        await sw.write(frame)
    assert not sw._last
    assert sw.stats.errors == 1
    transport.fail = False
    assert await sw.write(frame) == 8


def test_changing_one_group_does_not_restart_another_groups_animation(monkeypatch):
    clock = [100.0]
    monkeypatch.setattr(engine_module.timing, 'monotonic', lambda: clock[0])
    engine = Engine(EtherlightSwitch(SimulatedTransport()), rules=[], transition=0)
    specs = [
        {'id': 'left', 'name': 'Left', 'ports': [1, 2], 'effect': {'id': 'starry_night'}},
        {'id': 'right', 'name': 'Right', 'ports': [3, 4], 'effect': {'id': 'static'}},
    ]
    engine.set_zones(specs)
    left = engine.zone('left').effect
    sampled_times = []
    original_render = left.render

    def observe(t, ctx):
        sampled_times.append(t)
        return original_render(t, ctx)

    left.render = observe
    clock[0] = 105.0
    engine.compose()
    engine.set_effect('rainbow', zone='right')
    clock[0] = 106.0
    engine.compose()
    assert sampled_times == [5.0, 6.0]


def test_editing_group_layout_preserves_unchanged_effect_instances(monkeypatch):
    clock = [100.0]
    monkeypatch.setattr(engine_module.timing, 'monotonic', lambda: clock[0])
    engine = Engine(EtherlightSwitch(SimulatedTransport()), rules=[], transition=0)
    specs = [
        {'id': 'left', 'name': 'Left', 'ports': [1, 2], 'effect': {'id': 'starry_night'}},
        {'id': 'right', 'name': 'Right', 'ports': [3, 4], 'effect': {'id': 'static'}},
    ]
    engine.set_zones(specs)
    original = engine.zone('left').effect
    specs[1]['ports'] = [3, 4, 5]
    clock[0] += 5
    engine.set_zones(specs)
    assert engine.zone('left').effect is original
