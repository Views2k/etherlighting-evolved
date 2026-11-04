"""Schedules: run an action at a set time, e.g. load the "Evening" preset at 19:00 on weekdays.

Time-based colors are handled by rules. Schedules change the whole setup, or hand the LEDs back to the switch.

    {"name": "Evening", "at": "19:00", "days": ["mon", "tue", "wed", "thu", "fri"],
     "action": {"type": "preset", "preset": "Evening"}}

Action types: preset (load a saved preset), brightness (set 0-100), release (hand the LEDs back to the switch's
own Etherlighting), resume (take control again).
"""

from __future__ import annotations

import asyncio
import contextlib
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
    revision: int = 1

    @classmethod
    def parse(cls, data: dict) -> Schedule:
        if not isinstance(data, dict):
            raise ValueError("a schedule must be an object")
        action = data.get("action")
        if not isinstance(action, dict) or action.get("type") not in ACTIONS:
            raise ValueError(f"action.type must be one of {', '.join(ACTIONS)}")
        kind = action["type"]
        if kind == "preset":
            profile_id = action.get("profile_id")
            if profile_id is not None and (not isinstance(profile_id, str)
                    or not re.fullmatch(r"[0-9a-f]{32}", profile_id)):
                raise ValueError("Choose a saved profile.")
            if not profile_id and not str(action.get("preset") or "").strip():
                raise ValueError("a preset action needs a saved profile")
            action = {"type": kind, "preset": str(action.get("preset") or "").strip(),
                      **({"profile_id": profile_id} if profile_id else {})}
        elif kind == "brightness":
            value = finite_number(action.get("value", -1), "brightness")
            if not 0 <= value <= 100:
                raise ValueError("brightness must be 0-100")
            action = {"type": kind, "value": round(value)}
        else:
            action = {"type": kind}

        raw_days = data.get("days", WEEKDAYS)
        if not isinstance(raw_days, (list, tuple)) or not raw_days:
            raise ValueError("Choose at least one day.")
        days = tuple(str(d).lower()[:3] for d in raw_days)
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
        revision = data.get("revision", 1)
        if not isinstance(revision, int) or isinstance(revision, bool) or revision < 1:
            raise ValueError("The schedule revision is invalid.")
        schedule.revision = revision
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
            "revision": self.revision,
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
        if changes.get("expected_revision", current.revision) != current.revision:
            raise ValueError("This schedule changed. Refresh schedules before saving.")
        merged = {**current.to_dict(), **changes, "id": schedule_id, "revision": current.revision + 1}
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
    """Claim each local-time occurrence durably before changing output."""

    def __init__(self, store: ScheduleStore, presets: PresetStore, sessions) -> None:
        self.store = store
        self.presets = presets
        self.sessions = sessions
        self.history: list[dict] = []
        self._fired: dict[str, str] = {}
        self.journal_path = store.path.with_name(store.path.stem + "-runs.json")
        self._journal_error = False
        self._run_lock = asyncio.Lock()
        try:
            journal = json.loads(self.journal_path.read_text("utf-8"))
            if (not isinstance(journal, dict) or journal.get("version") != 1
                    or not isinstance(journal.get("fired"), dict)
                    or not all(isinstance(k, str) and isinstance(v, str) for k, v in journal["fired"].items())
                    or not isinstance(journal.get("history"), list)
                    or not all(isinstance(row, dict) for row in journal["history"])):
                raise ValueError("Invalid occurrence journal")
            self._fired = journal["fired"]
            self.history = journal["history"][:100]
            for row in self.history:
                if row.get("result") in {"pending", "pending output confirmation"}:
                    row["result"] = "unconfirmed: service stopped before the result was recorded"
        except FileNotFoundError:
            pass
        except (OSError, ValueError, TypeError):
            self._journal_error = True
        with contextlib.suppress(ProfileStoreError, ScheduleStoreError):
            self.migrate_profiles()

    def _save_journal(self) -> None:
        if self._journal_error:
            raise ScheduleStoreError("Schedule history could not be verified. "
                                     "Review the saved history before running schedules.")
        try:
            write_json(self.journal_path, {"version": 1, "fired": self._fired, "history": self.history})
        except OSError as exc:
            raise ScheduleStoreError("Schedule history could not be saved. "
                                     "Review the last run before trying again.") from exc

    def bind_action(self, data: dict) -> dict:
        result = dict(data)
        action = result.get("action")
        if isinstance(action, dict) and action.get("type") == "preset":
            try:
                row = (self.presets.record(profile_id=action["profile_id"]) if action.get("profile_id")
                       else self.presets.record(name=action.get("preset")))
            except KeyError:
                raise ValueError("Choose an existing saved profile.") from None
            result["action"] = {"type": "preset", "profile_id": row["id"], "preset": row["name"]}
        return result

    def migrate_profiles(self) -> None:
        self.store.check_available()
        for schedule in list(self.store.items):
            if schedule.action["type"] != "preset" or schedule.action.get("profile_id"):
                continue
            try:
                changes = self.bind_action({"action": schedule.action})
            except ValueError:
                # A missing legacy name must never attach to a later profile with that name.
                changes = {"action": {**schedule.action, "profile_id": uuid.uuid4().hex}, "enabled": False}
            self.store.update(schedule.id, changes)

    def tick(self, now: datetime | None = None) -> list[str]:
        self.store.check_available()
        self.migrate_profiles()
        if self._journal_error:
            raise ScheduleStoreError("Schedule history could not be verified. "
                                     "Review the saved history before running schedules.")
        now = now or datetime.now()
        # Both folds of a repeated DST minute identify one occurrence. A skipped
        # local minute is not replayed later or on startup.
        minute = now.strftime("%Y-%m-%d %H:%M")
        ran = []
        for schedule in list(self.store.items):
            key = f"{schedule.id}:{schedule.device_binding}"
            if self.is_bound(schedule) and schedule.due(now) and self._fired.get(key) != minute:
                self.run(schedule, now, occurrence=minute)
                ran.append(schedule.id)
        return ran

    def is_bound(self, schedule: Schedule) -> bool:
        identity = self.sessions.schedule_identity()
        return identity is not None and schedule.device_binding == identity

    def describe(self, schedule: Schedule) -> dict:
        result = schedule.to_dict()
        result["needs_device_confirmation"] = not self.is_bound(schedule)
        result["profile_missing"] = False
        if schedule.action["type"] == "preset":
            try:
                row = self.presets.record(profile_id=schedule.action.get("profile_id"))
                result["action"] = {**result["action"], "preset": row["name"]}
            except KeyError:
                result["profile_missing"] = True
        if result["needs_device_confirmation"] or result["profile_missing"] or self._journal_error:
            result["next_run"] = None
        result["last_run"] = next((row for row in self.history if row.get("id") == schedule.id
                                   and row.get("device_binding") == schedule.device_binding), None)
        result["conflicts"] = [{"id": other.id, "name": other.name} for other in self.store.items
                               if other.id != schedule.id and other.enabled and schedule.enabled
                               and other.at == schedule.at and set(other.days).intersection(schedule.days)
                               and other.device_binding == schedule.device_binding]
        return result

    def list(self) -> list[dict]:
        self.migrate_profiles()
        return [self.describe(schedule) for schedule in self.store.items]

    def run(self, schedule: Schedule, now: datetime | None = None, *, occurrence: str | None = None,
            defer_confirmation: bool = False) -> dict | None:
        engine = self.sessions.engine
        action = schedule.action
        key = f"{schedule.id}:{schedule.device_binding}"
        if occurrence and self._fired.get(key) == occurrence:
            return
        before_fired, before_history = dict(self._fired), list(self.history)
        entry = {"run_id": uuid.uuid4().hex, "id": schedule.id, "name": schedule.name,
                 "device_binding": schedule.device_binding,
                 "at": (now or datetime.now()).isoformat(timespec="seconds"),
                 "trigger": "scheduled" if occurrence else "manual", "result": "pending"}
        if occurrence:
            self._fired[key] = occurrence
        self.history = [entry, *self.history][:100]
        try:
            self._save_journal()
        except OSError:
            self._fired, self.history = before_fired, before_history
            raise
        result = "ok"
        try:
            if engine is None:
                result = "skipped: not logged in"
            elif not self.is_bound(schedule):
                result = "skipped: review and apply this schedule for the connected switch"
            elif action["type"] == "preset":
                if not action.get("profile_id"):
                    result = "failed: review this schedule and choose its saved profile"
                else:
                    engine.apply_snapshot(self.presets.get_id(action["profile_id"]))
            elif action["type"] == "brightness":
                engine.set_brightness(action["value"])
            elif action["type"] == "release":
                engine.release()
            elif action["type"] == "resume":
                engine.resume()
        except KeyError:
            result = "failed: the scheduled profile was removed; choose another saved profile"
        except (ValueError, ProfileStoreError, TransportError) as exc:
            result = f"failed: {exc}"
        entry["result"] = "pending output confirmation" if result == "ok" and defer_confirmation else result
        try:
            self._save_journal()
        except OSError:
            entry["result"] = "unconfirmed: the final schedule result could not be saved"
            raise
        log.info("schedule completed: %s", entry["result"])
        return entry

    async def run_confirmed(self, schedule: Schedule, now: datetime | None = None, *, occurrence: str | None = None):
        async with self._run_lock:
            current = next((item for item in self.store.items if item.id == schedule.id), None)
            if current is None or current.revision != schedule.revision:
                return {"result": "skipped: the schedule changed before it could run"}
            engine = self.sessions.engine
            before = dict(getattr(engine, "_sfp_revisions", {}))
            entry = self.run(schedule, now, occurrence=occurrence, defer_confirmation=True)
            if entry is None or entry["result"] != "pending output confirmation":
                return entry
            expected = dict(getattr(engine, "_sfp_revisions", {}))
            try:
                if schedule.action["type"] == "release":
                    await engine.wait_for_output_idle()
                elif hasattr(engine, "wait_for_sfp"):
                    for port, revision in expected.items():
                        if before.get(port) == revision and schedule.action["type"] != "resume":
                            continue
                        if engine._sfp_revisions.get(port) != revision:
                            raise ValueError("Lighting changed during schedule confirmation.")
                        await engine.wait_for_sfp(port)
                        if engine._sfp_revisions.get(port) != revision:
                            raise ValueError("Lighting changed during schedule confirmation.")
                        if engine.mode == "running" and engine._sfp_acknowledged.get(port) != revision:
                            raise ValueError("SFP output is still pending. Check the connected switch.")
                if self.sessions.engine is not engine:
                    raise ValueError("The connected switch changed during schedule confirmation.")
                entry["result"] = "ok"
            except (ValueError, TransportError) as exc:
                entry["result"] = f"unconfirmed: {exc}"
            try:
                self._save_journal()
            except OSError:
                entry["result"] = "unconfirmed: the final schedule result could not be saved"
                raise
            return entry

    async def tick_confirmed(self, now: datetime | None = None) -> list[str]:
        self.migrate_profiles()
        if self._journal_error:
            raise ScheduleStoreError("Schedule history could not be verified. "
                                     "Review the saved history before running schedules.")
        now = now or datetime.now()
        minute = now.strftime("%Y-%m-%d %H:%M")
        ran = []
        for schedule in list(self.store.items):
            key = f"{schedule.id}:{schedule.device_binding}"
            if (self.is_bound(schedule) and schedule.due(now) and self._fired.get(key) != minute
                    and await self.run_confirmed(schedule, now, occurrence=minute) is not None):
                ran.append(schedule.id)
        return ran

    async def run_forever(self, interval: float = 15.0) -> None:
        while True:
            try:
                await self.tick_confirmed()
            except Exception:
                log.exception("scheduler tick failed")
            await asyncio.sleep(interval)
