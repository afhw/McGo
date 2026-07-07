# Testing and Release Checks

Run the same checks as CI before sharing a build:

```bash
dotnet build McGo.slnx
dotnet run --project tests/McGo.Core.Tests/McGo.Core.Tests.csproj
```

Build a Windows desktop package with:

```powershell
.\scripts\build_release.ps1
```

Do not include `accounts.json`, `launcher_config.ini`, `.minecraft/`, `logs/`, or `version_settings.json` in release review artifacts unless the change explicitly targets those formats.
