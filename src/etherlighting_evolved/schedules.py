"""Schedules: run an action at a set time, e.g. load the "Evening" preset at 19:00 on weekdays.

Time-based colors are handled by rules. Schedules change the whole setup, or hand the LEDs back to the switch.

    {"name": "Evening", "at": "19:00", "days": ["mon", "tue", "wed", "thu", "fri"],
     "action": {"type": "preset", "preset": "Evening"}}

Action types: preset (load a saved preset), brightness (set 0-100), release (hand the LEDs back to the switch's
own Etherlighting), resume (take control again).
"""

from __future__ import annotations

import asyncio
import json
import logging
import re
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from pathlib import Path

from .engine import NotFound
from .presets import PresetStore, ProfileStoreError, write_json
from .rules import WEEKDAYS, finite_number, parse_clock
from .transport import TransportError

log = logging.getLogger(__name__)

ACTIONS = ("preset", "brightness", "release", "resume")


@dataclass
class Schedule:
    name: str
    at: int  # minutes after midnight
    action: dict
    days: tuple[str, ...] = WEEKDAYS
    enabled: bool = True
    id: str = field(default_factory=lambda: uuid.uuid4().hex[:8])
    device_binding: str | None = None

    @classmethod
    def parse(cls, data: dict) -> Schedule:
        if not isinstance(data, dict):
            raise ValueError("a schedule must be an object")
        action = data.get("action")
        if not isinstance(action, dict) or action.get("type") not in ACTIONS:
            raise ValueError(f"action.type must be one of {', '.join(ACTIONS)}")
        kind = action["type"]
        if kind == "preset":
            if not str(action.get("preset") or "").strip():
                raise ValueError("a preset action needs the preset's name")
            action = {"type": kind, "preset": str(action["preset"]).strip()}
        elif kind == "brightness":
            value = finite_number(action.get("value", -1), "brightness")
            if not 0 <= value <= 100:
                raise ValueError("brightness must be 0-100")
            action = {"type": kind, "value": round(value)}
        else:
            action = {"type": kind}

        days = tuple(str(d).lower()[:3] for d in (data.get("days") or WEEKDAYS))
        if any(d not in WEEKDAYS for d in days):
            raise ValueError(f"days are {', '.join(WEEKDAYS)}")
        schedule = cls(
            name=str(data.get("name") or "Schedule").strip()[:60],
            at=parse_clock(data.get("at", "")),
            action=action,
            days=tuple(d for d in WEEKDAYS if d in days),
            enabled=bool(data.get("enabled", True)),
        )
        if data.get("id"):
            schedule.id = str(data["id"])[:32]
        binding = data.get("device_binding")
        if binding == "demo" or isinstance(binding, str) and re.fullmatch(r"[0-9a-f]{64}", binding):
            schedule.device_binding = binding
        return schedule

    def due(self, now: datetime) -> bool:
        return self.enabled and WEEKDAYS[now.weekday()] in self.days and now.hour * 60 + now.minute == self.at

    def next_run(self, now: datetime) -> datetime | None:
        if not self.enabled:
            return None
        start = now.replace(hour=self.at // 60, minute=self.at % 60, second=0, microsecond=0)
        for offset in range(8):
            candidate = start + timedelta(days=offset)
            if candidate > now and WEEKDAYS[candidate.weekday()] in self.days:
                return candidate
        return None

    def to_dict(self, now: datetime | None = None) -> dict:
        upcoming = self.next_run(now or datetime.now())
        return {
            "id": self.id,
            "name": self.name,
            "enabled": self.enabled,
            "at": f"{self.at // 60:02d}:{self.at % 60:02d}",
            "days": list(self.days),
            "action": dict(self.action),
            "next_run": upcoming.isoformat(timespec="minutes") if upcoming else None,
            "device_binding": self.device_binding,
        }


class ScheduleStoreError(OSError):
    code = "schedule_storage_unavailable"


class ScheduleStore:
    def __init__(self, path: Path) -> None:
        self.path = Path(path)
        self.items: list[Schedule] = []
        self._load_error: ScheduleStoreError | None = None
        self._load()

    def _load(self) -> None:
        try:
            try:
                entries = json.loads(self.path.read_text("utf-8"))
            except FileNotFoundError:
                entries = []
            if not isinstance(entries, list):
                raise ValueError("saved schedules must be a list")
            items = [Schedule.parse(entry) for entry in entries]
            if len({item.id for item in items}) != len(items):
                raise ValueError("saved schedule IDs must be unique")
        except (OSError, ValueError, TypeError):
            self._load_error = ScheduleStoreError(
                "Saved schedules could not be read. Restore the schedule file and try again.")
            return
        self.items = items
        self._load_error = None

    def check_available(self) -> None:
        if self._load_error is not None:
            self._load()
            if self._load_error is not None:
                raise self._load_error

    def _save(self) -> None:
        try:
            write_json(self.path, [{k: v for k, v in s.to_dict().items() if k != "next_run"} for s in self.items])
        except OSError as exc:
            raise ScheduleStoreError("Schedules could not be saved. Check storage access and try again.") from exc

    def list(self) -> list[dict]:
        self.check_available()
        now = datetime.now()
        return [s.to_dict(now) for s in self.items]

    def get(self, schedule_id: str) -> Schedule:
        self.check_available()
        for schedule in self.items:
            if schedule.id == schedule_id:
                return schedule
        raise NotFound(f"no schedule {schedule_id!r}")

    def add(self, data: dict) -> Schedule:
        self.check_available()
        schedule = Schedule.parse(data)
        if schedule.id in {s.id for s in self.items}:
            raise ValueError(f"a schedule with id {schedule.id!r} already exists")
        self.items.append(schedule)
        try:
            self._save()
        except OSError:
            self.items.remove(schedule)
            raise
        return schedule

    def update(self, schedule_id: str, changes: dict) -> Schedule:
        current = self.get(schedule_id)
        merged = {**current.to_dict(), **changes, "id": schedule_id}
        replacement = Schedule.parse(merged)
        index = self.items.index(current)
        self.items[index] = replacement
        try:
            self._save()
        except OSError:
            self.items[index] = current
            raise
        return replacement

    def delete(self, schedule_id: str) -> None:
        current = self.get(schedule_id)
        index = self.items.index(current)
        self.items.pop(index)
        try:
            self._save()
        except OSError:
            self.items.insert(index, current)
            raise


class Scheduler:
    """Checks the clock a few times a minute and runs each due schedule once."""

    def __init__(self, store: ScheduleStore, presets: PresetStore, sessions) -> None:
        self.store = store
        self.presets = presets
        self.sessions = sessions
        self.history: list[dict] = []
        self._fired: dict[str, str] = {}

    def tick(self, now: datetime | None = None) -> list[str]:
        now = now or datetime.now()
        minute = now.strftime("%Y-%m-%d %H:%M")
        ran = []
        for schedule in self.store.items:
            if self.is_bound(schedule) and schedule.due(now) and self._fired.get(schedule.id) != minute:
                self._fired[schedule.id] = minute
                self.run(schedule, now)
                ran.append(schedule.id)
        return ran

    def is_bound(self, schedule: Schedule) -> bool:
        identity = self.sessions.schedule_identity()
        return identity is not None and schedule.device_binding == identity

    def describe(self, schedule: Schedule) -> dict:
        result = schedule.to_dict()
        result["needs_device_confirmation"] = not self.is_bound(schedule)
        if result["needs_device_confirmation"]:
            result["next_run"] = None
        return result

    def list(self) -> list[dict]:
        self.store.check_available()
        return [self.describe(schedule) for schedule in self.store.items]

    def run(self, schedule: Schedule, now: datetime | None = None) -> None:
        engine = self.sessions.engine
        action = schedule.action
        result = "ok"
        try:
            if engine is None:
                result = "skipped: not logged in"
            elif not self.is_bound(schedule):
                result = "skipped: review and apply this schedule for the connected switch"
            elif action["type"] == "preset":
                engine.apply_snapshot(self.presets.get(action["preset"]))
            elif action["type"] == "brightness":
                engine.set_brightness(action["value"])
            elif action["type"] == "release":
                engine.release()
            elif action["type"] == "resume":
                engine.resume()
        except KeyError:
            result = f"failed: there is no preset named {action.get('preset')!r} any more"
        except (ValueError, ProfileStoreError, TransportError) as exc:
            result = f"failed: {exc}"
        log.info("schedule %r: %s", schedule.name, result)
        entry = {"id": schedule.id, "name": schedule.name, "at": (now or datetime.now()).isoformat(timespec="seconds"),
                 "result": result}
        self.history = [entry, *self.history][:20]

    async def run_forever(self, interval: float = 15.0) -> None:
        while True:
            try:
                self.tick()
            except Exception:
                log.exception("scheduler tick failed")
            await asyncio.sleep(interval)
