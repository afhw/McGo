import json
import os
import shutil
import subprocess
import tempfile
import time
import zipfile

import http_client
from install_services import (
    INSTALL_TYPE_LABELS,
    build_optifine_library_version,
    build_optifine_version_id,
    download_profile_libraries,
    get_fabric_api_versions,
    get_fabric_loader_versions,
    get_forge_version_for_mc,
    get_neoforge_list,
    get_optifine_list,
    library_relative_path,
    mirror_root,
    select_optifine_candidate,
    stream_download,
    version_key,
)
from launcher import get_local_versions, get_version_json
from process_utils import hidden_subprocess_kwargs
from storage_utils import save_json_atomic
from version_utils import (
    find_matching_fabric_versions,
    load_version_settings,
    resolve_base_minecraft_version,
    runtime_directory_for_version,
)


class InstallerEngine:
    def __init__(self, minecraft_version, mirror_source, game_dir, java_path, status_callback=None, progress_callback=None, global_isolation=True):
        self.minecraft_version = minecraft_version
        self.mirror_source = mirror_source
        self.game_dir = os.path.abspath(game_dir)
        self.java_path = java_path
        self.status_callback = status_callback
        self.progress_callback = progress_callback
        self.global_isolation = global_isolation

    def emit_status(self, message):
        if self.status_callback:
            self.status_callback(message)

    def emit_snapshot(self, snapshot):
        if self.progress_callback:
            self.progress_callback(snapshot)

    def emit_progress_text(self, phase, current_file="", progress=0.0, completed_files=0, total_files=0):
        self.emit_snapshot({
            "progress": progress,
            "phase": phase,
            "current_file": current_file,
            "downloaded_bytes": completed_files,
            "total_bytes": total_files,
            "speed_bytes": 0,
            "completed_files": completed_files,
            "total_files": total_files,
            "reused_files": 0,
        })

    def runtime_directory_for(self, version_id):
        settings = load_version_settings()
        return runtime_directory_for_version(
            self.game_dir,
            settings,
            version_id,
            global_isolation=self.global_isolation,
        )

    def _resolve_base_minecraft_version(self):
        return resolve_base_minecraft_version(self.game_dir, self.minecraft_version)

    def _find_matching_fabric_versions(self, base_minecraft_version):
        return find_matching_fabric_versions(self.game_dir, base_minecraft_version)

    def ensure_launcher_profiles(self):
        launcher_profiles_path = os.path.join(self.game_dir, "launcher_profiles.json")
        if os.path.exists(launcher_profiles_path):
            return
        with open(launcher_profiles_path, "w", encoding="utf-8") as file_handle:
            json.dump({"profiles": {}, "settings": {}}, file_handle, ensure_ascii=False, indent=2)

    def _optifine_library_path(self, library_version):
        return os.path.join(
            self.game_dir,
            "libraries",
            "optifine",
            "OptiFine",
            library_version,
            f"OptiFine-{library_version}.jar",
        )

    def _ensure_launchwrapper_library(self, archive):
        launchwrapper_entries = [
            name for name in archive.namelist()
            if name.lower().startswith("launchwrapper-of-") and name.lower().endswith(".jar")
        ]
        if launchwrapper_entries:
            entry = launchwrapper_entries[0]
            version = entry.rsplit("-", 1)[-1][:-4]
            target_path = os.path.join(
                self.game_dir,
                "libraries",
                "optifine",
                "launchwrapper-of",
                version,
                f"launchwrapper-of-{version}.jar",
            )
            os.makedirs(os.path.dirname(target_path), exist_ok=True)
            with archive.open(entry) as src, open(target_path, "wb") as dst:
                shutil.copyfileobj(src, dst)
            return {
                "name": f"optifine:launchwrapper-of:{version}",
                "path": target_path,
            }

        fallback_name = "net.minecraft:launchwrapper:1.12"
        fallback_relative = library_relative_path(fallback_name)
        fallback_path = os.path.join(self.game_dir, "libraries", fallback_relative)
        if not os.path.exists(fallback_path):
            library_url = f"{mirror_root(self.mirror_source)}/maven/{fallback_relative.replace(os.sep, '/')}"
            self.emit_status("正在补充 LaunchWrapper 依赖")
            stream_download(library_url, fallback_path, self.emit_snapshot, "下载 LaunchWrapper")
        return {
            "name": fallback_name,
            "path": fallback_path,
        }

    def _write_optifine_version_json(self, version_id, base_json, optifine_library_name, launchwrapper_library_name):
        version_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(version_dir, exist_ok=True)

        version_json = {
            "id": version_id,
            "inheritsFrom": self.minecraft_version,
            "mainClass": "net.minecraft.launchwrapper.Launch",
            "type": base_json.get("type", "release"),
            "libraries": [
                {"name": launchwrapper_library_name},
                {"name": optifine_library_name},
            ],
        }

        if base_json.get("releaseTime"):
            version_json["releaseTime"] = base_json["releaseTime"]
        if base_json.get("time"):
            version_json["time"] = base_json["time"]

        modern_game_args = base_json.get("arguments", {}).get("game")
        if modern_game_args:
            version_json["arguments"] = {
                "game": ["--tweakClass", "optifine.OptiFineTweaker"],
                "jvm": [],
            }
        else:
            legacy_arguments = (base_json.get("minecraftArguments") or "").strip()
            tweak_argument = "--tweakClass optifine.OptiFineTweaker"
            if tweak_argument not in legacy_arguments:
                legacy_arguments = f"{legacy_arguments} {tweak_argument}".strip()
            version_json["minecraftArguments"] = legacy_arguments

        version_json_path = os.path.join(version_dir, f"{version_id}.json")
        with open(version_json_path, "w", encoding="utf-8") as file_handle:
            json.dump(version_json, file_handle, ensure_ascii=False, indent=4)

    def _install_fabric(self):
        base_minecraft_version = self._resolve_base_minecraft_version()
        if base_minecraft_version != self.minecraft_version:
            raise RuntimeError("安装 Fabric 时请选择原版 Minecraft 版本，而不是已安装的 Fabric 版本。")

        versions = get_fabric_loader_versions(self.minecraft_version, self.mirror_source)
        if not versions:
            raise RuntimeError(f"未找到适用于 {self.minecraft_version} 的 Fabric Loader。")

        selected = versions[0]
        loader_version = selected["loader"]["version"]
        profile_url = (
            f"{mirror_root(self.mirror_source)}/fabric-meta/v2/versions/loader/"
            f"{self.minecraft_version}/{loader_version}/profile/json"
        )
        self.emit_status(f"正在获取 Fabric 安装配置：{self.minecraft_version} / Loader {loader_version}")
        response = http_client.get(profile_url, timeout=30)
        http_client.raise_for_status(response, "获取 Fabric 安装配置")
        profile_json = response.json()

        version_id = profile_json.get("id") or f"fabric-loader-{loader_version}-{self.minecraft_version}"
        version_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(version_dir, exist_ok=True)
        version_json_path = os.path.join(version_dir, f"{version_id}.json")
        save_json_atomic(version_json_path, profile_json, indent=4)

        if not get_version_json(self.game_dir, self.minecraft_version):
            raise RuntimeError(f"请先下载原版 Minecraft {self.minecraft_version}。")

        download_profile_libraries(profile_json, self.game_dir, self.mirror_source, self.emit_snapshot)

        self.emit_snapshot({
            "progress": 1.0,
            "phase": "Fabric 安装完成",
            "current_file": version_id,
            "downloaded_bytes": 0,
            "total_bytes": 0,
            "speed_bytes": 0,
            "completed_files": 1,
            "total_files": 1,
            "reused_files": 0,
        })
        return {"installed_version": version_id, "message": f"Fabric {loader_version} 已安装"}

    def _install_fabric_api(self):
        base_minecraft_version = self._resolve_base_minecraft_version()
        fabric_versions = self._find_matching_fabric_versions(base_minecraft_version)
        if not fabric_versions:
            raise RuntimeError(f"请先为 Minecraft {base_minecraft_version} 安装 Fabric，再安装 Fabric API。")
        versions = get_fabric_api_versions(base_minecraft_version)
        if not versions:
            raise RuntimeError(f"未找到适用于 {base_minecraft_version} 的 Fabric API。")

        preferred = [item for item in versions if item.get("version_type") == "release"]
        selected = (preferred or versions)[0]
        files = selected.get("files", [])
        primary = next((item for item in files if item.get("primary")), None) or (files[0] if files else None)
        if not primary or not primary.get("url"):
            raise RuntimeError("Fabric API 元数据缺少可下载文件。")

        target_version = fabric_versions[0]
        mods_dir = os.path.join(self.runtime_directory_for(target_version), "mods")
        os.makedirs(mods_dir, exist_ok=True)
        filename = primary.get("filename") or f"fabric-api-{base_minecraft_version}.jar"
        target_path = os.path.join(mods_dir, filename)
        self.emit_status(f"正在安装 Fabric API：{selected.get('version_number', base_minecraft_version)}")
        stream_download(primary["url"], target_path, self.emit_snapshot, "下载 Fabric API")
        return {
            "installed_version": target_version,
            "message": f"Fabric API 已安装：{filename}",
        }

    def _install_forge_like(self, kind):
        if not self.java_path:
            raise RuntimeError(f"安装 {kind} 需要可用的 Java。")
        os.makedirs(self.game_dir, exist_ok=True)
        self.ensure_launcher_profiles()

        if kind == "forge":
            forge_version = get_forge_version_for_mc(self.minecraft_version, self.mirror_source)
            if not forge_version:
                raise RuntimeError(f"未找到适用于 {self.minecraft_version} 的 Forge 版本。")
            installer_url = (
                f"{mirror_root(self.mirror_source)}/maven/net/minecraftforge/forge/"
                f"{self.minecraft_version}-{forge_version}/forge-{self.minecraft_version}-{forge_version}-installer.jar"
            )
            label = f"Forge {forge_version}"
        else:
            versions = get_neoforge_list(self.minecraft_version, self.mirror_source)
            if not versions:
                raise RuntimeError(f"未找到适用于 {self.minecraft_version} 的 NeoForge 版本。")
            selected_entry = sorted(versions, key=lambda item: version_key(item.get("rawVersion", "")), reverse=True)[0]
            installer_path = selected_entry.get("installerPath")
            selected = selected_entry.get("version") or selected_entry.get("rawVersion")
            if installer_path:
                installer_url = f"{mirror_root(self.mirror_source)}{installer_path}"
            else:
                selected_value = selected_entry.get("version") or (selected_entry.get("rawVersion") or "").replace("neoforge-", "")
                installer_url = (
                    f"{mirror_root(self.mirror_source)}/maven/net/neoforged/neoforge/"
                    f"{selected_value}/neoforge-{selected_value}-installer.jar"
                )
            label = f"NeoForge {selected}"

        temp_dir = tempfile.mkdtemp(prefix=f"mcgo-{kind}-")
        try:
            installer_path = os.path.join(temp_dir, f"{kind}-installer.jar")
            self.emit_status(f"正在下载 {label} 安装器")
            stream_download(installer_url, installer_path, self.emit_snapshot, f"下载 {label} 安装器")
            self.emit_status(f"正在安装 {label}")
            before_versions = set(get_local_versions(self.game_dir))
            install_args = [self.java_path, "-jar", installer_path]
            if kind == "neoforge":
                install_args.extend(["--install-client", self.game_dir])
            else:
                install_args.extend(["--installClient", self.game_dir])
            process = subprocess.Popen(
                install_args,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                cwd=temp_dir,
                encoding="utf-8",
                errors="replace",
                **hidden_subprocess_kwargs(),
            )
            collected_output = []
            self.emit_progress_text(f"正在执行 {label} 安装器", os.path.basename(installer_path), progress=0.5, completed_files=1, total_files=2)
            start_time = time.monotonic()
            while True:
                line = process.stdout.readline() if process.stdout else ""
                if line:
                    cleaned = line.rstrip()
                    if cleaned:
                        collected_output.append(cleaned)
                        self.emit_status(cleaned)
                if process.poll() is not None:
                    break
                if time.monotonic() - start_time > 600:
                    process.kill()
                    raise RuntimeError(f"{label} 安装超时。")
            remaining = process.stdout.read() if process.stdout else ""
            if remaining:
                for line in remaining.splitlines():
                    cleaned = line.rstrip()
                    if cleaned:
                        collected_output.append(cleaned)
                        self.emit_status(cleaned)
            if process.returncode != 0:
                output = "\n".join(collected_output).strip()
                raise RuntimeError(output or f"{label} 安装失败，退出码 {process.returncode}")
            self.emit_progress_text(f"{label} 安装收尾中", "", progress=0.95, completed_files=2, total_files=2)
            after_versions = set(get_local_versions(self.game_dir))
            new_versions = sorted(after_versions - before_versions)
            installed_version = new_versions[-1] if new_versions else self.minecraft_version
            output = "\n".join(collected_output).strip()
            return {"installed_version": installed_version, "message": output or f"{label} 安装完成"}
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)

    def _install_optifine(self):
        base_json = get_version_json(self.game_dir, self.minecraft_version)
        if not base_json:
            raise RuntimeError(f"请先在当前游戏目录下载原版 Minecraft {self.minecraft_version}，再安装 OptiFine。")

        versions = get_optifine_list(self.mirror_source)
        candidates = [item for item in versions if str(item.get("mcversion")) == self.minecraft_version]
        selected = select_optifine_candidate(candidates)
        if not selected:
            raise RuntimeError(f"未找到适用于 {self.minecraft_version} 的 OptiFine。")

        patch = selected.get("patch") or selected.get("type")
        optifine_type = selected.get("type") or "HD_U"
        version_id = build_optifine_version_id(self.minecraft_version, selected)
        library_version = build_optifine_library_version(selected)
        optifine_library_name = f"optifine:OptiFine:{library_version}"
        installer_url = f"{mirror_root(self.mirror_source)}/optifine/{self.minecraft_version}/{optifine_type}/{patch}"

        temp_dir = tempfile.mkdtemp(prefix="mcgo-optifine-")
        try:
            installer_path = os.path.join(temp_dir, "optifine-installer.jar")
            self.emit_status(f"正在下载 OptiFine {self.minecraft_version} {optifine_type} {patch}")
            stream_download(installer_url, installer_path, self.emit_snapshot, "下载 OptiFine 安装包")

            optifine_library_path = self._optifine_library_path(library_version)
            os.makedirs(os.path.dirname(optifine_library_path), exist_ok=True)
            shutil.copy2(installer_path, optifine_library_path)

            with zipfile.ZipFile(installer_path) as archive:
                launchwrapper = self._ensure_launchwrapper_library(archive)

            self.emit_status(f"正在生成 OptiFine 版本 {version_id}")
            self._write_optifine_version_json(
                version_id,
                base_json,
                optifine_library_name,
                launchwrapper["name"],
            )
            self.emit_snapshot({
                "progress": 1.0,
                "phase": "OptiFine 安装完成",
                "current_file": version_id,
                "downloaded_bytes": 0,
                "total_bytes": 0,
                "speed_bytes": 0,
                "completed_files": 1,
                "total_files": 1,
                "reused_files": 0,
            })
            return {
                "installed_version": version_id,
                "message": f"OptiFine 已安装：{version_id}",
            }
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)

    def install(self, install_type):
        if install_type == "fabric":
            return self._install_fabric()
        if install_type == "fabric_api":
            return self._install_fabric_api()
        if install_type == "forge":
            return self._install_forge_like("forge")
        if install_type == "neoforge":
            return self._install_forge_like("neoforge")
        if install_type == "optifine":
            return self._install_optifine()
        raise RuntimeError(f"不支持的安装类型：{install_type}")

    def install_sequence(self, install_types):
        messages = []
        installed_version = self.minecraft_version
        for install_type in install_types:
            self.emit_status(f"正在安装 {INSTALL_TYPE_LABELS.get(install_type, install_type)}")
            payload = self.install(install_type)
            messages.append(payload.get("message", INSTALL_TYPE_LABELS.get(install_type, install_type)))
            if payload.get("installed_version") and install_type != "fabric_api":
                installed_version = payload["installed_version"]
        return {
            "installed_version": installed_version,
            "message": "；".join(messages),
            "steps": list(install_types),
            "install_type": install_types[-1] if install_types else "",
        }


