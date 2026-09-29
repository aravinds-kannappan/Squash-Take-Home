#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateSet('Prepare','Install','Uninstall')][string]$Mode = 'Install',
    [string]$BootstrapFile,
    [switch]$Purge
)
$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:ProgramFiles 'SquashRmm'
$dataDir = Join-Path $env:ProgramData 'SquashRmm'
$exe = Join-Path $installDir 'Squash.Agent.exe'
function Check-Native { if ($LASTEXITCODE -ne 0) { throw "Native command failed with exit code $LASTEXITCODE" } }
if ($Mode -eq 'Uninstall') {
    if (Get-Service SquashRmm -ErrorAction SilentlyContinue) {
        Stop-Service SquashRmm -Force -ErrorAction SilentlyContinue
        & sc.exe delete SquashRmm | Out-Null; Check-Native
    }
    if (Test-Path $installDir) { Remove-Item -LiteralPath $installDir -Recurse -Force }
    if ($Purge) {
        if (Test-Path $dataDir) { Remove-Item -LiteralPath $dataDir -Recurse -Force }
        $provider = [System.Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
        $options = [System.Security.Cryptography.CngKeyOpenOptions]::MachineKey
        if ([System.Security.Cryptography.CngKey]::Exists('Squash.Rmm.Device', $provider, $options)) {
            $key = [System.Security.Cryptography.CngKey]::Open('Squash.Rmm.Device', $provider, $options)
            $key.Delete(); $key.Dispose()
        }
    }
    Write-Host 'Agent removed. Server audit records remain. Identity and local ledger remain unless -Purge was specified.'
    return
}
if (Get-Service SquashRmm -ErrorAction SilentlyContinue) { Stop-Service SquashRmm -Force }
New-Item -ItemType Directory -Force $installDir, $dataDir | Out-Null
& icacls.exe $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null; Check-Native
$source = Join-Path $PSScriptRoot 'agent'
if (!(Test-Path (Join-Path $source 'Squash.Agent.exe'))) { throw 'Missing agent package. Run scripts/package.ps1 first.' }
Copy-Item "$source\*" $installDir -Recurse -Force
if ($Mode -eq 'Prepare') { & $exe --prepare; Check-Native; return }
if ($BootstrapFile) {
    Copy-Item -LiteralPath $BootstrapFile -Destination (Join-Path $dataDir 'bootstrap.json') -Force
    & $exe --enroll; Check-Native
    Remove-Item -LiteralPath $BootstrapFile -Force
}
if (!(Test-Path (Join-Path $dataDir 'agent.json'))) { throw 'Enrollment required: prepare identity, issue a grant, then pass -BootstrapFile.' }
if (!(Get-Service SquashRmm -ErrorAction SilentlyContinue)) {
    & sc.exe create SquashRmm binPath= ('"' + $exe + '"') start= auto obj= LocalSystem DisplayName= 'Squash RMM Agent' | Out-Null; Check-Native
}
& sc.exe failure SquashRmm reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null; Check-Native
Start-Service SquashRmm
Write-Host 'Squash RMM installed and running.'
