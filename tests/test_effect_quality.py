import asyncio
import re
from datetime import datetime, timedelta
from types import SimpleNamespace

import pytest

from etherlighting_evolved import switch as switch_module
from etherlighting_evolved.color import Color
from etherlighting_evolved.config import EngineConfig, load_config
from etherlighting_evolved.effects import create_effect
from etherlighting_evolved.effects.base import RenderContext
from etherlighting_evolved.effects.library import Lightning
from etherlighting_evolved.engine import Engine, to_output
from etherlighting_evolved.links import PortLink
from etherlighting_evolved.presets import PresetStore
from etherlighting_evolved.schedules import Schedule, Scheduler, ScheduleStore
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport


def largest_channel_change(a, b):
    return max(abs(x - y) for left, right in zip(a, b, strict=True)
               for x, y in zip((left.r, left.g, left.b), (right.r, right.g, right.b), strict=True))


class CaptureTransport(SimulatedTransport):
    def __init__(self):
        super().__init__(latency=0)
        self.commands = []

    async def run(self, command, **kwargs):
        self.commands.append(command)
        return await super().run(command, **kwargs)

    def port_writes(self, port):
        return [(rgb, int(level)) for command in self.commands
                for p, rgb, level in re.findall(r'"port_rgb":"(\d+) ([0-9a-f]{6}) (\d+)"', command)
                if int(p) == port]


@pytest.fixture
def command_clock(monkeypatch):
    clock = [100.0]

    async def advance(delay, wake=None):
        clock[0] += max(0.0, delay)
        await asyncio.sleep(0)

    # These sweeps verify rendered command values; pacing has separate real-time and virtual-clock tests.
    monkeypatch.setattr(switch_module, "time", SimpleNamespace(perf_counter=lambda: clock[0]))
    monkeypatch.setattr(switch_module, "timing", SimpleNamespace(monotonic=lambda: clock[0], wait=advance))


@pytest.mark.parametrize("ports", [1, 2, 8, 48, 64])
@pytest.mark.parametrize("bounce", [False, True])
def test_comet_tail_remains_continuous_through_wraps_and_turns(ports, bounce):
    effect = create_effect("comet", ports, {"color": "white", "tail": 8, "bounce": bounce, "speed": 0.5})
    ctx = RenderContext(ports)
    for position in [ports / 2, ports - 1, ports, 2 * (ports - 1)]:
        time = position / 0.5
        before = effect.render(max(0, time - 0.0001), ctx)
        after = effect.render(time + 0.0001, ctx)
        assert largest_channel_change(before, after) <= 1


@pytest.mark.parametrize("mirror", [False, True])
def test_moving_gradient_wraps_without_a_color_cut(mirror):
    effect = create_effect("gradient", 8, {"speed": 0.1, "mirror": mirror})
    ctx = RenderContext(8)
    for time in [0, 10 / 7, 10, 20]:
        assert largest_channel_change(effect.render(time - 0.0001, ctx),
                                      effect.render(time + 0.0001, ctx)) <= 1


@pytest.mark.parametrize("hour", [6, 22])
@pytest.mark.parametrize("palette", ["natural", "warm", "vivid"])
def test_daylight_night_brightness_does_not_jump_at_clock_boundaries(hour, palette):
    effect = create_effect("daylight", 8, {"palette": palette})
    boundary = datetime(2026, 1, 1, hour)
    before = effect.render(0, RenderContext(8, now=boundary - timedelta(seconds=1)))
    after = effect.render(1, RenderContext(8, now=boundary))
    assert largest_channel_change(before, after) <= 1


def test_traffic_smoothing_has_the_same_time_response_at_different_frame_rates():
    ctx = RenderContext(1, links={1: PortLink(1, True, True, 1000, True)}, traffic_bps={1: 1e9})
    outputs = []
    for fps in (4, 20):
        effect = create_effect("traffic", 1, {"smoothing": 0.9})
        for step in range(fps * 2 + 1):
            frame = effect.render(step / fps, ctx)
        outputs.append(frame)
    assert largest_channel_change(*outputs) <= 1


