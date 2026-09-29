#Requires -RunAsAdministrator
[CmdletBinding()]
param([int]$Port = 18443)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$evidence = Join-Path $artifacts 'windows-e2e'
$private = Join-Path $env:TEMP ('squash-e2e-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $evidence, $private | Out-Null
$events = [System.Collections.Generic.List[object]]::new()
$start = [Diagnostics.Stopwatch]::StartNew()
$serverProcess = $null
$cert = $null
$installer = Join-Path $artifacts 'squash-win-x64/install.ps1'
$url = "https://localhost:$Port"
function Record([string]$Title, [string]$Command, $Result) {
    $event = [ordered]@{ elapsedMs = $start.ElapsedMilliseconds; title = $Title; command = $Command; result = $Result }
    $events.Add($event)
    $event | ConvertTo-Json -Depth 12 -Compress | Write-Host
    $events | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $evidence 'demo-events.json') -Encoding utf8
}
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Api([string]$Path, [string]$Method = 'Get', $Body = $null, [string]$IdempotencyKey = '') {
    $h = @{ Authorization = "Bearer $env:RMM_API_KEY" }
    if ($IdempotencyKey) { $h['Idempotency-Key'] = $IdempotencyKey }
    $p = @{Uri="$url$Path"; Method=$Method; Headers=$h; TimeoutSec=20}
    if ($null -ne $Body) { $p.ContentType='application/json'; $p.Body=($Body | ConvertTo-Json -Depth 10 -Compress) }
    Invoke-RestMethod @p
}
function Wait-Online([string]$DeviceId, [bool]$Expected = $true) {
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    do {
        $d = Api "/v1/devices/$DeviceId"
        if ($d.online -eq $Expected) { return $d }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Device online state did not become $Expected"
}
function Run-Script([string]$DeviceId, [string]$Script, [int]$Timeout = 10, [int]$DispatchTimeout = 10, [string]$Key = '') {
    if (!$Key) { $Key = [guid]::NewGuid().ToString('N') }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $job = Api "/v1/devices/$DeviceId/executions" Post @{script=$Script;timeoutSeconds=$Timeout;dispatchTimeoutSeconds=$DispatchTimeout} $Key
    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout + $DispatchTimeout + 10)
    while ($job.status -notin @('succeeded','failed','timed_out','offline','interrupted','revoked')) {
        Assert ([DateTime]::UtcNow -lt $deadline) 'Job did not reach terminal state'
        Start-Sleep -Milliseconds 50
        $job = Api "/v1/executions/$($job.id)"
    }
    [pscustomobject]@{job=$job;roundTripMs=$clock.ElapsedMilliseconds}
}
try {
    & (Join-Path $PSScriptRoot 'package.ps1')
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $env:RMM_API_KEY = [Convert]::ToBase64String($bytes)
    $env:Rmm__ApiKey = $env:RMM_API_KEY
    $env:Rmm__DataDir = Join-Path $private 'server-data'
    $env:RMM_URL = $url
    if ($env:GITHUB_ACTIONS) { Write-Host "::add-mask::$env:RMM_API_KEY" }
    $cert = New-SelfSignedCertificate -DnsName localhost -CertStoreLocation Cert:\LocalMachine\My
    $certFile = Join-Path $private 'server.cer'
    Export-Certificate -Cert $cert -FilePath $certFile | Out-Null
    Import-Certificate -FilePath $certFile -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    $env:ASPNETCORE_URLS = $url
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $certPassword = [guid]::NewGuid().ToString('N')
    Export-PfxCertificate -Cert $cert -FilePath "$private/server.pfx" -Password (ConvertTo-SecureString $certPassword -AsPlainText -Force) | Out-Null
    $env:ASPNETCORE_Kestrel__Certificates__Default__Path = "$private/server.pfx"
    $env:ASPNETCORE_Kestrel__Certificates__Default__Password = $certPassword
    if ($env:GITHUB_ACTIONS) { Write-Host "::add-mask::$certPassword" }
    dotnet publish "$root/src/ControlPlane/ControlPlane.csproj" -c Release -o "$private/server" | Out-Host
    Assert ($LASTEXITCODE -eq 0) 'Control plane publish failed'
    $serverProcess = Start-Process dotnet -ArgumentList @((Join-Path $private 'server/ControlPlane.dll')) -PassThru -RedirectStandardOutput "$private/server.stdout" -RedirectStandardError "$private/server.stderr"
    $ready = $false
    for ($i=0; $i -lt 15; $i++) {
        if ($serverProcess.HasExited) { break }
        try { $null = Invoke-RestMethod "$url/health" -TimeoutSec 2; $ready = $true; break } catch { Start-Sleep -Milliseconds 200 }
    }
    if (!$ready) { Get-Content "$private/server.stdout", "$private/server.stderr"; throw 'HTTPS control plane did not start' }
    $identity = & $installer -Mode Prepare | ConvertFrom-Json
    $grant = Api '/v1/enrollment-grants' Post $identity
    $bootstrap = Join-Path $private 'bootstrap.json'
    @{serverUrl=$url; token=$grant.token} | ConvertTo-Json | Set-Content $bootstrap -Encoding utf8
    & $installer -Mode Install -BootstrapFile $bootstrap
    $devices = @(Api '/v1/devices')
    Assert ($devices.Count -eq 1) 'Expected one enrolled device'
    $id = $devices[0].id
    $online = Wait-Online $id
    Assert (!(Test-Path $bootstrap)) 'Bootstrap file was not removed'
    $service = Get-CimInstance Win32_Service -Filter "Name='SquashRmm'"
    Assert ($service.StartName -eq 'LocalSystem' -and $service.StartMode -eq 'Auto') 'Service account/startup configuration incorrect'
    Record 'Unattended installation and enrollment' 'install.ps1 -Mode Install -BootstrapFile bootstrap.json; GET /v1/devices' @{device=$online;serviceAccount=$service.StartName;startMode=$service.StartMode}

    $run = Run-Script $id "Write-Output 'hello café 世界'; [Console]::Error.WriteLine('diagnostic stderr'); exit 0"
    Assert ($run.job.status -eq 'succeeded' -and $run.job.result.stdout.Contains('hello café 世界')) 'Unicode execution failed'
    Assert ($run.job.result.stderr.Contains('diagnostic stderr')) 'Stderr was not captured'
    Record 'Raw PowerShell with structured results' 'POST /v1/devices/{id}/executions; GET /v1/executions/{job}' $run

    $script = '$p=Join-Path $env:ProgramData ''SquashRmm\once.txt''; Add-Content $p ''once''; (Get-Content $p).Count'
    $key = [guid]::NewGuid().ToString('N')
    $once = Run-Script $id $script 10 10 $key
    $twice = Run-Script $id $script 10 10 $key
    Assert ($once.job.id -eq $twice.job.id -and $twice.job.result.stdout.Trim() -eq '1') 'Duplicate request repeated execution'
    Record 'Duplicate requests execute once' 'Resubmit identical request with same Idempotency-Key' @{firstJob=$once.job.id;secondJob=$twice.job.id;sideEffectCount=$twice.job.result.stdout.Trim()}

    $run = Run-Script $id 'Start-Sleep -Seconds 30' 1
    Assert ($run.job.status -eq 'timed_out') 'Hung script was not timed out'
    Record 'Timeout terminates the script' 'Start-Sleep 30 (timeoutSeconds=1)' $run
    $run = Run-Script $id '[Console]::Out.Write((''x'' * 100000)); [Console]::Error.Write((''y'' * 100000))'
    Assert ($run.job.result.stdoutTruncated -and $run.job.result.stderrTruncated) 'Output was not bounded'
    Record 'Output limits' '100000 characters on each output stream' @{status=$run.job.status;stdoutBytes=[Text.Encoding]::UTF8.GetByteCount($run.job.result.stdout);stderrBytes=[Text.Encoding]::UTF8.GetByteCount($run.job.result.stderr);stdoutTruncated=$run.job.result.stdoutTruncated;stderrTruncated=$run.job.result.stderrTruncated}

    Stop-Service SquashRmm
    $null = Wait-Online $id $false
    $run = Run-Script $id 'Write-Output unreachable' 5 1
    Assert ($run.job.status -eq 'offline') 'Offline execution did not terminate'
    Record 'Offline device reaches a terminal state' 'Stop-Service SquashRmm; dispatch with 1-second deadline' $run
    Start-Service SquashRmm
    $null = Wait-Online $id

    & $installer -Mode Uninstall
    Assert ($null -eq (Get-Service SquashRmm -ErrorAction SilentlyContinue)) 'Service was not removed'
    & $installer -Mode Install
    $again = Wait-Online $id
    Assert ($again.id -eq $id) 'Reinstall changed stable identity'
    Record 'Reinstall preserves identity' 'Uninstall; install using retained machine identity' @{before=$id;after=$again.id;online=$again.online}

    $service = Get-CimInstance Win32_Service -Filter "Name='SquashRmm'"
    $oldPid = $service.ProcessId
    Stop-Process -Id $oldPid -Force
    $recovered = $false
    for ($i=0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        $service = Get-CimInstance Win32_Service -Filter "Name='SquashRmm'"
        if ($service.State -eq 'Running' -and $service.ProcessId -ne $oldPid) { $recovered=$true; break }
    }
    Assert $recovered 'Service did not automatically recover after crash'
    $null = Wait-Online $id
    Record 'Windows service crash recovery' 'Terminate service process; observe SCM automatic restart' @{oldPid=$oldPid;newPid=$service.ProcessId;state=$service.State}

    $first = Run-Script $id 'Get-CimInstance Win32_OperatingSystem | Select-Object TotalVisibleMemorySize,FreePhysicalMemory | ConvertTo-Json -Compress'
    Assert ($first.job.status -eq 'succeeded') 'First diagnostic failed'
    $memory = $first.job.result.stdout | ConvertFrom-Json
    $sort = if (($memory.FreePhysicalMemory / $memory.TotalVisibleMemorySize) -lt .25) {'WorkingSet64'} else {'CPU'}
    $second = Run-Script $id "Get-Process | Sort-Object $sort -Descending | Select-Object -First 1 Id,ProcessName,CPU,WorkingSet64 | ConvertTo-Json -Compress"
    Assert ($second.job.status -eq 'succeeded') 'Second diagnostic failed'
    $processInfo = $second.job.result.stdout | ConvertFrom-Json
    $processId = [int]$processInfo.Id
    $third = Run-Script $id "Get-Process -Id $processId | Select-Object Id,ProcessName,StartTime,Handles,CPU,WorkingSet64 | ConvertTo-Json -Compress"
    Assert ($third.job.status -eq 'succeeded') 'Third diagnostic failed'
    Record 'Three dependent diagnostic steps' 'Inspect memory -> choose CPU/memory ranking -> inspect selected process' @($first,$second,$third)
    $timings = [System.Collections.Generic.List[double]]::new()
    for ($i=0; $i -lt 20; $i++) {
        $sample = Run-Script $id "Write-Output $i"
        Assert ($sample.job.status -eq 'succeeded' -and $sample.job.result.stdout.Trim() -eq "$i") 'Benchmark returned incorrect output'
        $timings.Add($sample.roundTripMs)
    }
    $sorted = @($timings | Sort-Object)
    $stats = @{samples=20;medianMs=($sorted[9]+$sorted[10])/2;p95Ms=$sorted[18];allRoundTripsMs=@($timings);platform=[Environment]::OSVersion.VersionString}
    $stats | ConvertTo-Json -Depth 5 | Set-Content "$evidence/benchmark.json" -Encoding utf8
    Record 'Measured Windows execution latency' '20 sequential PowerShell executions through HTTPS API' $stats
    Assert ($stats.p95Ms -le 2000) "P95 latency exceeded 2 seconds: $($stats.p95Ms) ms"

    $hasLlm = [bool]($env:OPENROUTER_API_KEY -or ($env:ANTHROPIC_API_KEY -and $env:ANTHROPIC_MODEL))
    $aiPassed = $false
    if ($hasLlm) {
        python "$root/tools/rmm.py" ai --device $id --problem 'Why does this machine feel slow?' | Tee-Object "$evidence/ai-transcript.txt"
        $aiPassed = $LASTEXITCODE -eq 0
        if ($aiPassed) { Record 'Live AI investigation' 'python tools/rmm.py ai --device DEVICE --problem ...' (Get-Content "$evidence/ai-transcript.txt" -Raw) }
        else { Record 'Live AI provider rejected the request' 'python tools/rmm.py ai --device DEVICE --problem ...' @{completed=$false;error='See provider HTTP status in workflow logs; no AI result is claimed.'} }
    } else { Record 'Live AI demo pending credentials' 'OPENROUTER_API_KEY or Anthropic credentials required' @{executed=$false} }
    $null = Api "/v1/devices/$id/revoke" Post
    $revoked = Wait-Online $id $false
    Assert $revoked.revoked 'Revocation did not persist'
    Record 'Device revocation' 'POST /v1/devices/{id}/revoke' $revoked
    @{status=$(if ($hasLlm -and !$aiPassed) {'core_passed_ai_blocked'} else {'passed'});deviceId=$id;elapsedSeconds=$start.Elapsed.TotalSeconds;rebootTested=$false;liveAiTested=$aiPassed} | ConvertTo-Json | Set-Content "$evidence/summary.json" -Encoding utf8
    Assert (!$hasLlm -or $aiPassed) 'Core acceptance passed; live AI provider request failed. Check configured credentials.'
} catch {
    if (!(Test-Path "$evidence/summary.json")) { @{status='failed';error=$_.Exception.Message} | ConvertTo-Json | Set-Content "$evidence/summary.json" -Encoding utf8 }
    # Application logs contain only status; never print bootstrap or environment.
    if (Test-Path "$private/server.stderr") { Get-Content "$private/server.stderr" }
    Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-20)} -ErrorAction SilentlyContinue | Where-Object {$_.ProviderName -match 'Squash|\.NET Runtime'} | Select-Object -First 5 TimeCreated,Message | Format-List | Out-Host
    throw
} finally {
    if (Test-Path $installer) { & $installer -Mode Uninstall -Purge }
    if ($serverProcess -and !$serverProcess.HasExited) { Stop-Process -Id $serverProcess.Id -Force }
    if ($cert) {
        Remove-Item -LiteralPath "Cert:\LocalMachine\Root\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath "Cert:\LocalMachine\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
    }
    # Remove only the task-specific temporary directory created at the start.
    Remove-Item -LiteralPath $private -Recurse -Force -ErrorAction SilentlyContinue
}
