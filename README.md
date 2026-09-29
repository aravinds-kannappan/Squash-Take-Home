# Squash AI-native RMM

A Windows service and an authenticated control plane for executing raw PowerShell,
returning structured results, and supporting fast sequential diagnostics. .NET 10,
SQLite, outbound WebSockets. No inbound endpoint port or logged-in user required.

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then:

```sh
dotnet test Squash.slnx
dotnet run --project src/ControlPlane
```

The server needs `Rmm__ApiKey` (32+ random characters). By default it stores its
SQLite audit database and signing key in `data/`; set `Rmm__DataDir` to override.
All interfaces require HTTPS. HTTP is permitted only for loopback clients when
`ASPNETCORE_ENVIRONMENT=Development`. Never use Development on a public server.

Tests exercise the real ASP.NET API and WebSocket protocol with a simulated
endpoint. Windows-only tests additionally execute PowerShell and verify timeouts;
these are explicitly skipped on macOS/Linux. GitHub Actions runs both Linux and
Windows. A successful protocol test is not a Windows performance measurement.

## First enrolled device (about 20 minutes after VM and SDK setup)

You need a Windows 10/11 machine reachable *outbound* to your HTTPS control plane.
The VM must trust the server certificate and the certificate must match its DNS
name. Do not bypass certificate validation. For a local demo you can run both
components on the same Windows VM with a local trusted certificate.

### 1. Start HTTPS control plane on Windows

