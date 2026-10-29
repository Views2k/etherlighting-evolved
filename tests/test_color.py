import pytest

from etherlighting_evolved.color import BLACK, WHITE, Color


@pytest.mark.parametrize(
    "value, expected",
    [
        ("#ff8000", (255, 128, 0)),
        ("ff8000", (255, 128, 0)),
        ("#f80", (255, 136, 0)),
        ("white", (255, 255, 255)),
        ([1, 2, 3], (1, 2, 3)),
        ({"r": 9, "g": 8, "b": 7}, (9, 8, 7)),
    ],
)
def test_parse(value, expected):
    c = Color.parse(value)
    assert (c.r, c.g, c.b) == expected


@pytest.mark.parametrize("bad", ["#12", "nope", [1, 2], "#gggggg"])
def test_parse_rejects_garbage(bad):
    with pytest.raises(ValueError):
        Color.parse(bad)


def test_hex_round_trip():
    assert Color.parse("#0a84ff").to_hex() == "#0a84ff"


def test_hsv_round_trip():
    c = Color.from_hsv(0.6, 0.8, 0.9)
    h, s, v = c.to_hsv()
    assert Color.from_hsv(h, s, v) == c


def test_lerp_and_scale():
    assert BLACK.lerp(WHITE, 0.5) == Color(128, 128, 128)
    assert WHITE.scaled(0) == BLACK
    assert WHITE.max_channel == 255
    assert BLACK.is_off
