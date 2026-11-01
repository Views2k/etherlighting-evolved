import os
from urllib.parse import parse_qs, urlsplit

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved import desktop
from etherlighting_evolved.api import create_app

LOCAL = "http://127.0.0.1:8787"


def web_fixture(config):
    web = config.base_dir / "web"
    (web / "src").mkdir(parents=True)
    (web / "index.html").write_text('<script type="module" src="/src/app.mjs?v=first"></script>', encoding="utf-8")
    (web / "src/app.mjs").write_text("export const build = 'first';", encoding="utf-8")
    return web


@pytest.mark.parametrize("path", ["/", "/?v=0.2.1", "/index.html", "/src/app.mjs?v=first"])
def test_ui_entry_and_modules_are_not_reused_from_browser_storage(config, path):
    web_fixture(config)
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as client:
        response = client.get(path)
        assert response.status_code == 200
        assert response.headers["cache-control"] == "no-store"
        conditional = client.get(path, headers={"If-None-Match": response.headers["etag"],
                                                "If-Modified-Since": response.headers["last-modified"]})
        assert conditional.status_code == 200
        assert conditional.content == response.content
        assert conditional.headers["cache-control"] == "no-store"


def test_upgrade_serves_new_entry_even_if_packaged_size_and_timestamp_match(config):
    web = web_fixture(config)
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as client:
        prior = client.get("/")
        index = web / "index.html"
        stat = index.stat()
        index.write_text('<script type="module" src="/src/app.mjs?v=later"></script>', encoding="utf-8")
        os.utime(index, ns=(stat.st_atime_ns, stat.st_mtime_ns))
        assert index.stat().st_size == stat.st_size
        upgraded = client.get("/?v=0.2.1", headers={"If-None-Match": prior.headers["etag"]})
        assert upgraded.status_code == 200
        assert 'v=later' in upgraded.text
        assert upgraded.headers["cache-control"] == "no-store"


def test_ui_delivery_keeps_api_auth_and_local_host_checks(config):
    web_fixture(config)
    config.api.token = "fixture-token"
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as client:
        assert client.get("/?v=0.2.1").status_code == 200
        assert client.get("/api/state").status_code == 401
        assert client.get("/api/health", headers={"Authorization": "Bearer fixture-token"}).status_code == 200
        assert client.get("/?v=0.2.1", headers={"Host": "remote.invalid"}).status_code == 403


def test_desktop_navigation_changes_with_the_installed_version_without_resetting_profiles(config, monkeypatch):
    monkeypatch.setattr(desktop, "data_dir", lambda: config.data_dir)
    monkeypatch.setattr(desktop, "load_config", lambda path: config)
    monkeypatch.setattr(desktop, "free_port", lambda preferred: 8787)
    monkeypatch.setattr(desktop, "web_dir", lambda: config.base_dir / "web")
    monkeypatch.setattr(desktop, "Server", lambda app, port: None)
    urls = []
    for version in ("0.2.0", "0.2.1"):
        monkeypatch.setattr(desktop, "__version__", version)
        app = desktop.DesktopApp(background=True)
        url = urlsplit(app.url)
        assert url.scheme == "http" and url.netloc == "127.0.0.1:8787" and url.path == "/"
        assert parse_qs(url.query) == {"v": [version]}
        assert app.config.data_dir == config.data_dir
        urls.append(app.url)
    assert urls[0] != urls[1]
