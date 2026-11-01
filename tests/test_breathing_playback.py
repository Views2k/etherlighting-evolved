import pytest

from etherlighting_evolved.color import BLACK, Color
from etherlighting_evolved.effects import create_effect
from etherlighting_evolved.effects.base import RenderContext
from etherlighting_evolved.effects.library import Breathing


def test_old_breathing_scenes_keep_custom_playback_and_rendering():
    params = {"color": "#2468ac", "period": 7.5, "floor": 0.25, "ripple": True,
              "random_color": True, "color_transition": 11, "brightness_color": 35}
    legacy = create_effect("breathing", 8, params)
    explicit = create_effect("breathing", 8, {**params, "playback": "custom", "pace": 3})
    assert legacy.params_json()["playback"] == "custom"
    for time in (0, 0.1, 1.5, 8, 30):
        assert legacy.render(time, RenderContext(8)) == explicit.render(time, RenderContext(8))


@pytest.mark.parametrize(("pace", "period"), [(1, 3.08), (2, 2), (3, 1)])
def test_synchronized_preview_shares_phase_and_uses_fixed_color(pace, period):
    effect = create_effect("breathing", 8, {"playback": "synchronized", "pace": pace,
        "color": "#2468ac", "brightness_color": 50, "random_color": True, "period": 30,
        "floor": 0.9, "ripple": True})
    assert effect.animation_rate() == 1 / period
    chosen = Color.parse("#2468ac").scaled(0.5)
    for time in (0, period / 4, period / 2, period * 0.75, period):
        frame = effect.render(time, RenderContext(8))
        assert len(set(frame)) == 1
        assert effect.colors(time) == {"color": chosen}
        if time in (0, period):
            assert frame == [BLACK] * 8
        if time == period / 2:
            assert frame == [chosen] * 8


def test_switching_playback_preserves_all_custom_color_and_movement_settings():
    effect = create_effect("breathing", 8, {"color": "#2468ac", "period": 7.5, "floor": 0.25,
        "ripple": True, "random_color": True, "color_transition": 11, "brightness_color": 35})
    original = effect.params_json()
    effect.update({"playback": "synchronized", "pace": 3})
    effect.update({"playback": "custom"})
    assert effect.params_json() == {**original, "pace": 3}


def test_pace_updates_preserve_software_preview_phase():
    effect = create_effect("breathing", 8, {"playback": "synchronized", "pace": 2})
    before = effect.render(0.4, RenderContext(8))
    effect.update_at({"pace": 3}, 0.4)
    assert effect.render(0.4, RenderContext(8)) == before


def test_playback_and_pace_catalog_defaults_are_backward_compatible():
    params = Breathing.param_map()
    assert params["playback"].default == "custom"
    assert params["playback"].choices == ("synchronized", "custom")
    assert params["pace"].type == "int"
    assert params["pace"].default == 2
    assert (params["pace"].min, params["pace"].max, params["pace"].step) == (1, 3, 1)
    with pytest.raises(ValueError):
        create_effect("breathing", 8, {"playback": "invalid"})
