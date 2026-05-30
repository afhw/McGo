import os
import sys
import tarfile
import zipfile

import http_client


def current_platform_name():
    if sys.platform.startswith("win"):
        return "windows"
    if sys.platform == "darwin":
        return "mac"
    return "linux"


def current_arch_name():
    machine = os.environ.get("PROCESSOR_ARCHITECTURE", "")
    if not machine and hasattr(os, "uname"):
        machine = os.uname().machine
    machine = str(machine).lower()
    if machine in {"amd64", "x86_64"}:
        return "x64"
    if machine in {"aarch64", "arm64"}:
        return "aarch64"
    if machine in {"x86", "i386", "i686"}:
        return "x32"
    return "x64"


def java_runtime_download_url(major_version):
    response = http_client.get(
        f"https://api.adoptium.net/v3/assets/latest/{major_version}/hotspot",
        params={
            "architecture": current_arch_name(),
            "image_type": "jre",
            "os": current_platform_name(),
            "vendor": "eclipse",
        },
        headers={"User-Agent": "McGo/1.0"},
        timeout=30,
    )
    http_client.raise_for_status(response, "获取 Java 下载地址")
    data = response.json()
    if not data:
        raise RuntimeError(f"未找到 Java {major_version} 的可下载运行时。")
    package = data[0].get("binary", {}).get("package", {})
    link = package.get("link")
    if not link:
        raise RuntimeError("Adoptium 返回数据缺少下载链接。")
    return link, package.get("name") or os.path.basename(link)


def find_java_in_directory(root_dir):
    candidates = []
    for current_root, _, files in os.walk(root_dir):
        for filename in files:
            if filename not in ("java", "java.exe"):
                continue
            path = os.path.join(current_root, filename)
            if os.path.basename(os.path.dirname(path)).lower() == "bin":
                candidates.append(path)
    candidates.sort(key=lambda path: (len(path), path))
    return candidates[0] if candidates else ""


def extract_java_archive(archive_path, target_dir):
    if zipfile.is_zipfile(archive_path):
        with zipfile.ZipFile(archive_path, "r") as archive:
            archive.extractall(target_dir)
        return
    if tarfile.is_tarfile(archive_path):
        with tarfile.open(archive_path, "r:*") as archive:
            archive.extractall(target_dir)
        return
    raise RuntimeError("Java 运行时压缩包格式不受支持。")
