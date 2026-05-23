# Repository Guidelines

## Project Structure & Module Organization
This repository is a small, flat Python desktop application for launching Minecraft. Core modules live at the repository root:

- `main.py`: PyQt6/QFluentWidgets entry point, UI, worker threads, and app orchestration.
- `launcher.py`: builds and starts the Java launch command.
- `downloader.py`: downloads versions, libraries, assets, and natives.
- `auth.py` and `auth_server.py`: Microsoft OAuth and callback handling.
- `java_utils.py`: Java discovery and version inspection.

Local runtime state is also stored at the root: `launcher_config.ini`, `accounts.json`, and `.minecraft/`. Treat these as user data, not source files.

## Build, Test, and Development Commands
Use a virtual environment and install dependencies before running the app:

```bash
python -m pip install -r requirements.txt
python main.py
python -m py_compile main.py launcher.py downloader.py auth.py auth_server.py java_utils.py
```

`python main.py` launches the desktop app. `py_compile` is the current lightweight validation step because no committed test suite or packaging script exists yet.

## Coding Style & Naming Conventions
Follow existing Python conventions:

- 4-space indentation, UTF-8 source files, and PEP 8 style.
- `snake_case` for functions, variables, and module-level helpers.
- `PascalCase` for Qt classes such as `DownloadWorker` and `LauncherWindow`.
- Keep UI orchestration in `main.py`; move reusable logic into focused modules instead of expanding the window class further.

Prefer small, targeted functions and preserve existing naming patterns when extending config keys or account fields.

## Testing Guidelines
There is no committed `tests/` directory yet. For now:

- Run `python -m py_compile ...` after changes.
- Manually verify the affected flow in the UI, especially download, login, and launch paths.
- If you add automated tests, use `pytest`, place them under `tests/`, and name files `test_*.py`.

## Commit & Pull Request Guidelines
This workspace does not include `.git` history, so no repository-specific commit convention can be verified. Use short, imperative commit messages such as `Fix account refresh handling` or `Add Java path validation`.

Pull requests should include:

- a brief summary of user-visible behavior changes,
- validation steps run locally,
- screenshots for UI changes,
- notes about config, auth, or download side effects.

## Security & Configuration Tips
Do not commit real account tokens or personal launcher data. Keep `accounts.json`, `launcher_config.ini`, `.minecraft/`, and log output out of review unless a change explicitly targets those formats.
