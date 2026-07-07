from app_format import format_bytes, format_resource_hit_label


def test_format_bytes_uses_binary_units():
    assert format_bytes(0) == "0 B"
    assert format_bytes(1536) == "1.5 KB"
    assert format_bytes(2 * 1024 * 1024) == "2.0 MB"


def test_format_resource_hit_label_includes_compatibility():
    label = format_resource_hit_label({
        "source": "modrinth",
        "title": "Sodium",
        "downloads": 123,
        "follows": 45,
        "target_game_version": "1.20.1",
        "target_loader": "fabric",
        "compatible_version": "mc1.20.1",
        "description": "Fast renderer\nfor Minecraft",
    })

    assert "Sodium" in label
    assert "兼容 1.20.1 / fabric" in label
    assert "Fast renderer for Minecraft" in label