def test_lightning_has_no_instant_brightness_cuts():
    for time in (0, 0.25, 0.45, 0.75, 1.0, 2.5, 3.0):
        assert abs(Lightning._flash(time - 0.00001) - Lightning._flash(time + 0.00001)) < 0.001


@pytest.mark.parametrize("value", [float("nan"), float("inf"), float("-inf")])
def test_effect_numeric_parameters_reject_nonfinite_values(value):
    with pytest.raises(ValueError, match="finite"):
        create_effect("breathing", 8, {"period": value})


@pytest.mark.parametrize("effect_id,key,start,changed", [
    ("breathing", "period", 10, 8), ("color_cycle", "speed", 0.03, 0.06),
    ("rainbow", "speed", 0.1, 0), ("comet", "speed", 0.25, 0.5),
    ("scanner", "speed", 0.5, 0.25), ("marquee", "speed", 0.25, 0.5),
    ("fire", "speed", 1, 0.1), ("police", "speed", 0.4, 0.1),
    ("strobe", "rate", 0.5, 1), ("gradient", "speed", 0, 0.1),
    ("gradient", "speed", 0.1, 0), ("ripple", "speed", 0.25, 0.5),
])
def test_speed_changes_preserve_animation_position(effect_id, key, start, changed):
    effect = create_effect(effect_id, 8, {key: start})
    ctx = RenderContext(8)
    before = effect.render(18.7, ctx)
    effect.update_at({key: changed}, 18.7)
    assert largest_channel_change(before, effect.render(18.7, ctx)) <= 1


@pytest.mark.parametrize("effect_id", ["rainbow", "gradient"])
def test_pausing_and_resuming_speed_keeps_the_same_frame(effect_id):
    effect = create_effect(effect_id, 8, {"speed": 0.1})
    ctx = RenderContext(8)
    before = effect.render(12.3, ctx)
    effect.update_at({"speed": 0}, 12.3)
    assert effect.render(40, ctx) == before
    effect.update_at({"speed": 0.15}, 40)
    assert effect.render(40, ctx) == before
    assert effect.render(41, ctx) != before


@pytest.mark.parametrize("effect_id", [
    "breathing", "color_cycle", "rainbow", "comet", "scanner", "marquee", "starry_night", "fire", "gradient", "ripple",
])
@pytest.mark.parametrize("fastest", [False, True])
def test_continuous_effects_have_bounded_color_steps_at_fallback_sample_rates(effect_id, fastest):
    effect = create_effect(effect_id, 8, seed=3)
    if fastest:
        # Retain the original low-rate regression samples independently of the selectable speed range.
        effect.update({
            "breathing": {"period": 1}, "color_cycle": {"speed": 0.2}, "rainbow": {"speed": 0.2},
            "comet": {"speed": 1.5}, "scanner": {"speed": 1.25}, "marquee": {"speed": 1.25},
            "starry_night": {"rate": 2}, "fire": {"speed": 1.5}, "gradient": {"speed": 0.2},
            "ripple": {"speed": 1.5},
        }[effect_id])
    previous = None
    worst = 0
    for tick in range(225):
        rendered = effect.render(tick * 8 / 30, RenderContext(8))
        outputs = [to_output(color, 100) for color in rendered]
        # Recover approximate perceived channels after the actual RGB/level quantization.
        # This measures sampled command colors, not the physical LED response.
        channels = [((channel / 255) * (output.level / 100)) ** (1 / 2.2)
                    for output in outputs for channel in (output.color.r, output.color.g, output.color.b)]
        if previous is not None:
            worst = max(worst, max(abs(a - b) for a, b in zip(previous, channels, strict=True)))
        previous = channels
    # The optional one-second breath has fewer than four samples per cycle at this budget.
    # Its larger steps are retained as an explicit fast setting, not certified as smooth.
    assert worst <= (0.8 if effect_id == "breathing" and fastest else 0.42)


