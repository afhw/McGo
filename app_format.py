from resource_market import RESOURCE_SOURCE_LABELS


def format_bytes(byte_count):
    units = ["B", "KB", "MB", "GB"]
    value = float(max(0, byte_count))
    for unit in units:
        if value < 1024 or unit == units[-1]:
            return f"{value:.1f} {unit}" if unit != "B" else f"{int(value)} {unit}"
        value /= 1024


def format_resource_hit_label(hit):
    title = hit.get("title") or hit.get("slug") or hit.get("project_id", "未命名")
    downloads = hit.get("downloads", 0)
    follows = hit.get("follows", 0)
    source = RESOURCE_SOURCE_LABELS.get(hit.get("source", "modrinth"), hit.get("source", ""))
    description = (hit.get("description") or "").replace("\n", " ").strip()
    downloads_text = format_bytes(downloads) if hit.get("source") == "local" else downloads
    compatibility = ""
    if hit.get("source") == "modrinth":
        if hit.get("compatibility_checking"):
            compatibility = f" | 正在验证 {hit.get('target_game_version', '')}"
            if hit.get("target_loader"):
                compatibility += f" / {hit.get('target_loader')}"
        elif hit.get("compatible", True):
            version_label = hit.get("compatible_version", "")
            compatibility = f" | 兼容 {hit.get('target_game_version', '')}"
            if hit.get("target_loader"):
                compatibility += f" / {hit.get('target_loader')}"
            if version_label:
                compatibility += f" | 文件 {version_label}"
            elif hit.get("compatibility_unverified"):
                compatibility += " | 等待验证"
        else:
            compatibility = f" | 不兼容 {hit.get('target_game_version', '')}"
            if hit.get("target_loader"):
                compatibility += f" / {hit.get('target_loader')}"
    label = f"[{source}] {title} | 下载 {downloads_text} | 收藏 {follows}{compatibility}"
    if description:
        label += f"\n{description[:160]}"
    return label
