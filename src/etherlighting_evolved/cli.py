"""Command line. `etherlighting-evolved` runs the server; `eectl` has tools for checking and testing a switch.

    etherlighting-evolved                 start the server, then log in from the UI
    etherlighting-evolved --demo          start logged in to the built-in simulator
    eectl probe 192.168.1.2 -u admin      check a switch works with EE
    eectl set 192.168.1.2 -u admin 3 ff00ff
    eectl benchmark 192.168.1.2 -u admin
    eectl effects
"""

from __future__ import annotations

import argparse
import asyncio
import getpass
import logging
import os
import sys
from pathlib import Path

from . import DISCLAIMER, PROJECT_NAME, TAGLINE, __version__
from .color import Color
from .config import EXAMPLE_CONFIG, data_dir, load_config
from .effects import list_effects
from .switch import EtherlightSwitch
from .transport import HostKeyStore, SSHTransport, TransportError


def _serve(args: argparse.Namespace) -> int:
    import uvicorn

    from .api import create_app

    config = load_config(args.config)
    host = args.host or config.api.host
    port = args.port or config.api.port
    logging.basicConfig(level=args.log_level.upper(), format="%(asctime)s %(levelname)-7s %(name)s: %(message)s")

    print(f"{PROJECT_NAME} {__version__}  ·  {TAGLINE}")
    print(DISCLAIMER)
    shown = "localhost" if host in ("127.0.0.1", "0.0.0.0") else host
    print(f"\nOpen http://{shown}:{port} and log in to your switch.")
    if args.demo:
        print("Demo mode: logged in to the simulator, no switch needed.")
    print()

    app = create_app(config, demo=args.demo, autoconnect=not args.no_autoconnect)
    uvicorn.run(app, host=host, port=port, log_level=args.log_level.lower())
    return 0


def _switch(args: argparse.Namespace) -> EtherlightSwitch:
    password = args.password if args.password is not None else os.environ.get("EE_PASSWORD")
    if password is None and not args.key_file:
        password = getpass.getpass(f"SSH password for {args.username}@{args.host}: ")
    transport = SSHTransport(
        args.host,
        port=args.ssh_port,
        username=args.username or None,
        password=password or None,
        key_file=args.key_file,
        host_keys=HostKeyStore(data_dir() / "known_switches.json"),
    )
    return EtherlightSwitch(transport, num_ports=args.ports)


async def _probe(args: argparse.Namespace) -> int:
    switch = _switch(args)
    await switch.connect()
    try:
        info = await switch.probe()
        links = await switch.read_links()
        poe = await switch.read_poe()
    finally:
        await switch.close()

    print(f"Switch          {info['hostname'] or '?'}")
    print(f"etherlight.mcu  {'yes' if info['etherlight_mcu'] else 'no'}")
    print(f"port_rgb        {'yes' if info['port_rgb'] else 'no'}")
    print()
    print("Port  Link   Speed   PoE      Anomaly")
    for port in range(1, args.ports + 1):
        link, p = links.get(port), poe.get(port)
        state = "up" if link and link.link_up else "down"
        speed = f"{link.speed_mbps}M" if link and link.link_up else "-"
        tier = p.tier if p else "-"
        anomaly = hex(link.anomaly) if link else "-"
        print(f"{port:>4}  {state:<5} {speed:>7}   {tier:<8} {anomaly}")
    if not poe:
        print("\nNo PoE status read. If your switch has PoE, please open an issue with the output of")
        print("`swctrl poe show` so EE can learn its format.")
    if not info["port_rgb"]:
        print("\nThis switch doesn't expose per-port RGB control, so EE can't drive it.")
        return 1
    return 0


async def _set(args: argparse.Namespace) -> int:
    switch = _switch(args)
    await switch.connect()
    try:
        await switch.set_port(args.port, Color.parse(args.color), args.level)
    finally:
        await switch.close()
    print(f"Port {args.port} set to {Color.parse(args.color).to_hex()} at {args.level}%.")
    print("The switch's own Etherlighting takes the port back within a few seconds; run the server to keep it.")
    return 0


async def _benchmark(args: argparse.Namespace) -> int:
    switch = _switch(args)
    await switch.connect()
    try:
        result = await switch.benchmark(count=args.count, port=args.port)
    finally:
        await switch.close()
    print(f"One command                {result['single_command_ms']} ms")
    print(f"Per command, batched       {result['batched_per_command_ms']} ms")
    print(f"Full frame ({args.ports} ports)       {result['full_frame_ms']} ms")
    print(f"Highest full-frame rate    {result['max_full_frame_fps']} fps")
    return 0


