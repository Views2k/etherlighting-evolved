from etherlighting_evolved.links import TrafficMeter, parse_port_show

# Trimmed from a real USW-Pro-XG-8-PoE.
SAMPLE = """\
                             Packets                  Bytes
Port  Link  Rate    Receive   Transmit     Receive     Transmit   state    STP     1x Ctrl Mode  1x Status    Anomaly      LAG
----  ---- ------ ---------- ---------- ------------ ------------ ----- ---------- ------------ ------------ ------------ --------
   1  U/U  10000F   94178966   87651127  17674367305  33333228102     J forwarding unknown      disabled     0x0
   2  U/D      0H          0          0            0            0  N  J disabled   unknown                   0x0
 U 5  U/U  10000F   57404777  153980064  76376354767  64803791643     J forwarding unknown      disabled     0x0
   6  U/U   2500F       1200       3400       560000       780000     J blocking   unknown      disabled     0x4          LAG1
"""


def test_parses_link_speed_and_counters():
    ports = parse_port_show(SAMPLE)
    assert sorted(ports) == [1, 2, 5, 6]
    p1 = ports[1]
    assert p1.link_up and p1.admin_up and p1.full_duplex
    assert p1.speed_mbps == 10000
    assert p1.rx_bytes == 17674367305 and p1.tx_bytes == 33333228102
    assert p1.stp_state == "forwarding"
    assert p1.anomaly == 0 and p1.lag is None


def test_down_port_has_no_speed():
    p2 = parse_port_show(SAMPLE)[2]
    assert p2.admin_up and not p2.link_up
    assert p2.speed_mbps == 0


def test_uplink_anomaly_and_lag():
    ports = parse_port_show(SAMPLE)
    assert ports[5].uplink and not ports[1].uplink
    assert ports[6].anomaly == 4
    assert ports[6].lag == "LAG1"
    assert ports[6].stp_state == "blocking"


def test_ignores_headers_and_noise():
    assert parse_port_show("garbage\n\n----\nPort Link\n") == {}


def test_traffic_meter():
    meter = TrafficMeter()
    first = parse_port_show(SAMPLE)
    assert meter.update(first, now=0.0)[1] == 0.0
    later = SAMPLE.replace("17674367305", str(17674367305 + 125_000_000))
    rates = meter.update(parse_port_show(later), now=1.0)
    assert rates[1] == 1_000_000_000  # 125 MB in a second = 1 Gbit/s
