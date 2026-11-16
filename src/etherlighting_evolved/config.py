"""Optional configuration.

None of this is required. The switch login is entered in the UI; a TOML file only changes advanced settings
such as the frame rate and the address the API listens on.
"""

from __future__ import annotations

import os
import tomllib
from dataclasses import dataclass, field, fields
from pathlib import Path

from .guard import LOOPBACK_BINDS

EXAMPLE_CONFIG = """\
# Etherlighting Evolved (EE) - optional advanced settings. Everything here has a sensible default;
# day-to-day you just log in from the UI with the switch IP, SSH username and password.

[engine]
fps = 25                    # highest animation frame rate (1-30); max_commands usually limits it first
max_commands = 200          # requested direct-output ceiling; starts at 30 and adapts to fresh device feedback
transition = 0.6            # seconds a port takes to fade to a new effect or rule color (0 = switch at once)
gamma = 2.2                 # brightness curve of the LEDs (1 = off); higher makes dim colors darker
keepalive = 1.5             # seconds; every port is re-sent at least this often (the switch re-applies ~2 s)
link_poll = 1.0             # seconds between port status reads (link, PoE, traffic, switch CPU)
brightness = 100            # 0-100
dimming = "rgb"             # RGB carries effect fades and all brightness controls
effect = "static"           # what a fresh install shows under its rules; list effects with `eectl effects`

[engine.effect_params]      # optional starting parameters for that effect
color = "#ffffff"

[switch]
num_ports = 8               # Simulator/fallback count; known real switches use their detected layout
# Headless auto-connect without the login screen (optional; prefer env vars EE_HOST / EE_USERNAME / EE_PASSWORD):
# host = "192.168.1.2"
# username = "admin"
# key_file = "~/.ssh/id_ed25519"

[api]
host = "127.0.0.1"          # network access requires an API token of at least 32 characters
port = 8787
token = ""                  # bearer token; required for network access; or set EE_API_TOKEN
cors_origins = []           # other sites allowed to use the API, e.g. a UI dev server: ["http://localhost:5173"]
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
    fps: float = 25.0
    max_commands: float = 200.0
    transition: float = 0.6
    gamma: float = 2.2
    keepalive: float = 1.5
    link_poll: float = 1.0
    brightness: int = 100
    dimming: str = "rgb"
    effect: str = "static"
    effect_params: dict = field(default_factory=lambda: {"color": "#ffffff"})

    def __post_init__(self) -> None:
        if self.dimming not in ("level", "rgb"):
            raise ValueError("dimming must be 'level' or 'rgb'")
        # Older configurations chose "level"; output always carries brightness in RGB now.
        if self.dimming == "level":
            self.dimming = "rgb"


@dataclass
class ApiConfig:
    host: str = "127.0.0.1"
    port: int = 8787
    token: str = ""
    cors_origins: list = field(default_factory=list)

    def validate(self) -> None:
        if not isinstance(self.host, str) or not self.host or self.host != self.host.strip():
            raise ValueError("API host must be a nonempty address without surrounding whitespace")
        if not isinstance(self.token, str) or any(not 33 <= ord(c) <= 126 for c in self.token):
            raise ValueError("API token must contain only printable ASCII characters without spaces")
        if self.host not in LOOPBACK_BINDS and len(self.token) < 32:
            raise ValueError("Network access requires an API token of at least 32 characters; "
                             "set EE_API_TOKEN or api.token")
        if "*" in self.cors_origins and len(self.token) < 32:
            raise ValueError('api.cors_origins = ["*"] lets every website use the API, so it requires an API token '
                             "of at least 32 characters; set EE_API_TOKEN or api.token, or list the allowed "
                             "origins instead")


@dataclass
class Config:
    switch: SwitchConfig = field(default_factory=SwitchConfig)
    engine: EngineConfig = field(default_factory=EngineConfig)
    api: ApiConfig = field(default_factory=ApiConfig)
    data_dir: Path = field(default_factory=data_dir)


def _section(cls, data: dict, name: str):
    allowed = {f.name for f in fields(cls)}
    unknown = set(data) - allowed
    if unknown:
        raise ValueError(f"unknown key(s) in [{name}]: {', '.join(sorted(unknown))}")
    return cls(**data)


def load_config(path: str | Path | None = None) -> Config:
    data: dict = {}
    if path:
        data = tomllib.loads(Path(path).expanduser().read_text("utf-8"))
    unknown = set(data) - {"switch", "engine", "api"}
    if unknown:
        raise ValueError(f"unknown section(s): {', '.join(sorted(unknown))}")
    # Earlier example configs set api.static_dir for a web interface the server no longer serves.
    api = {key: value for key, value in data.get("api", {}).items() if key != "static_dir"}
    cfg = Config(
        switch=_section(SwitchConfig, data.get("switch", {}), "switch"),
        engine=_section(EngineConfig, data.get("engine", {}), "engine"),
        api=_section(ApiConfig, api, "api"),
    )
    env = os.environ
    cfg.switch.host = env.get("EE_HOST", cfg.switch.host)
    cfg.switch.username = env.get("EE_USERNAME", cfg.switch.username)
    cfg.switch.password = env.get("EE_PASSWORD", cfg.switch.password)
    cfg.switch.key_file = env.get("EE_KEY_FILE", cfg.switch.key_file)
    cfg.api.token = env.get("EE_API_TOKEN", cfg.api.token)
    return cfg