@pytest.mark.parametrize("effect_id,key,value", [
    ("color_cycle", "speed", 1), ("rainbow", "speed", 1), ("comet", "speed", 12),
    ("scanner", "speed", 12), ("marquee", "speed", 10), ("fire", "speed", 3),
    ("police", "speed", 3), ("strobe", "rate", 4), ("gradient", "speed", 3),
    ("ripple", "speed", 10), ("lightning", "rate", 2),
])
def test_restored_speed_maxima_are_selectable_and_preserve_position(effect_id, key, value):
    effect = create_effect(effect_id, 8, seed=7)
    context = RenderContext(8)
    before = effect.render(18.7, context)
    rate = effect.animation_rate()
    phase = None if rate is None else effect.phase(18.7, rate)
    effect.update_at({key: value}, 18.7)
    assert effect.p[key] == value
    assert effect.param_map()[key].max == value
    if phase is not None:
        assert effect.phase(18.7, effect.animation_rate()) == pytest.approx(phase)
    if effect_id != "strobe":
        assert effect.render(18.7, context) == before


@pytest.mark.parametrize("effect_id", [
    "breathing", "color_cycle", "rainbow", "comet", "scanner", "marquee", "fire", "police", "gradient", "ripple",
])
def test_restored_fastest_motion_has_no_discontinuous_color_cuts(effect_id):
    effect = create_effect(effect_id, 8)
    effect.update({p.key: p.min if p.key == "period" else p.max for p in effect.params
                   if p.key in ("speed", "period")})
    context = RenderContext(8)
    for tick in range(300):
        at = tick / 8
        before = effect.render(at - 0.000001, context)
        after = effect.render(at + 0.000001, context)
        assert largest_channel_change(before, after) <= 1


@pytest.mark.parametrize("rate", [0.25, 0.5, 1, 2, 3, 4])
@pytest.mark.parametrize("duty", [0.4, 0.5, 0.6])
def test_strobe_fast_edges_reach_full_color_inside_every_on_window(rate, duty):
    effect = create_effect("strobe", 8, {"rate": rate, "duty": duty, "color": "blue"})
    context = RenderContext(8)
    for cycle in range(8):
        assert effect.render((cycle + duty / 2) / rate, context) == [Color(0, 0, 255)] * 8
        assert effect.render((cycle + (1 + duty) / 2) / rate, context) == [Color(0, 0, 0)] * 8


@pytest.mark.parametrize("effect_id", ["breathing", "color_cycle", "comet", "scanner", "marquee", "ripple"])
def test_slow_fade_settings_have_small_steps_at_the_eight_port_budget(effect_id):
    params = {"period": 10} if effect_id == "breathing" else {"speed": 0.03 if effect_id == "color_cycle" else 0.25}
    effect = create_effect(effect_id, 8, params, seed=3)
    previous = None
    for tick in range(900):
        outputs = [to_output(color, 100) for color in effect.render(tick * 8 / 30, RenderContext(8))]
        channels = [((channel / 255) * output.level / 100) ** (1 / 2.2)
                    for output in outputs for channel in (output.color.r, output.color.g, output.color.b)]
        if previous is not None:
            assert max(abs(a - b) for a, b in zip(previous, channels, strict=True)) <= 0.105
        previous = channels


@pytest.mark.anyio
@pytest.mark.parametrize("effect_id", ["breathing", "comet", "ripple"])
@pytest.mark.parametrize("dimming", [None, "level"])
async def test_fades_reach_port_rgb_as_rgb_amplitude(effect_id, dimming, command_clock):
    transport = CaptureTransport()
    switch = EtherlightSwitch(transport, keepalive=60)
    await switch.connect()
    options = {} if dimming is None else {"dimming": dimming}
    engine = Engine(switch, effect=effect_id, effect_params={"color": "#0000ff"},
                    rules=[], transition=0, **options)
    for tick in range(225):
        _, outputs = engine.compose(t=tick * 8 / 30)
        await switch.write(outputs)
    writes = [match for command in transport.commands
              for match in re.findall(r'"port_rgb":"(\d+) ([0-9a-f]{6}) (\d+)"', command)]
    for port in range(1, 9):
        samples = [(int(rgb, 16), int(level)) for p, rgb, level in writes if int(p) == port]
        values = [rgb for rgb, _ in samples]
        assert max(values) - min(values) >= 230
        assert all(level == 100 for rgb, level in samples)
    assert engine.dimming == "rgb"
    await switch.close()


