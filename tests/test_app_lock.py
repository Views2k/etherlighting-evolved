import sys
import uuid

import pytest

from etherlighting_evolved.app_lock import AlreadyRunningError, AppInstanceLock, AppLockError


@pytest.mark.skipif(sys.platform != "win32", reason="Windows named mutex")
def test_instance_mutex_rejects_a_second_owner_and_releases_on_exit():
    name = "EE-fixture-" + uuid.uuid4().hex
    first = AppInstanceLock(name)
    with first:
        with pytest.raises(AlreadyRunningError) as error, AppInstanceLock(name):
            pytest.fail("second owner entered")
        assert error.value.code == "instance_running"
        with pytest.raises(AppLockError):
            first.__enter__()
    with AppInstanceLock(name):
        pass


@pytest.mark.skipif(sys.platform != "win32", reason="Windows named mutex")
def test_mutex_is_released_when_service_fails():
    name = "EE-fixture-" + uuid.uuid4().hex
    with pytest.raises(ValueError), AppInstanceLock(name):
        raise ValueError("fixture")
    with AppInstanceLock(name):
        pass


def test_non_windows_browser_lock_fails_closed(monkeypatch):
    monkeypatch.setattr(sys, "platform", "fixture")
    with pytest.raises(AppLockError) as error, AppInstanceLock("unused-fixture"):
        pytest.fail("unprotected service started")
    assert error.value.code == "instance_unavailable"
