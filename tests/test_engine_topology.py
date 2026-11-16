from copy import deepcopy
from dataclasses import dataclass, replace

import pytest
from test_native_engine import NativeSwitch

from etherlighting_evolved.color import Color
from etherlighting_evolved.engine import Engine, NotFound
from etherlighting_evolved.sfp import SfpLight


@dataclass(frozen=True)
class Profile:
    key: str
    copper_ports: tuple[int, ...]
    optical_ports: tuple[int, ...]
    stack_ports: tuple[int, ...] = ()

    def public_dict(self):
        return {"key": self.key}


class LayoutSwitch(NativeSwitch):
    def __init__(self, copper, optical, *, model="fixture", native=False):
        super().__init__()
        self.num_ports = copper
        self.sfp_ports = tuple(optical)
        self.hardware_profile = (
            Profile(model, tuple(range(1, copper + 1)), tuple(optical)) if model is not None else None
        )
        self.native = native

    @property
    def sfp_native_gradient(self):
        return self.native


def engine_for_layout(copper=24, optical=(25, 26), **kwargs):
    return Engine(LayoutSwitch(copper, optical, **kwargs), rules=[], gamma=1, transition=0)


@pytest.mark.parametrize("copper,optical", [(8, (9, 10)), (16, (17, 18)), (24, (25, 26)),
                                             (48, (49, 50, 51, 52)), (0, (1, 2, 3, 4)), (0, ())])
def test_detected_layout_drives_scene_state_and_rendering(copper, optical):
    engine = engine_for_layout(copper, optical)
    scene = engine.snapshot()
    assert scene["version"] == 3
    assert scene["topology"] == {"version": 1, "model": "fixture", "copper_ports": list(range(1, copper + 1)),
                                  "sfp_ports": list(optical)}
    assert set(engine.sfp) == set(optical)
    assert len(engine.compose()[0]) == copper
    assert [port["port"] for port in engine.state()["sfp"]] == list(optical)
    assert engine.state()["hardware_profile"] == {"key": "fixture"}
    assert engine.budget.value > 0
    restored = engine_for_layout(copper, optical)
    restored.apply_snapshot(scene)
    assert restored.snapshot() == scene
    if not copper:
        assert not engine.zones
        with pytest.raises(NotFound):
            engine.set_effect("static")


def test_sync_uses_detected_optical_leader_and_all_qualified_ports():
    engine = engine_for_layout(48, (49, 50, 51, 52))
    engine.set_sfp(49, {"mode": "color", "color": "blue", "effect": "pulse"})
    engine.set_sfp(52, {"mode": "color", "color": "red"})
    independent = engine.sfp[52]
    engine.set_sfp_sync({"mode": "together"})
    for at in (0, 1.5, 3, 4.5):
        assert len({engine.sfp_indicators.color(port, at) for port in engine.sfp}) == 1
    before = dict(engine.sfp_indicators.revisions)
    engine.set_sfp(49, {"color": "green"})
    assert all(engine.sfp_indicators.revisions[port] > before[port] for port in engine.sfp)
    assert engine.sfp[52] == independent
    engine.set_sfp_sync({"mode": "ethernet", "source_port": 48})
    engine.set_override(48, "#1234ab", brightness=68)
    engine.compose(t=0)
    assert all(engine.sfp_indicators.color(port, 0) == (Color.parse("#1234ab"), 68) for port in engine.sfp)


def test_shared_native_plan_includes_the_detected_group_without_legacy_ids():
    engine = engine_for_layout(24, (25, 26, 27, 28), native=True)
    engine.set_sfp(25, {"mode": "color", "color": "blue", "effect": "two_color"})
    engine.set_sfp_sync({"mode": "together"})
    outputs = engine.sfp_indicators.outputs()
    assert set(outputs) == {25, 26, 27, 28}
    assert list(engine.sfp_indicators.playback._plans) == [(25, 26, 27, 28)]
    assert len(set(outputs.values())) == 1


def test_optical_only_sync_cannot_follow_a_nonexistent_ethernet_port():
    engine = engine_for_layout(0, (1, 2))
    engine.set_sfp(1, {"mode": "color", "color": "blue"})
    engine.set_sfp_sync({"mode": "together"})
    assert set(engine.sfp_indicators.outputs()) == {1, 2}
    before = engine.snapshot()
    with pytest.raises(ValueError, match="available Ethernet"):
        engine.set_sfp_sync({"mode": "ethernet"})
    assert engine.snapshot() == before
    invalid = deepcopy(before)
    invalid["sfp_sync"]["mode"] = "ethernet"
    with pytest.raises(ValueError, match="available Ethernet"):
        engine.apply_snapshot(invalid)
    assert engine.snapshot() == before


def test_unqualified_profile_optical_ports_never_become_output_settings():
    engine = engine_for_layout(24, ())
    assert engine.sfp == {} and engine.sfp_indicators.outputs() == {}
    for port in (9, 10, 25, 26):
        with pytest.raises(ValueError, match="unavailable"):
            engine.set_sfp(port, {"mode": "color"})
    engine.set_override(9, "blue")
    assert engine.compose(t=0)[0][8] == Color(0, 0, 255)


