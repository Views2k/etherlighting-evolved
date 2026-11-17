import asyncio
import importlib.util
import io
import json
from pathlib import Path
from unittest.mock import Mock

import pytest
import uvicorn
from uvicorn.server import ServerState
from websockets.protocol import CLOSED, CLOSING, OPEN

from etherlighting_evolved.websocket_protocol import websocket_protocol


async def application(scope, receive, send):
    pass


def protocol():
    selected = websocket_protocol()
    config = uvicorn.Config(application, ws=selected, log_config=None)
    result = selected(config, ServerState(), {})
    result.transport = Mock()
    result.handshake_complete = True
    result.conn.state = OPEN
    return result


@pytest.mark.anyio
@pytest.mark.parametrize("state", [CLOSING, CLOSED])
async def test_shutdown_finishes_a_websocket_that_is_already_closing(state):
    connection = protocol()
    connection.conn.send_close(1000)
    assert connection.close_sent is False
    connection.conn.state = state
    timer = asyncio.get_running_loop().call_later(60, lambda: None)
    connection.close_timer = timer
    connection.shutdown()
    assert timer.cancelled()
    assert connection.close_timer is None
    connection.transport.close.assert_called_once()
    connection.transport.write.assert_not_called()


@pytest.mark.anyio
async def test_shutdown_still_sends_restart_close_for_open_websocket():
    connection = protocol()
    connection.shutdown()
    assert connection.conn.state == CLOSING
    assert connection.queue.get_nowait() == {"type": "websocket.disconnect", "code": 1012}
    connection.transport.write.assert_called_once()
    connection.transport.close.assert_called_once()


@pytest.mark.anyio
async def test_shutdown_does_not_hide_unrelated_protocol_failure():
    connection = protocol()
    connection.conn.send_close = Mock(side_effect=RuntimeError("fixture failure"))
    with pytest.raises(RuntimeError, match="fixture failure"):
        connection.shutdown()
    connection.transport.close.assert_not_called()


def load_service(name):
    source = Path(__file__).resolve().parents[1] / "packaging" / "native_service.py"
    spec = importlib.util.spec_from_file_location(name, source)
    service = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(service)
    return service


def test_native_failure_diagnostic_excludes_exception_values_and_paths(monkeypatch, capsys):
    service = load_service("diagnostic_service_fixture")
    secret = "private-token-that-must-not-be-logged"
    monkeypatch.setattr(service.sys, "stdin", io.StringIO(secret))
    monkeypatch.setattr(service, "configure_logging", lambda: None)
    assert service.main() == 1
    captured = capsys.readouterr()
    diagnostic = json.loads(captured.err)
    assert diagnostic["event"] == "engine_failure"
    assert diagnostic["error_type"] == "JSONDecodeError"
    assert 0 < len(diagnostic["frames"]) <= 8
    assert all("/" not in frame["module"] and "\\" not in frame["module"] for frame in diagnostic["frames"])
    assert secret not in captured.err + captured.out
    assert len(captured.err) < 4096


def test_native_log_handler_preserves_locations_without_formatted_messages(capsys):
    service = load_service("log_handler_fixture")
    source = Path(service.__file__)
    secret = "private-token-that-must-not-be-logged"
    record = service.logging.LogRecord("uvicorn.error", service.logging.ERROR,
                                      str(source), 10, "Error: %s", (secret,), None, "serve")
    service.DiagnosticLogHandler().emit(record)
    output = capsys.readouterr().err
    diagnostic = json.loads(output)
    assert diagnostic["event"] == "engine_log"
    assert diagnostic["logger"] == "uvicorn.error"
    assert diagnostic["frames"] == [{"module": "native_service.py", "function": "serve", "line": 10}]
    assert secret not in output
    assert str(source.parent) not in output


@pytest.mark.parametrize(("logger", "template", "expected"), [
    ("etherlighting_evolved.session", "automatic login to %s failed: %s", "automatic login to %s failed: %s"),
    ("etherlighting_evolved.engine", "x" * 200, "x" * 120),
    ("uvicorn.error", "Error: %s", None),
])
def test_native_log_handler_records_ee_templates_without_arguments(capsys, logger, template, expected):
    service = load_service("log_template_fixture")
    secret = "private-token-that-must-not-be-logged"
    record = service.logging.LogRecord(logger, service.logging.WARNING, service.__file__, 10, template, (secret,),
                                       None, "serve")
    service.DiagnosticLogHandler().emit(record)
    output = capsys.readouterr().err
    assert json.loads(output).get("template") == expected
    assert secret not in output


@pytest.mark.parametrize(("code", "kept"), [("output_recovery_required", True), ("Bad-Code", False), ("x" * 65, False)])
def test_native_failure_diagnostic_keeps_only_identifier_codes(capsys, code, kept):
    service = load_service("failure_code_fixture")
    secret = "private-token-that-must-not-be-logged"
    error = RuntimeError(secret)
    error.code = code
    service.report_failure(error)
    output = capsys.readouterr().err
    assert json.loads(output).get("code") == (code if kept else None)
    assert secret not in output