@pytest.mark.anyio
@pytest.mark.parametrize("control", ["global", "manual", "rule", "scheduled"])
async def test_every_brightness_control_reaches_actual_rgb_commands(control, tmp_path, command_clock):
    transport = CaptureTransport()
    switch = EtherlightSwitch(transport, keepalive=60)
    await switch.connect()
    engine = Engine(switch, effect="static", effect_params={"color": "#0000ff"}, rules=[], transition=0)
    scheduler = Scheduler(ScheduleStore(tmp_path / "schedules.json"), PresetStore(tmp_path / "presets.json"),
                          SimpleNamespace(engine=engine, schedule_identity=lambda: "demo"))
    # Includes the low-end quantization boundary: 1% rounds to black with gamma 2.2.
    for brightness, expected_blue in [(0, 0), (1, 0), (15, 4), (50, 55), (100, 255)]:
        if control == "global":
            engine.set_brightness(brightness)
        elif control == "manual":
            for port in range(1, 9):
                engine.set_override(port, "#0000ff", brightness)
        elif control == "rule":
            engine.set_rules([{"name": "Night dim", "look": {"style": "effect", "brightness": brightness}}])
        else:
            scheduler.run(Schedule.parse({"at": "23:00", "device_binding": "demo",
                                          "action": {"type": "brightness", "value": brightness}}))
            assert scheduler.history[0]["result"] == "ok"
        _, outputs = engine.compose(t=0)
        await switch.write(outputs)
        expected = (f"0000{expected_blue:02x}", 100)
        assert all(transport.port_writes(port)[-1] == expected for port in range(1, 9))
    await switch.close()


@pytest.mark.anyio
@pytest.mark.parametrize("brightness,peak", [(0, 0), (1, 0), (15, 4), (50, 55), (100, 255)])
async def test_brightness_preserves_fade_amplitude_in_commands(brightness, peak, command_clock):
    transport = CaptureTransport()
    switch = EtherlightSwitch(transport, keepalive=60)
    await switch.connect()
    engine = Engine(switch, effect="breathing", effect_params={"color": "#0000ff", "floor": 0},
                    brightness=brightness, rules=[], transition=0)
    for tick in range(75):
        _, outputs = engine.compose(t=tick * 8 / 30)
        await switch.write(outputs)
    samples = transport.port_writes(1)
    blues = {int(rgb, 16) for rgb, _ in samples}
    assert min(blues) == 0
    assert max(0, peak - 2) <= max(blues) <= peak  # sent-frame cache tolerates two RGB code points
    assert all(level == 100 for rgb, level in samples)
    await switch.close()


def test_legacy_level_config_uses_rgb_fades_without_rewriting_the_file(tmp_path):
    path = tmp_path / "legacy.toml"
    content = '[engine]\ndimming = "level"\n'
    path.write_text(content, encoding="utf-8")
    assert load_config(path).engine.dimming == "rgb"
    assert EngineConfig().dimming == "rgb"
    assert path.read_text(encoding="utf-8") == content


@pytest.mark.parametrize("rate", [0.25, 0.5, 0.75, 1.0])
@pytest.mark.parametrize("duty", [0.4, 0.5, 0.6])
def test_strobe_pulses_are_not_lost_at_supported_rates(rate, duty):
    effect = create_effect("strobe", 8, {"rate": rate, "duty": duty})
    ctx = RenderContext(8)
    for offset in (0, 0.03, 0.11, 0.2):
        peaks = [0.0] * 8
        for tick in range(int(8 / rate * 30 / 8) + 2):
            time = offset + tick * 8 / 30
            cycle = int(time * rate)
            if cycle < 8:
                peaks[cycle] = max(peaks[cycle], effect.render(time, ctx)[0].max_channel / 255)
        assert min(peaks) >= 0.4