def test_scene_port_roles_cannot_cross_between_eight_and_twenty_four_port_switches():
    small = engine_for_layout(8, (9, 10), model="small")
    small.set_sfp(9, {"mode": "color", "color": "red"})
    small.set_rules([{"ports": [9], "look": {"color": "green"}}])
    large = engine_for_layout()
    before = large.snapshot()
    for legacy in (False, True):
        scene = small.snapshot()
        if legacy:
            scene["version"] = 2
            del scene["topology"]
        with pytest.raises(ValueError, match="layout|eight-port"):
            large.apply_snapshot(scene)
        assert large.snapshot() == before
    same_shape = engine_for_layout(8, (9, 10), model="different-model")
    with pytest.raises(ValueError, match="different switch"):
        same_shape.apply_snapshot(small.snapshot())


def test_legacy_eight_port_scene_migrates_with_sfp_roles_and_sync_intact():
    engine = engine_for_layout(8, (9, 10), model=None)
    engine.set_sfp(9, {"mode": "color", "effect": "pulse", "color": "blue"})
    engine.set_sfp_sync({"mode": "together"})
    legacy = engine.snapshot()
    legacy["version"] = 2
    del legacy["topology"]
    restored = engine_for_layout(8, (9, 10), model="xg8")
    restored.apply_snapshot(legacy)
    assert restored.sfp[9] == engine.sfp[9]
    assert restored.sfp_sync == engine.sfp_sync
    assert restored.snapshot()["topology"]["model"] == "xg8"
    assert restored.snapshot()["version"] == 3


def test_legacy_global_effect_migrates_but_legacy_bound_ports_do_not_change_layout():
    engine = engine_for_layout()
    engine.apply_snapshot({"effect": "static", "params": {"color": "blue"}})
    assert engine.compose(t=0)[0] == [Color(0, 0, 255)] * 24
    with pytest.raises(ValueError, match="Legacy port"):
        engine.apply_snapshot({"effect": "static", "overrides": {"9": {"color": "red"}}})


@pytest.mark.parametrize("field,value", [
    ("zones", [None]), ("zones", [{"ports": 1}]), ("zones", [{"ports": "1"}]),
    ("zones", [{"ports": [1], "effect": {"id": []}}]),
    ("zones", [{"ports": [1], "effect": {"params": []}}]),
    ("rules", None), ("rules", {}), ("rules", [{"ports": 1}]),
    ("rules", [{"conditions": {}}]), ("rules", [{"look": []}]),
    ("rules", [{"conditions": [{"field": [], "op": "is", "value": True}]}]),
    ("overrides", []), ("overrides", {"1": None}), ("overrides", {"1": {}}),
    ("overrides", {"1": {"color": "blue"}, "01": {"color": "red"}}),
    ("sfp", {"25": []}), ("sfp", {"9": {"mode": "color"}}),
    ("sfp_sync", []), ("version", True), ("version", 4), ("topology", {}),
])
def test_malformed_nested_scene_is_rejected_atomically(field, value):
    engine = engine_for_layout()
    engine.set_sfp(25, {"mode": "color", "color": "blue"})
    before = engine.snapshot()
    before_revisions = deepcopy((engine.revision, engine.sfp_indicators.revisions, engine.sfp_indicators.release_ports))
    scene = deepcopy(before)
    scene["brightness"] = 12
    scene[field] = value
    with pytest.raises(ValueError):
        engine.apply_snapshot(scene)
    assert engine.snapshot() == before
    assert (engine.revision, engine.sfp_indicators.revisions, engine.sfp_indicators.release_ports) == before_revisions


@pytest.mark.parametrize("field,value", [("copper_ports", [1, True]), ("copper_ports", [1, 1]),
                                        ("copper_ports", 24), ("sfp_ports", [9, 10]),
                                        ("sfp_ports", [25, 65]), ("model", []), ("version", True)])
def test_invalid_or_colliding_topology_is_rejected(field, value):
    engine = engine_for_layout()
    scene = engine.snapshot()
    scene["topology"][field] = value
    with pytest.raises(ValueError):
        engine.apply_snapshot(scene)


def test_scene_input_must_be_an_object():
    engine = engine_for_layout()
    for scene in (None, [], "scene", 1):
        with pytest.raises(ValueError):
            engine.apply_snapshot(scene)


@pytest.mark.parametrize("mutation", ["copper", "optical", "model"])
def test_reconnect_does_not_initialize_a_different_topology(mutation):
    engine = engine_for_layout()
    if mutation == "copper":
        engine.switch.num_ports = 48
    elif mutation == "optical":
        engine.switch.sfp_ports = (49, 50)
    else:
        engine.switch.hardware_profile = replace(engine.switch.hardware_profile, key="another-model")
    with pytest.raises(ValueError, match="changed"):
        engine._validate_switch_topology()
    assert not engine.switch.calls


def test_effect_math_accepts_other_model_optical_numbers():
    effect = SfpLight(mode="color", effect="pulse")
    for port in (1, 17, 25, 49, 52):
        assert effect.render(0.7, port) == effect.render(0.7, 9)
