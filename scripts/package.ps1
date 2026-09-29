[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = Join-Path $root "artifacts/squash-$Runtime"
New-Item -ItemType Directory -Force $package | Out-Null
dotnet publish "$root/src/WindowsAgent/WindowsAgent.csproj" -c Release -r $Runtime --self-contained true -o "$package/agent"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item "$root/installer/install.ps1" $package -Force
Compress-Archive -Path "$package/*" -DestinationPath "$root/artifacts/squash-$Runtime.zip" -Force
Write-Host "Package: $root/artifacts/squash-$Runtime.zip"
