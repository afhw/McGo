$ErrorActionPreference = "Stop"

$ProjectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$VenvPython = Join-Path $ProjectRoot ".venv\Scripts\python.exe"
$Python = if ($env:MCGO_PYTHON) {
  $env:MCGO_PYTHON
} elseif (Test-Path $VenvPython) {
  $VenvPython
} else {
  "python"
}

& $Python -m pip install -r requirements.txt nuitka ordered-set zstandard
& $Python -m nuitka `
  --standalone `
  --onefile `
  --windows-console-mode=disable `
  --enable-plugin=pyside6 `
  --assume-yes-for-downloads `
  --include-data-dir=assets=assets `
  --include-package-data=qfluentwidgets `
  --windows-icon-from-ico=assets\mcgo.ico `
  --output-dir=dist `
  --output-filename=McGo.exe `
  main.py
