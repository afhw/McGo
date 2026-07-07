import json

from installer_engine import InstallerEngine


def write_version(game_dir, version_id, payload):
    version_dir = game_dir / "versions" / version_id
    version_dir.mkdir(parents=True)
    (version_dir / f"{version_id}.json").write_text(
        json.dumps({"id": version_id, **payload}),
        encoding="utf-8",
    )


def test_loader_install_uses_base_version_when_target_is_fabric(tmp_path):
    write_version(tmp_path, "1.20.1", {"type": "release"})
    write_version(
        tmp_path,
        "fabric-loader-0.16.0-1.20.1",
        {"inheritsFrom": "1.20.1", "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient"},
    )
    statuses = []
    engine = InstallerEngine(
        "fabric-loader-0.16.0-1.20.1",
        "official",
        str(tmp_path),
        "",
        status_callback=statuses.append,
    )

    target = engine._use_base_minecraft_version_for_loader("Fabric")

    assert target == "1.20.1"
    assert engine.minecraft_version == "1.20.1"
    assert any("自动切换" in status for status in statuses)
