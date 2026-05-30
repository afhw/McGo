$ErrorActionPreference = "Stop"

python -m pip install -r requirements.txt nuitka ordered-set zstandard
python -m nuitka `
  --standalone `
  --onefile `
  --windows-console-mode=disable `
  --enable-plugin=pyqt6 `
  --assume-yes-for-downloads `
  --include-data-dir=assets=assets `
  --include-package-data=qfluentwidgets `
  --windows-icon-from-ico=assets\mcgo.ico `
  --output-dir=dist `
  --output-filename=McGo.exe `
  main.py
