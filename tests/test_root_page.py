from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.config import load_config

LOCAL = "http://127.0.0.1:8787"


def test_server_provides_the_api_only_even_with_an_older_interface_folder(config, tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    web = tmp_path / "web"
    web.mkdir()
    (web / "index.html").write_text("<title>Older interface</title>", encoding="utf-8")
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as client:
        root = client.get("/")
        assert root.status_code == 200
        assert "provides the API only" in root.text
        assert "Older interface" not in root.text
        assert client.get("/index.html").status_code == 404
        assert client.get("/docs").status_code == 200


def test_root_page_keeps_api_auth_and_local_host_checks(config):
    config.api.token = "fixture-token"
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as client:
        assert client.get("/").status_code == 200
        assert client.get("/api/state").status_code == 401
        assert client.get("/api/health", headers={"Authorization": "Bearer fixture-token"}).status_code == 200
        assert client.get("/", headers={"Host": "remote.invalid"}).status_code == 403


def test_older_config_files_with_a_static_folder_still_load(tmp_path):
    path = tmp_path / "ee.toml"
    path.write_text('[api]\nport = 8790\nstatic_dir = "web"\n', encoding="utf-8")
    config = load_config(path)
    assert config.api.port == 8790
    assert not hasattr(config.api, "static_dir")
