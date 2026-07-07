import json
import os

from launcher import build_launch_command, infer_required_java_version


def write_version(root, version_id, data, jar=True):
    version_dir = root / "versions" / version_id
    version_dir.mkdir(parents=True)
    (version_dir / f"{version_id}.json").write_text(json.dumps({"id": version_id, **data}), encoding="utf-8")
    if jar:
        (version_dir / f"{version_id}.jar").write_bytes(b"jar")


def test_infer_required_java_uses_manifest_major(tmp_path):
    write_version(
        tmp_path,
        "1.20.1",
        {"javaVersion": {"majorVersion": 17}, "libraries": [], "mainClass": "net.minecraft.client.main.Main"},
    )
    assert infer_required_java_version(str(tmp_path), "1.20.1") == 17


def test_build_launch_command_includes_runtime_and_memory(tmp_path):
    library_path = tmp_path / "libraries" / "com" / "example" / "demo" / "1.0" / "demo-1.0.jar"
    library_path.parent.mkdir(parents=True)
    library_path.write_bytes(b"lib")
    write_version(
        tmp_path,
        "1.20.1",
        {
            "mainClass": "net.minecraft.client.main.Main",
            "assetIndex": {"id": "1"},
            "libraries": [{"name": "com.example:demo:1.0"}],
            "arguments": {"game": ["--username", "${auth_player_name}"], "jvm": []},
        },
    )
    runtime_dir = tmp_path / "runtime"
    command = build_launch_command(
        "java",
        "1.20.1",
        game_directory=str(tmp_path),
        username="Steve",
        runtime_directory=str(runtime_dir),
        max_memory_mb=3072,
    )
    assert "-Xmx3072M" in command
    assert "net.minecraft.client.main.Main" in command
    assert "Steve" in command
    assert os.pathsep.join([str(library_path), str(tmp_path / "versions" / "1.20.1" / "1.20.1.jar")]) in command
