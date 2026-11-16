import shlex
import subprocess
from types import SimpleNamespace

import pytest

from etherlighting_evolved.helper_recovery import CONFIRMED, cleanup_command
from etherlighting_evolved.transport import SSHTransport, TransportError

TAG = 'ee_' + 'a' * 32


@pytest.mark.parametrize('condition', ['missing_proc', 'unreadable_cmdline', 'no_owned_helpers'])
def test_process_inventory_must_be_readable_before_confirming_absence(tmp_path, condition, posix_shell):
    proc = tmp_path / 'proc'
    if condition != 'missing_proc':
        for pid in ['self', '42']:
            directory = proc / pid
            directory.mkdir(parents=True)
            (directory / 'cmdline').write_bytes(b'unrelated\x00')
            (directory / 'stat').write_text('fixture')
    command = cleanup_command([TAG]).replace('/proc/', shlex.quote(proc.as_posix()) + '/')
    if condition == 'unreadable_cmdline':
        command = 'tr() { return 1; }\n' + command
    result = subprocess.run([posix_shell.path], input=command, text=True, capture_output=True,
                            timeout=5, env={**posix_shell.env, 'MSYS_NO_PATHCONV': '1'})
    assert (result.returncode == 0) == (condition == 'no_owned_helpers')
    assert (CONFIRMED in result.stdout) == (condition == 'no_owned_helpers')


@pytest.mark.parametrize('tags', [[], [None], ['sh'], [TAG, TAG], ['ee_' + 'a' * 31 + ';'], [TAG] * 17])
def test_cleanup_refuses_unknown_or_invalid_ownership(tags):
    with pytest.raises(ValueError):
        cleanup_command(tags)


def test_command_targets_exact_owned_shell_not_network_services():
    command = cleanup_command([TAG])
    assert f"'sh -s {TAG} '" in command
    assert 'kill -TERM "$ee_pid"' in command
    assert 'killall' not in command and 'sshd' not in command and 'reboot' not in command
    assert 'PATTERNS' not in command


@pytest.mark.anyio
@pytest.mark.parametrize('fault', [None, 'still_open', 'unknown_tag', 'wrong_identity', 'helper_alive', 'probe_close'])
async def test_disconnected_recovery_requires_identity_ownership_and_confirmed_cleanup(monkeypatch, fault):
    old_closed = fault != 'still_open'
    events = []
    async def wait_closed():
        events.append('old_closed')
    old = SimpleNamespace(is_closed=lambda: old_closed, wait_closed=wait_closed)
    process = object()
    transport = SSHTransport('fixture.invalid')
    transport.host_key_fingerprint = 'fixture-key'
    transport.host_keys = SimpleNamespace(get=lambda *args: 'fixture-key')
    transport._cleanup = {'connection': old, 'processes': [process], 'late_connections': []}
    transport._process_tags[id(process)] = None if fault == 'unknown_tag' else TAG

    class Probe:
        def __init__(self, *args, **kwargs):
            self._conn = self
            self.host_key_fingerprint = 'different' if fault == 'wrong_identity' else 'fixture-key'
        async def connect(self):
            events.append('probe_connected')
        async def run(self, command, **kwargs):
            assert TAG in command and kwargs == {'check': True, 'timeout': 10}
            events.append('scoped_cleanup')
            return SimpleNamespace(stdout='not confirmed' if fault == 'helper_alive' else CONFIRMED)
        async def close(self):
            events.append('probe_closed')
            if fault == 'probe_close':
                raise TransportError('cleanup failed')

    monkeypatch.setattr('etherlighting_evolved.transport.SSHTransport', Probe)
    if fault is None:
        await transport.recover_disconnected_helpers()
        assert transport._cleanup is None and not transport._process_tags
        assert events == ['old_closed', 'probe_connected', 'scoped_cleanup', 'probe_closed']
    else:
        with pytest.raises((TransportError, ValueError)):
            await transport.recover_disconnected_helpers()
        assert transport._cleanup is not None
        if fault in {'still_open', 'unknown_tag', 'wrong_identity'}:
            assert 'scoped_cleanup' not in events
