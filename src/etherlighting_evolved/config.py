"""Optional configuration.

None of this is required. The switch login is entered in the UI; a TOML file only changes advanced settings
such as the frame rate and the address the API listens on.
"""

from __future__ import annotations

import os
import tomllib
from dataclasses import dataclass, field, fields
from pathlib import Path

EXAMPLE_CONFIG = """\
# Etherlighting Evolved (EE) - optional advanced settings. Everything here has a sensible default;
# day-to-day you just log in from the UI with the switch IP, SSH username and password.

[engine]
fps = 10                    # animation frames per second (1-30); measure your switch with `eectl benchmark`
keepalive = 1.5             # seconds; every port is re-sent at least this often (the switch re-applies ~2 s)
link_poll = 1.0             # seconds between port status reads (link, PoE, traffic)
brightness = 100            # 0-100
dimming = "level"           # "level" (hardware dimming, smoother fades) or "rgb"
effect = "static"           # what a fresh install shows under its rules; list effects with `eectl effects`

[engine.effect_params]      # optional starting parameters for that effect
color = "#ffffff"

[switch]
num_ports = 8               # Etherlighting ports (USW-Pro-XG-8-PoE: 8; its SFP+ ports have no LEDs)
# Headless auto-connect without the login screen (optional; prefer env vars EE_HOST / EE_USERNAME / EE_PASSWORD):
# host = "192.168.1.2"
# username = "admin"
# key_file = "~/.ssh/id_ed25519"

[api]
host = "127.0.0.1"          # "0.0.0.0" makes EE reachable from other devices on your network (set a token!)
port = 8787
token = ""                  # optional bearer token required by the API; or set EE_API_TOKEN
cors_origins = ["*"]
static_dir = "web"          # the web UI build (index.html) served at /
"""


def data_dir() -> Path:
    """Where EE keeps the current scene, presets, schedules, the remembered login and pinned host keys."""
    custom = os.environ.get("EE_DATA_DIR")
    return Path(custom).expanduser() if custom else Path.home() / ".etherlighting-evolved"


@dataclass
class SwitchConfig:
    num_ports: int = 8
    port: int = 22
    host: str = ""
    username: str = ""
    password: str = ""
    key_file: str = ""


@dataclass
class EngineConfig:
    fps: float = 10.0
    keepalive: float = 1.5
    link_poll: float = 1.0
    brightness: int = 100
    dimming: str = "level"
    effect: str = "static"
    effect_params: dict = field(default_factory=lambda: {"color": "#ffffff"})


@dataclass
class ApiConfig:
    host: str = "127.0.0.1"
    port: int = 8787
    token: str = ""
    cors_origins: list = field(default_factory=lambda: ["*"])
    static_dir: str = "web"


@dataclass
class Config:
    switch: SwitchConfig = field(default_factory=SwitchConfig)
    engine: EngineConfig = field(default_factory=EngineConfig)
    api: ApiConfig = field(default_factory=ApiConfig)
    base_dir: Path = field(default_factory=Path.cwd)
    data_dir: Path = field(default_factory=data_dir)

    def resolve(self, value: str) -> Path:
        path = Path(value).expanduser()
        return path if path.is_absolute() else self.base_dir / path


def _section(cls, data: dict, name: str):
    allowed = {f.name for f in fields(cls)}
    unknown = set(data) - allowed
    if unknown:
        raise ValueError(f"unknown key(s) in [{name}]: {', '.join(sorted(unknown))}")
    return cls(**data)


def load_config(path: str | Path | None = None) -> Config:
    data: dict = {}
    base = Path.cwd()
    if path:
        p = Path(path).expanduser()
        data = tomllib.loads(p.read_text("utf-8"))
        base = p.resolve().parent
    unknown = set(data) - {"switch", "engine", "api"}
    if unknown:
        raise ValueError(f"unknown section(s): {', '.join(sorted(unknown))}")
    cfg = Config(
        switch=_section(SwitchConfig, data.get("switch", {}), "switch"),
        engine=_section(EngineConfig, data.get("engine", {}), "engine"),
        api=_section(ApiConfig, data.get("api", {}), "api"),
        base_dir=base,
    )
    env = os.environ
    cfg.switch.host = env.get("EE_HOST", cfg.switch.host)
    cfg.switch.username = env.get("EE_USERNAME", cfg.switch.username)
    cfg.switch.password = env.get("EE_PASSWORD", cfg.switch.password)
    cfg.switch.key_file = env.get("EE_KEY_FILE", cfg.switch.key_file)
    cfg.api.token = env.get("EE_API_TOKEN", cfg.api.token)
    return cfg
