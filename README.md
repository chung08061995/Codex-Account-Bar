# Codex Account Bar for Windows

Windows system-tray account switcher for Codex with an integrated 9Router on/off toggle.

## Features

- Imports `%CODEX_HOME%\auth.json` (default `%USERPROFILE%\.codex\auth.json`).
- Adds accounts through the official `codex login` browser flow in an isolated temporary home.
- Encrypts saved auth documents with Windows DPAPI (`CurrentUser`) in `%LOCALAPPDATA%\CodexAccountBar`.
- Atomically switches `auth.json` and restarts an open Codex desktop window.
- Shows session/weekly quota when the usage endpoint is available.
- Detects and toggles 9Router as a Windows service, Docker container named `9router`, or global npm CLI.

## Build on Windows 10/11

Install the .NET 8 SDK, then run:

```powershell
.\publish-win-x64.ps1
```

Output: `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\CodexAccountBar.exe`. It is a self-contained single executable; the .NET runtime and WPF native libraries are embedded.

## Security

DPAPI blobs can only be decrypted by the same Windows user on the same installation. Token contents are never logged. Process-mode 9Router shutdown only proceeds after its port owner command line is verified to contain `9router`. UAC appears only when a Windows service needs administrator rights.

Detection order is Windows service, Docker, port `20128` owner, then `9router.cmd` on `PATH`.
