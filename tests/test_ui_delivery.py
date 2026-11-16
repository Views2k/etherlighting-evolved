import os

import pytest
from fastapi.testclient import TestClient

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