In an elevated PowerShell terminal, from the source directory:

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$env:Rmm__ApiKey = [Convert]::ToBase64String($bytes)
# Save this in your password manager; do not record it in the demo.
$cert = New-SelfSignedCertificate -DnsName localhost -CertStoreLocation Cert:\LocalMachine\My
Export-Certificate -Cert $cert -FilePath "$env:TEMP\squash-local.cer" | Out-Null
Import-Certificate -FilePath "$env:TEMP\squash-local.cer" -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
$env:ASPNETCORE_URLS = 'https://localhost:8443'
$env:ASPNETCORE_Kestrel__Certificates__Default__Subject = 'localhost'
$env:ASPNETCORE_Kestrel__Certificates__Default__Store = 'My'
$env:ASPNETCORE_Kestrel__Certificates__Default__Location = 'LocalMachine'
$env:ASPNETCORE_Kestrel__Certificates__Default__AllowInvalid = 'true'
dotnet run --project src/ControlPlane
```

`AllowInvalid` permits Kestrel to select the local self-signed server certificate;
it does not disable client verification. Only trust this certificate in your demo
VM. For separate machines use your domain name and an appropriate trusted cert.

### 2. Package and prepare device identity

In another elevated PowerShell terminal:

```powershell
./scripts/package.ps1
Set-Location artifacts/squash-win-x64
$identity = ./install.ps1 -Mode Prepare | ConvertFrom-Json
```

Preparation generates a non-exportable machine key on the intended endpoint. The
public identity is safe to transfer to the operator. Capturing the later bootstrap
file cannot impersonate the endpoint because enrollment also proves possession
of this private key. The two-phase flow is intentional; a bearer token plus a
spoofable hardware identifier would not meet the enrollment requirement.

### 3. Issue grant, install unattended, enroll

Set `$env:RMM_API_KEY` to the value generated in step 1 using your secret manager.
Run grant issuance from the operator's terminal. Transfer only `bootstrap.json`
to the endpoint if the two are separate machines.

```powershell
$server = 'https://localhost:8443'
$headers = @{ Authorization = "Bearer $env:RMM_API_KEY" }
$grant = Invoke-RestMethod "$server/v1/enrollment-grants" -Method Post -Headers $headers -ContentType 'application/json' -Body ($identity | ConvertTo-Json)
@{serverUrl=$server; token=$grant.token} | ConvertTo-Json | Set-Content bootstrap.json -Encoding UTF8
./install.ps1 -Mode Install -BootstrapFile ./bootstrap.json
Invoke-RestMethod "$server/v1/devices" -Headers $headers
```

The installer deletes the bootstrap file after enrollment, sets restrictive data
directory ACLs, installs the service as LocalSystem, and enables automatic startup
and crash recovery. No operator API key is passed to the installed service.
An existing enrolled install can be upgraded with `./install.ps1 -Mode Install`.

### 4. Execute a script

```powershell
$device = (Invoke-RestMethod "$server/v1/devices" -Headers $headers)[0].id
$requestHeaders = @{Authorization="Bearer $env:RMM_API_KEY"; 'Idempotency-Key'=[guid]::NewGuid().ToString()}
$job = Invoke-RestMethod "$server/v1/devices/$device/executions" -Method Post -Headers $requestHeaders -ContentType 'application/json' -Body (@{script="Write-Output 'hello'"; timeoutSeconds=10; dispatchTimeoutSeconds=10} | ConvertTo-Json)
$job.id
Invoke-RestMethod "$server/v1/executions/$($job.id)" -Headers $headers | ConvertTo-Json -Depth 8
```

Dispatch returns `202` immediately. Poll the returned location until terminal.
Retried requests with the same idempotency key and identical fields return the same
job; changing any field returns `409`. Results contain `exitCode`, `stdout`,
`stderr`, `durationMs`, truncation flags, and the dispatched script hash.

Terminal states: `succeeded`, `failed`, `timed_out`, `offline`, `interrupted`,
`revoked`. `offline` means no dispatch before the deadline (including waiting
behind another job). `timed_out` may mean a missing result, not confirmed remote
termination; read `result.error`. Ambiguous execution is never automatically retried.

### 5. Demo client and AI driver

Python 3.10+; no pip packages required:

```powershell
$env:RMM_URL = 'https://localhost:8443'
# Python uses platform default CA trust; RMM_CA_FILE can point to a PEM CA bundle.
python tools/rmm.py devices
python tools/rmm.py run --device DEVICE_ID --script "Write-Output 'hello'"
python tools/rmm.py run --device DEVICE_ID --script 'Start-Sleep 30' --timeout 1
python tools/rmm.py diagnose --device DEVICE_ID
python tools/rmm.py benchmark --device DEVICE_ID --count 20
$env:OPENROUTER_API_KEY = 'set-via-secret-manager'
# Optional override; default is anthropic/claude-haiku-4.5:
$env:OPENROUTER_MODEL = 'anthropic/claude-haiku-4.5'
python tools/rmm.py ai --device DEVICE_ID --problem 'Why does this machine feel slow?'
```

The deterministic diagnosis inspects memory, chooses CPU or memory ranking based
on that result, then inspects the selected process. Each step prints elapsed time.
The AI driver uses OpenRouter tool calls, with six executions and ten turns maximum.
Direct Anthropic is also supported with `ANTHROPIC_API_KEY` and `ANTHROPIC_MODEL`
when `OPENROUTER_API_KEY` is absent.
Its read-only instruction is a prompt, **not a security sandbox**. Use a disposable
VM. Endpoint results sent to the LLM may contain sensitive device data; the calling
application owns provider/privacy policy and approvals.

### Uninstall

```powershell
./install.ps1 -Mode Uninstall
# Optional explicit deletion of local key/config/ledger:
./install.ps1 -Mode Uninstall -Purge
```

Default uninstall removes the service and program files but preserves machine
identity and execution ledger for reinstall. Purge removes those too. Revoke the
device via `POST /v1/devices/{id}/revoke` before decommissioning it. Re-enrollment
with a new operator grant preserves the device ID using the machine identifier;
cloned/reimaged Windows machines need explicit identity management.

## Docker

Place a valid TLS certificate in `certs/server.pfx`; set `RMM_API_KEY` and
`RMM_CERT_PASSWORD`, then `docker compose up --build`. The database is persisted in
the `rmm-data` volume. Expose 8443 to agents. The container terminates TLS itself;
there is no trust in unverified forwarded headers. Restrict volume access.

## API

Every operator route uses `Authorization: Bearer ...`. OpenAPI is available at
`GET /openapi/v1.json` with the same authentication.

| Route | Purpose |
|---|---|
| `GET /health` | Minimal liveness probe |
| `POST /v1/enrollment-grants` | Expiring grant bound to machine ID and public key |
| `POST /v1/agents/enroll` | Consume grant with proof of private key possession |
| `GET /v1/devices` | Device list and live reachability |
| `GET /v1/devices/{id}` | Device details |
| `POST /v1/devices/{id}/revoke` | Revoke identity, close channel, terminate outstanding records |
| `POST /v1/devices/{id}/executions` | Submit raw script with `Idempotency-Key` |
| `GET /v1/executions/{id}` | Status and bounded result |
| `GET /v1/executions?deviceId=...&limit=50` | Audit history, maximum 200 results |
| `WSS /v1/agent/connect` | Agent-only signed challenge authentication |

See [design notes](docs/architecture.md), [threat model](docs/threat-model.md),
[demo script](docs/demo-script.md), [build notes](docs/how-built.md), and the
[requirement-by-requirement audit](docs/requirements.md).

The [validation report](docs/validation.md) records actual Windows results and
distinguishes the passing core acceptance from the blocked live OpenRouter demo.

## Automated Windows acceptance and submission artifacts

GitHub Actions runs the full suite on Linux and Windows, then installs the actual
Windows service and exercises enrollment, Unicode output, duplicate submissions,
timeout, output truncation, offline handling, uninstall/reinstall, automatic crash
recovery, dependent diagnosis, latency measurement, and revocation. Download the
`windows-agent` artifact for the installer and JSON evidence. Windows CI is a
Windows Server environment; it is not a substitute for a final Windows 10/11 reboot.

Run it yourself from elevated PowerShell with `./scripts/windows-e2e.ps1`. It uses
a temporary trusted localhost certificate and cleans up its own service and data.
Use a disposable test machine: the script installs and removes the `SquashRmm`
service. For a live AI run, set the `OPENROUTER_API_KEY` Actions secret (optionally
`OPENROUTER_MODEL` as a repository variable), then dispatch the workflow again.

```sh
python scripts/package-source.py
# Optional video rendering dependencies (not needed to run the RMM):
python -m pip install Pillow imageio-ffmpeg
python tools/render_demo.py artifacts/windows-e2e/demo-events.json artifacts/demo.mp4
```

The MP4 is labeled as a replay of recorded CI evidence; missing live-AI evidence
is shown as pending rather than represented as a completed demo.
