import asyncio
import json
import os
import shutil
import zipfile

import http_client
from PyQt6.QtCore import QObject, pyqtSignal as Signal

from downloader import download_game_files, extract_natives
from file_utils import sanitize_filename
from install_services import INSTALL_TYPE_LABELS, MIRROR_SOURCES, get_version_metadata_with_fallback
from installer_engine import InstallerEngine
from launcher import get_version_json


class ModpackImportWorker(QObject):
    progress = Signal(int)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(
        self,
        pack_path,
        game_dir,
        mirror_source="official",
        java_path="",
        download_options=None,
        global_isolation=True,
    ):
        super().__init__()
        self.pack_path = pack_path
        self.game_dir = os.path.abspath(game_dir)
        self.mirror_source = mirror_source
        self.java_path = java_path
        self.download_options = dict(download_options or {})
        self.global_isolation = global_isolation
        self.missing_files = []

    def _safe_join(self, root, relative_path):
        normalized = os.path.normpath(relative_path).replace("\\", os.sep).lstrip(os.sep)
        target = os.path.abspath(os.path.join(root, normalized))
        root_abs = os.path.abspath(root)
        if not target.startswith(root_abs + os.sep) and target != root_abs:
            raise RuntimeError(f"整合包包含不安全路径：{relative_path}")
        return target

    def _extract_prefix(self, archive, prefix, target_dir):
        prefix = prefix.strip("/")
        if prefix:
            prefix = prefix + "/"
        copied = 0
        for item in archive.infolist():
            name = item.filename.replace("\\", "/")
            if item.is_dir() or (prefix and not name.startswith(prefix)):
                continue
            relative = name[len(prefix):] if prefix else name
            if not relative:
                continue
            target = self._safe_join(target_dir, relative)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with archive.open(item) as src, open(target, "wb") as dst:
                shutil.copyfileobj(src, dst)
            copied += 1
        return copied

    def _read_json(self, archive, name):
        with archive.open(name) as handle:
            return json.loads(handle.read().decode("utf-8-sig"))

    def _find_entry(self, archive, candidates):
        names = {item.filename.replace("\\", "/"): item.filename for item in archive.infolist()}
        for candidate in candidates:
            if candidate in names:
                return names[candidate]
        for name in names:
            for candidate in candidates:
                if name.endswith("/" + candidate):
                    return names[name]
        return ""

    def _download_file(self, url, target_path):
        os.makedirs(os.path.dirname(target_path), exist_ok=True)
        with http_client.get(url, stream=True, timeout=60) as response:
            http_client.raise_for_status(response, "下载整合包文件")
            with open(target_path, "wb") as handle:
                for chunk in response.iter_content(chunk_size=128 * 1024):
                    if chunk:
                        handle.write(chunk)

    def _ensure_minecraft_version(self, minecraft_version):
        if not minecraft_version or get_version_json(self.game_dir, minecraft_version):
            return
        self.status.emit(f"正在下载整合包基础版本 {minecraft_version}...")
        version_json, resolved_source = get_version_metadata_with_fallback(
            minecraft_version,
            self.mirror_source,
            status_callback=self.status.emit,
        )
        asyncio.run(download_game_files(
            version_json,
            self.game_dir,
            minecraft_version,
            MIRROR_SOURCES[resolved_source],
            progress_callback=None,
            **self.download_options,
        ))
        extract_natives(version_json, self.game_dir, minecraft_version)

    def _install_declared_loader(self, minecraft_version, loader_key):
        if not minecraft_version or not loader_key:
            return minecraft_version

        normalized_loader = str(loader_key).lower().strip()
        if normalized_loader.startswith("fabric"):
            normalized_loader = "fabric"
        elif normalized_loader.startswith("forge"):
            normalized_loader = "forge"
        elif normalized_loader.startswith("neoforge"):
            normalized_loader = "neoforge"

        install_type = {
            "fabric-loader": "fabric",
            "fabric": "fabric",
            "forge": "forge",
            "neoforge": "neoforge",
        }.get(normalized_loader)
        if not install_type:
            self.missing_files.append({
                "path": "loader",
                "reason": f"暂不支持自动安装加载器：{loader_key}",
            })
            return minecraft_version
        if install_type in {"forge", "neoforge"} and not self.java_path:
            raise RuntimeError(f"整合包声明需要 {INSTALL_TYPE_LABELS[install_type]}，请先在环境中选择 Java。")

        self.status.emit(f"正在安装整合包加载器 {INSTALL_TYPE_LABELS.get(install_type, install_type)}...")
        engine = InstallerEngine(
            minecraft_version,
            self.mirror_source,
            self.game_dir,
            self.java_path,
            status_callback=self.status.emit,
            progress_callback=None,
            global_isolation=self.global_isolation,
        )
        payload = engine.install(install_type)
        return payload.get("installed_version") or minecraft_version

    def _write_imported_version(self, version_id, minecraft_version, installed_base_version):
        version_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(version_dir, exist_ok=True)
        source_json = get_version_json(self.game_dir, installed_base_version or minecraft_version)
        if not source_json:
            return

        version_json = dict(source_json)
        version_json["id"] = version_id
        if installed_base_version and installed_base_version != version_id:
            version_json = {
                "id": version_id,
                "inheritsFrom": installed_base_version,
                "type": source_json.get("type", "release"),
                "time": source_json.get("time", ""),
                "releaseTime": source_json.get("releaseTime", ""),
            }
        with open(os.path.join(version_dir, f"{version_id}.json"), "w", encoding="utf-8") as file_handle:
            json.dump(version_json, file_handle, ensure_ascii=False, indent=4)

    def _write_missing_report(self, version_id):
        if not self.missing_files:
            return ""
        report_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(report_dir, exist_ok=True)
        report_path = os.path.join(report_dir, "missing-modpack-files.json")
        with open(report_path, "w", encoding="utf-8") as file_handle:
            json.dump(self.missing_files, file_handle, ensure_ascii=False, indent=2)
        return report_path

    def _copy_override_file(self, archive, item, runtime_dir, prefix):
        name = item.filename.replace("\\", "/")
        relative = name[len(prefix):] if prefix else name
        if not relative:
            return False
        target = self._safe_join(runtime_dir, relative)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with archive.open(item) as src, open(target, "wb") as dst:
            shutil.copyfileobj(src, dst)
        return True

    def _extract_overrides(self, archive, runtime_dir, prefixes):
        copied = 0
        normalized_prefixes = []
        for prefix in prefixes:
            prefix = prefix.strip("/")
            normalized_prefixes.append(f"{prefix}/" if prefix else "")
        for item in archive.infolist():
            name = item.filename.replace("\\", "/")
            if item.is_dir():
                continue
            for prefix in normalized_prefixes:
                if prefix and not name.startswith(prefix):
                    continue
                if self._copy_override_file(archive, item, runtime_dir, prefix):
                    copied += 1
                break
        return copied

    def _curseforge_file_url(self, project_id, file_id):
        return f"https://edge.forgecdn.net/files/{str(file_id)[:-3]}/{str(file_id)[-3:]}/{file_id}"

    def _download_curseforge_files(self, files, runtime_dir):
        downloaded = 0
        optional = 0
        for idx, item in enumerate(files, start=1):
            project_id = item.get("projectID")
            file_id = item.get("fileID")
            required = item.get("required", True)
            if not project_id or not file_id:
                continue
            if not required:
                optional += 1
                self.missing_files.append({
                    "projectID": project_id,
                    "fileID": file_id,
                    "reason": "可选文件，未自动下载",
                })
                continue
            self.status.emit(f"正在下载 CurseForge 文件 {idx}/{len(files)}：{project_id}/{file_id}")
            self.progress.emit(min(95, int(idx / max(len(files), 1) * 75) + 15))
            try:
                meta_response = http_client.get(
                    f"https://api.cfwidget.com/minecraft/mc-mods/{project_id}",
                    timeout=20,
                )
                filename = f"{project_id}-{file_id}.jar"
                if meta_response.ok:
                    data = meta_response.json()
                    for candidate in data.get("files", []):
                        if str(candidate.get("id")) == str(file_id):
                            filename = candidate.get("name") or candidate.get("displayName") or filename
                            if not filename.lower().endswith(".jar"):
                                filename = f"{filename}.jar"
                            break
                target_path = os.path.join(runtime_dir, "mods", sanitize_filename(filename, f"{project_id}-{file_id}.jar"))
                self._download_file(self._curseforge_file_url(project_id, file_id), target_path)
                downloaded += 1
            except Exception as exc:
                self.missing_files.append({
                    "projectID": project_id,
                    "fileID": file_id,
                    "reason": str(exc),
                    "manual_url": f"https://www.curseforge.com/minecraft/mc-mods/{project_id}/files/{file_id}",
                })
        return downloaded, optional

    def _discover_import_metadata(self, payload):
        minecraft_version = ""
        loader_key = ""
        pack_name = ""

        if not isinstance(payload, dict):
            return minecraft_version, loader_key, pack_name

        pack_name = (
            payload.get("name")
            or payload.get("displayName")
            or payload.get("instanceName")
            or payload.get("title")
            or ""
        )
        minecraft_version = (
            payload.get("minecraft")
            or payload.get("minecraftVersion")
            or payload.get("gameVersion")
            or payload.get("mcversion")
            or ""
        )
        loader_key = payload.get("loader") or payload.get("modLoader") or payload.get("loaderType") or ""

        components = payload.get("components") or payload.get("addons") or payload.get("loaders") or []
        if isinstance(components, dict):
            components = [{"uid": key, "version": value} for key, value in components.items()]
        for component in components if isinstance(components, list) else []:
            if not isinstance(component, dict):
                continue
            uid = str(component.get("uid") or component.get("id") or component.get("name") or "").lower()
            version = str(component.get("version") or component.get("versionNumber") or "")
            if not minecraft_version and ("minecraft" == uid or uid.endswith("minecraft")):
                minecraft_version = version
            if not loader_key:
                if "fabric" in uid:
                    loader_key = "fabric"
                elif "neoforge" in uid:
                    loader_key = "neoforge"
                elif "forge" in uid:
                    loader_key = "forge"
                elif "quilt" in uid:
                    loader_key = "quilt"

        launch = payload.get("launch") or payload.get("version") or {}
        if isinstance(launch, dict):
            minecraft_version = minecraft_version or launch.get("minecraft") or launch.get("minecraftVersion") or ""
            loader_key = loader_key or launch.get("loader") or launch.get("modLoader") or ""

        return str(minecraft_version), str(loader_key).lower(), str(pack_name)

    def _read_text(self, archive, name):
        with archive.open(name) as handle:
            return handle.read().decode("utf-8-sig", errors="replace")

    def _parse_instance_cfg(self, text):
        payload = {"components": []}
        for raw_line in text.splitlines():
            line = raw_line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            key = key.strip()
            value = value.strip()
            lowered = key.lower()
            if lowered in {"name", "instancename"}:
                payload["name"] = value
            elif "minecraft" in lowered and "version" in lowered:
                payload["minecraftVersion"] = value
            elif "fabric" in lowered:
                payload["components"].append({"uid": "fabric", "version": value})
            elif "neoforge" in lowered:
                payload["components"].append({"uid": "neoforge", "version": value})
            elif "forge" in lowered:
                payload["components"].append({"uid": "forge", "version": value})
            elif "quilt" in lowered:
                payload["components"].append({"uid": "quilt", "version": value})
        return payload

    def _install_generic_manifest_pack(self, archive, manifest_entry, pack_kind):
        if manifest_entry.lower().endswith(".json") or manifest_entry.lower().endswith(".packmeta"):
            manifest = self._read_json(archive, manifest_entry)
        else:
            manifest = self._parse_instance_cfg(self._read_text(archive, manifest_entry))
        minecraft_version, loader_key, manifest_name = self._discover_import_metadata(manifest)
        pack_name = (manifest_name or os.path.splitext(os.path.basename(self.pack_path))[0]).strip()
        version_id = sanitize_filename(pack_name, f"{pack_kind}-{minecraft_version or 'Imported'}")
        runtime_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(runtime_dir, exist_ok=True)

        if minecraft_version:
            self.status.emit("正在准备基础版本和加载器...")
            self._ensure_minecraft_version(minecraft_version)
            installed_base_version = self._install_declared_loader(minecraft_version, loader_key)
            self._write_imported_version(version_id, minecraft_version, installed_base_version)
        else:
            self.missing_files.append({
                "path": manifest_entry,
                "reason": "未识别到 Minecraft 版本，已按普通覆写包导入。",
            })

        base = manifest_entry.replace("\\", "/").rsplit("/", 1)[0]
        base_prefix = f"{base}/" if base else ""
        prefixes = [
            base_prefix + "overrides",
            base_prefix + "minecraft",
            base_prefix + ".minecraft",
            base_prefix + "instance",
            base_prefix + "mmc-pack",
        ]
        copied = self._extract_overrides(archive, runtime_dir, prefixes)
        if copied == 0:
            copied = self._extract_prefix(archive, "", runtime_dir)

        missing_report = self._write_missing_report(version_id)
        return {
            "version": version_id,
            "alias": pack_name,
            "minecraft": minecraft_version,
            "loader": loader_key,
            "message": f"{pack_kind} 整合包已导入：覆写 {copied} 个文件，缺失 {len(self.missing_files)} 项。",
            "missing_report": missing_report,
        }

    def _install_modrinth(self, archive, index_entry):
        index = self._read_json(archive, index_entry)
        dependencies = index.get("dependencies", {})
        minecraft_version = dependencies.get("minecraft", "")
        pack_name = (index.get("name") or os.path.splitext(os.path.basename(self.pack_path))[0]).strip()
        version_id = sanitize_filename(pack_name, f"Modrinth-{minecraft_version}")
        runtime_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(runtime_dir, exist_ok=True)

        self.status.emit("正在准备基础版本和加载器...")
        loader_key = next((key for key in ("fabric-loader", "forge", "neoforge", "quilt-loader") if dependencies.get(key)), "")
        self._ensure_minecraft_version(minecraft_version)
        installed_base_version = self._install_declared_loader(minecraft_version, loader_key)
        self._write_imported_version(version_id, minecraft_version, installed_base_version)

        self.status.emit("正在解压 overrides...")
        base = index_entry.replace("\\", "/").rsplit("/", 1)[0]
        base_prefix = f"{base}/" if base else ""
        copied = self._extract_overrides(archive, runtime_dir, [base_prefix + "overrides", base_prefix + "client-overrides"])

        files = index.get("files", [])
        for idx, item in enumerate(files, start=1):
            path = item.get("path", "")
            downloads = item.get("downloads", [])
            if not path or not downloads:
                continue
            self.status.emit(f"正在下载整合包文件 {idx}/{len(files)}：{os.path.basename(path)}")
            self.progress.emit(min(95, int(idx / max(len(files), 1) * 90)))
            try:
                self._download_file(downloads[0], self._safe_join(runtime_dir, path))
            except Exception as exc:
                self.missing_files.append({"path": path, "downloads": downloads, "reason": str(exc)})

        missing_report = self._write_missing_report(version_id)

        return {
            "version": version_id,
            "alias": pack_name,
            "minecraft": minecraft_version,
            "loader": loader_key,
            "message": f"Modrinth 整合包已导入：{pack_name}，覆写 {copied} 个文件，缺失 {len(self.missing_files)} 项",
            "missing_report": missing_report,
        }

    def _install_curseforge(self, archive, manifest_entry):
        manifest = self._read_json(archive, manifest_entry)
        minecraft_version = manifest.get("minecraft", {}).get("version", "")
        pack_name = (manifest.get("name") or os.path.splitext(os.path.basename(self.pack_path))[0]).strip()
        version_id = sanitize_filename(pack_name, f"CurseForge-{minecraft_version}")
        runtime_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(runtime_dir, exist_ok=True)

        mod_loaders = manifest.get("minecraft", {}).get("modLoaders", [])
        primary_loader = next((item.get("id", "") for item in mod_loaders if item.get("primary")), "")
        if not primary_loader and mod_loaders:
            primary_loader = mod_loaders[0].get("id", "")
        loader_key = primary_loader.split("-", 1)[0] if primary_loader else ""

        self.status.emit("正在准备基础版本和加载器...")
        self._ensure_minecraft_version(minecraft_version)
        installed_base_version = self._install_declared_loader(minecraft_version, loader_key)
        self._write_imported_version(version_id, minecraft_version, installed_base_version)

        override_dir = (manifest.get("overrides") or "overrides").strip("/\\")
        base = manifest_entry.replace("\\", "/").rsplit("/", 1)[0]
        base_prefix = f"{base}/" if base else ""
        copied = self._extract_prefix(archive, base_prefix + override_dir, runtime_dir)
        files = manifest.get("files", [])
        downloaded, optional = self._download_curseforge_files(files, runtime_dir)
        missing_report = self._write_missing_report(version_id)
        return {
            "version": version_id,
            "alias": pack_name,
            "minecraft": minecraft_version,
            "loader": loader_key,
            "message": f"CurseForge 整合包已导入：覆写 {copied} 个，下载 {downloaded} 个，可选 {optional} 个，缺失 {len(self.missing_files)} 项。",
            "missing_report": missing_report,
        }

    def _install_plain_zip(self, archive):
        pack_name = os.path.splitext(os.path.basename(self.pack_path))[0].strip()
        version_id = sanitize_filename(pack_name, "ImportedPack")
        runtime_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(runtime_dir, exist_ok=True)
        copied = self._extract_prefix(archive, "", runtime_dir)
        self._write_missing_report(version_id)
        return {
            "version": version_id,
            "alias": pack_name,
            "minecraft": "",
            "loader": "",
            "message": f"压缩包已导入：{copied} 个文件",
        }

    def run(self):
        try:
            if not zipfile.is_zipfile(self.pack_path):
                raise RuntimeError("当前仅支持 zip/mrpack 格式整合包。")
            self.progress.emit(3)
            self.status.emit("正在识别整合包格式...")
            with zipfile.ZipFile(self.pack_path, "r") as archive:
                modrinth_entry = self._find_entry(archive, ["modrinth.index.json"])
                manifest_entry = self._find_entry(archive, ["manifest.json"])
                mmc_entry = self._find_entry(archive, ["mmc-pack.json", "instance.cfg"])
                hmcl_entry = self._find_entry(archive, ["modpack.json", "hmcl.json"])
                mcbbs_entry = self._find_entry(archive, ["mcbbs.packmeta", "mcbbs-pack.json", "pack.json"])
                if modrinth_entry:
                    payload = self._install_modrinth(archive, modrinth_entry)
                elif manifest_entry:
                    payload = self._install_curseforge(archive, manifest_entry)
                elif mmc_entry:
                    payload = self._install_generic_manifest_pack(archive, mmc_entry, "MMC")
                elif hmcl_entry:
                    payload = self._install_generic_manifest_pack(archive, hmcl_entry, "HMCL")
                elif mcbbs_entry:
                    payload = self._install_generic_manifest_pack(archive, mcbbs_entry, "MCBBS")
                else:
                    payload = self._install_plain_zip(archive)
            self.progress.emit(100)
            self.finished.emit(payload)
        except Exception as exc:
            self.failed.emit(str(exc))
