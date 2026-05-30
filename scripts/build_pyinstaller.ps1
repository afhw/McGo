$ErrorActionPreference = "Stop"

python -m pip install -r requirements.txt pyinstaller
python -m PyInstaller `
  --noconfirm `
  --windowed `
  --name McGo `
  --add-data "assets;assets" `
  main.py
