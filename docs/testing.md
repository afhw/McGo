# Testing and Release Checks

Run the same checks as CI before sharing a build:

```bash
python -m py_compile main.py launcher.py downloader.py auth.py auth_server.py java_utils.py version_utils.py log_utils.py http_client.py secure_store.py storage_utils.py
pytest
```

Build a Windows desktop package with:

```powershell
.\scripts\build_pyinstaller.ps1
```

Do not include `accounts.json`, `launcher_config.ini`, `.minecraft/`, `logs/`, or `version_settings.json` in release review artifacts unless the change explicitly targets those formats.
