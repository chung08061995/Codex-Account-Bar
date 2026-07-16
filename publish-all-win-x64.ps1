$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot 'artifacts\win-x64'
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item $root -ItemType Directory | Out-Null

dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:DebugType=None `
  -o (Join-Path $root 'slim')
Copy-Item (Join-Path $root 'slim\CodexAccountBar.exe') (Join-Path $root 'Codex-Bar.exe')

Write-Host "Published release files to $root"
