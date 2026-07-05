import hashlib

import app_settings
import resource_market


class DummyResponse:
    url = "https://api.modrinth.com/v2/version_files"
    status_code = 200
    text = ""

    def __init__(self, payload):
        self._payload = payload

    def json(self):
        return self._payload

    def raise_for_status(self):
        return None


def write_mod(path, content):
    path.write_bytes(content)
    return hashlib.sha1(content).hexdigest()


def test_resolve_modrinth_dependency_plan_finds_missing_required_dependency(tmp_path, monkeypatch):
    mods_dir = tmp_path / "mods"
    mods_dir.mkdir()
    sodium_hash = write_mod(mods_dir / "sodium.jar", b"sodium")

    def fake_post(*_args, **_kwargs):
        return DummyResponse({
            sodium_hash: {
                "project_id": "sodium",
                "name": "Sodium",
                "dependencies": [
                    {"dependency_type": "required", "project_id": "fabric-api"},
                    {"dependency_type": "optional", "project_id": "modmenu"},
                ],
            }
        })

    monkeypatch.setattr(resource_market.http_client, "post", fake_post)

    plan = resource_market.resolve_modrinth_dependency_plan(str(mods_dir))

    assert plan["installed_project_ids"] == ["sodium"]
    assert plan["missing_dependencies"] == [{
        "project_id": "fabric-api",
        "version_id": "",
        "required_by": ["Sodium"],
    }]


def test_resolve_modrinth_dependency_plan_skips_installed_dependency(tmp_path, monkeypatch):
    mods_dir = tmp_path / "mods"
    mods_dir.mkdir()
    sodium_hash = write_mod(mods_dir / "sodium.jar", b"sodium")
    fabric_api_hash = write_mod(mods_dir / "fabric-api.jar", b"fabric-api")

    def fake_post(*_args, **_kwargs):
        return DummyResponse({
            sodium_hash: {
                "project_id": "sodium",
                "name": "Sodium",
                "dependencies": [{"dependency_type": "required", "project_id": "fabric-api"}],
            },
            fabric_api_hash: {
                "project_id": "fabric-api",
                "name": "Fabric API",
                "dependencies": [],
            },
        })

    monkeypatch.setattr(resource_market.http_client, "post", fake_post)

    plan = resource_market.resolve_modrinth_dependency_plan(str(mods_dir))

    assert plan["installed_project_ids"] == ["fabric-api", "sodium"]
    assert plan["missing_dependencies"] == []


def test_modrinth_api_url_uses_configured_base():
    previous = {section: dict(app_settings.config.items(section)) for section in app_settings.config.sections()}
    try:
        app_settings.config.clear()
        app_settings.config.add_section("DOWNLOAD")
        app_settings.config["DOWNLOAD"]["modrinth_api_base"] = "https://proxy.example/modrinth/v2/"

        assert resource_market.modrinth_api_url("search") == "https://proxy.example/modrinth/v2/search"
    finally:
        app_settings.config.clear()
        for section, values in previous.items():
            app_settings.config.add_section(section)
            for key, value in values.items():
                app_settings.config[section][key] = value


def test_rewrite_modrinth_download_url_uses_configured_cdn_base():
    previous = {section: dict(app_settings.config.items(section)) for section in app_settings.config.sections()}
    try:
        app_settings.config.clear()
        app_settings.config.add_section("DOWNLOAD")
        app_settings.config["DOWNLOAD"]["modrinth_cdn_base"] = "https://cdn.example/modrinth"

        rewritten = resource_market.rewrite_modrinth_download_url(
            "https://cdn.modrinth.com/data/demo/versions/1/demo.jar?download=1"
        )

        assert rewritten == "https://cdn.example/modrinth/data/demo/versions/1/demo.jar?download=1"
    finally:
        app_settings.config.clear()
        for section, values in previous.items():
            app_settings.config.add_section(section)
            for key, value in values.items():
                app_settings.config[section][key] = value
