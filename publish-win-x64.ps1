$ErrorActionPreference = 'Stop'
dotnet restore
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
Write-Host "Published: $PSScriptRoot\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\CodexAccountBar.exe"
