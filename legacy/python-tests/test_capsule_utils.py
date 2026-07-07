import json
import zipfile

from capsule_utils import (
    CAPSULE_MANIFEST,
    export_capsule,
    import_capsule,
    read_capsule_manifest,
    should_include_capsule_file,
)


def write_version(game_dir, version_id):
    version_dir = game_dir / "versions" / version_id
    version_dir.mkdir(parents=True)
    (version_dir / f"{version_id}.json").write_text(
        json.dumps({"id": version_id, "mainClass": "net.minecraft.client.main.Main"}),
        encoding="utf-8",
    )
    return version_dir


def test_capsule_file_filter_excludes_sensitive_and_runtime_noise():
    assert should_include_capsule_file("mods/private.jar")
    assert should_include_capsule_file("config/example.json")
    assert should_include_capsule_file("resourcepacks/pack.zip")
    assert not should_include_capsule_file("saves/world/level.dat")
    assert not should_include_capsule_file("logs/latest.log")
    assert not should_include_capsule_file("mods/debug.log")
    assert not should_include_capsule_file("launcher_profiles.json")
    assert not should_include_capsule_file("../outside.txt")


def test_export_and_import_capsule_roundtrip(tmp_path):
    game_dir = tmp_path / "game"
    version_id = "fabric-loader-0.16.0-1.20.1"
    runtime_dir = write_version(game_dir, version_id)
    (runtime_dir / "mods").mkdir()
    (runtime_dir / "config").mkdir()
    (runtime_dir / "logs").mkdir()
    (runtime_dir / "mods" / "private.jar").write_bytes(b"private mod")
    (runtime_dir / "config" / "private.json").write_text('{"ok":true}', encoding="utf-8")
    (runtime_dir / "logs" / "latest.log").write_text("secret log", encoding="utf-8")

    capsule_path = tmp_path / "party.mcgo-capsule"
    settings = {version_id: {"alias": "Party Pack", "use_isolated_directory": True}}
    payload = export_capsule(
        str(game_dir),
        settings,
        version_id,
        str(capsule_path),
        p2p_info={"room": "ROOM1", "relay_host": "relay.example", "relay_port": 10721},
    )

    assert payload["file_count"] == 2
    manifest = read_capsule_manifest(str(capsule_path))
    assert manifest["p2p"]["room"] == "ROOM1"
    with zipfile.ZipFile(capsule_path, "r") as archive:
        assert CAPSULE_MANIFEST in archive.namelist()
        assert archive.namelist() == [CAPSULE_MANIFEST]

    imported_settings = {"_meta": {}}
    imported = import_capsule(
        str(capsule_path),
        str(game_dir),
        imported_settings,
        target_version_id="ImportedParty",
    )

    imported_dir = game_dir / "versions" / "ImportedParty"
    assert imported["extracted"] == 0
    assert imported["verified"] == 0
    assert len(imported["missing_files"]) == 2
    assert not (imported_dir / "mods" / "private.jar").exists()
    assert not (imported_dir / "config" / "private.json").exists()
    assert imported_settings["ImportedParty"]["alias"] == "Party Pack"
    assert imported_settings["ImportedParty"]["use_isolated_directory"] is True