def _effects(args: argparse.Namespace) -> int:
    for effect in list_effects():
        params = ", ".join(p["key"] for p in effect["params"]) or "-"
        print(f"{effect['id']:<14} {effect['name']:<16} {effect['description']}")
        print(f"{'':<14} params: {params}")
    return 0


def _init_config(args: argparse.Namespace) -> int:
    path = Path(args.path)
    if path.exists() and not args.force:
        print(f"{path} already exists (use --force to overwrite)", file=sys.stderr)
        return 1
    path.write_text(EXAMPLE_CONFIG, "utf-8")
    print(f"Wrote {path}. Start with: etherlighting-evolved --config {path}")
    return 0


def _forget_host(args: argparse.Namespace) -> int:
    store = HostKeyStore(data_dir() / "known_switches.json")
    if store.forget(args.host, args.ssh_port):
        print(f"Forgot the host key for {args.host}. The next login pins the new one.")
    else:
        print(f"No pinned host key for {args.host}.")
    return 0


def _ssh_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("host", help="switch IP or hostname")
    parser.add_argument("-u", "--username", default=os.environ.get("EE_USERNAME", ""), help="SSH username")
    parser.add_argument("-p", "--password", default=None, help="SSH password (prompted for if not given)")
    parser.add_argument("-i", "--key-file", default=os.environ.get("EE_KEY_FILE") or None, help="SSH private key")
    parser.add_argument("--ssh-port", type=int, default=22)
    parser.add_argument("--ports", type=int, default=8, help="number of Etherlighting ports (default 8)")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="eectl",
        description=f"{PROJECT_NAME}: {TAGLINE}",
        epilog=DISCLAIMER,
    )
    parser.add_argument("--version", action="version", version=f"{PROJECT_NAME} {__version__}")
    commands = parser.add_subparsers(dest="command")

    serve = commands.add_parser("serve", help="run the server and web UI (the default)")
    serve.add_argument("-c", "--config", help="optional TOML file with advanced settings")
    serve.add_argument("--host", help="address to listen on (default 127.0.0.1)")
    serve.add_argument("--port", type=int, help="port to listen on (default 8787)")
    serve.add_argument("--demo", action="store_true", help="start logged in to the built-in simulator")
    serve.add_argument("--no-autoconnect", action="store_true", help="don't log in to the remembered switch")
    serve.add_argument("--log-level", default="info", choices=("debug", "info", "warning", "error"))
    serve.set_defaults(func=_serve)

    probe = commands.add_parser("probe", help="check that a switch works with EE")
    _ssh_args(probe)
    probe.set_defaults(func=_probe)

    set_ = commands.add_parser("set", help="set one port's color once")
    _ssh_args(set_)
    set_.add_argument("port", type=int)
    set_.add_argument("color", help="hex (ff00ff, #f0f) or a name (white, red, ...)")
    set_.add_argument("--level", type=int, default=100, help="brightness 0-100")
    set_.set_defaults(func=_set)

    bench = commands.add_parser("benchmark", help="measure how fast the switch takes LED commands")
    _ssh_args(bench)
    bench.add_argument("--count", type=int, default=20)
    bench.add_argument("--port", type=int, default=1, help="port that flashes white during the test")
    bench.set_defaults(func=_benchmark)

    effects = commands.add_parser("effects", help="list the effects")
    effects.set_defaults(func=_effects)

    init = commands.add_parser("init-config", help="write an example config file")
    init.add_argument("path", nargs="?", default="ee.toml")
    init.add_argument("--force", action="store_true")
    init.set_defaults(func=_init_config)

    forget = commands.add_parser("forget-host", help="forget a switch's pinned SSH host key")
    forget.add_argument("host")
    forget.add_argument("--ssh-port", type=int, default=22)
    forget.set_defaults(func=_forget_host)
    return parser


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    parser = build_parser()
    if not argv or argv[0].startswith("-") and argv[0] not in ("-h", "--help", "--version"):
        argv = ["serve", *argv]  # `etherlighting-evolved --demo` means `serve --demo`
    args = parser.parse_args(argv)
    if args.command is None:
        parser.print_help()
        return 0
    try:
        result = args.func(args)
        if asyncio.iscoroutine(result):
            result = asyncio.run(result)
    except TransportError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        return 130
    return int(result or 0)


if __name__ == "__main__":
    sys.exit(main())
