# Testing and Release Checks

Run the same checks as CI before sharing a build:

```bash
python -m py_compile main.py launcher.py downloader.py app_workers.py auth.py auth_server.py external_auth.py file_utils.py install_services.py installer_engine.py java_runtime.py java_utils.py version_utils.py log_utils.py http_client.py secure_store.py storage_utils.py modpack_utils.py nat_utils.py process_utils.py resource_market.py resource_workers.py ui_base.py p2p_tunnel.py p2p_server.py
pytest
```

Build a Windows desktop package with:

```powershell
.\scripts\build_pyinstaller.ps1
```

Build a Windows one-file desktop package with Nuitka:

```powershell
.\scripts\build_nuitka_main.ps1
```

Do not include `accounts.json`, `launcher_config.ini`, `.minecraft/`, `logs/`, or `version_settings.json` in release review artifacts unless the change explicitly targets those formats.
