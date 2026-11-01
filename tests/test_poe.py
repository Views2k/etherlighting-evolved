import pytest

from etherlighting_evolved.poe import parse_poe_show, tier_for_class
from etherlighting_evolved.transport import SimulatedTransport

SAMPLE = """\
Total Power Limit(mW): 155000

Port  OpMode      HpMode    PwrLimit   Class   PoEPwr  PwrGood  Power(W)  Voltage(V)  Current(mA)
                              (mW)
----  ------  ------------  --------  -------  ------  -------  --------  ----------  -----------
   1    Auto  Dot3bt-Type3     30500  Class 4      On     Good      3.43       53.75        64.00
   2    Auto  Dot3bt-Type3     30500  Unknown     Off      Bad      0.00        0.00         0.00
   3     Off  Dot3bt-Type3     30500  Unknown     Off      Bad      0.00        0.00         0.00
   4    Auto  Dot3bt-Type4     99900  Class 8      On     Good     71.20       53.60      1328.00
"""


@pytest.mark.parametrize(
    "pd_class, tier", [(0, "poe"), (3, "poe"), (4, "poe+"), (5, "poe++"), (6, "poe++"), (7, "poe+++"), (8, "poe+++")]
)
def test_tiers(pd_class, tier):
    assert tier_for_class(pd_class) == tier


def test_parse():
    ports = parse_poe_show(SAMPLE)
    assert sorted(ports) == [1, 2, 3, 4]
    assert ports[1].delivering and ports[1].pd_class == 4 and ports[1].tier == "poe+"
    assert ports[1].power_w == pytest.approx(3.43)
    assert ports[1].mode == "Dot3bt-Type3"
    assert ports[2].tier == "off" and ports[2].enabled
    assert not ports[3].enabled
    assert ports[4].tier == "poe+++" and ports[4].current_ma == pytest.approx(1328.0)


def test_unfamiliar_output_gives_nothing():
    assert parse_poe_show("command not found") == {}


@pytest.mark.anyio
async def test_simulator_output_parses():
    sim = SimulatedTransport()
    await sim.connect()
    ports = parse_poe_show(await sim.run("swctrl poe show"))
    assert ports[2].tier == "poe+" and ports[6].tier == "poe++" and ports[7].tier == "poe++"
    assert ports[1].tier == "off"
    assert all(port.power_w <= 60 and port.mode == "Dot3bt-Type3" for port in ports.values())
