# Take-home requirement audit

Source: https://squash.ai/interview-projects/ai-native-rmm

This table separates implementation from demonstration. Optional extra credit is
not necessary for the requested core. The product intentionally has no dashboard,
chat UI, approval workflow, patch management, antivirus, or remote desktop.

| Requirement | Implementation and evidence |
|---|---|
| Unattended Windows installation with enrollment | `installer/install.ps1` and `scripts/windows-e2e.ps1` exercise prepare → key-bound grant → install/enroll. |
| Service without a logged-in user; reboot persistence | Windows SCM LocalSystem service with automatic startup and crash recovery. CI tests the actual service and crash recovery. A real VM reboot remains a separate validation step. |
| Clean uninstall | E2E removes service/program files, reinstalls with retained identity, and purges test data during cleanup. |
| NAT-compatible contact | Outbound authenticated WSS; no inbound endpoint listener. |
| Deterministic endpoint, no AI on endpoint | Agent accepts only server-signed, target-bound execution envelopes. |
| Secure enrollment | Expiring, single-use grant bound to public key; proof of possession; atomically consumed. Captured bootstrap alone is insufficient. |
| Stable identity across reinstall | Persistent CNG key and server ID mapped by hashed MachineGuid. Operator-authorized re-enrollment can rotate keys. |
| Device listing and current reachability | Device routes plus acknowledged heartbeats and receive deadlines. |
| Raw PowerShell and nonblocking execution handle | `POST /v1/devices/{id}/executions` returns 202 with job ID; original source is retained. |
| No duplicate execution on retries | Unique API idempotency key, conflict rejection, and durable local execution intent ledger. Unknown outcomes are not retried. |
| Status and terminal failure handling | Persistent lifecycle; dispatch deadline, local timeout, remote-result deadline, restart interruption, revocation. |
| Structured output and bounds | Exit code, UTF-8 stdout/stderr, duration, SHA-256 and truncation flags. 64 KiB per stream. |
| Authenticated callers and agents | Operator bearer key; device-key challenge response; TLS required. |
| Complete execution audit | Exact accepted source, caller, timestamps, parameters, result retained in SQLite. |
| Approximately ≤2 seconds per online step | Windows E2E measures 20 real executions, records median/p95, fails if p95 exceeds 2000 ms. |
| Individually revocable devices | Revoke route closes channel and rejects subsequent authentication/dispatch. |
| Dispatched content matches execution | Signed envelope, validated hash, unchanged source file, execution only after containment. |
| Endpoint output is untrusted | Stored as data; never parsed as executable instructions by agent/server. Caller AI has an explicit untrusted-data boundary. |
| Secrets excluded from logs/output/errors | No credential logging; configured secrets and common formats redacted before persistence; obvious embedded credentials rejected from source. Unknown arbitrary secrets cannot be perfectly recognized; see threat model. |
| Hung endpoint cannot hang control plane | Independent watchdog and bounded network/handshake/runtime deadlines. |
| Minimal LLM driver | `tools/rmm.py ai` implements a bounded Claude tool loop. Offline protocol tests pass; live run requires configured API key and model. |
| Demo video | `tools/render_demo.py` generates an explicitly labeled replay of actual Windows CI evidence. A full live-AI segment requires credentials. |
| Source zip including `.git` | `python scripts/package-source.py`; preserves history and sanitizes Git credential configuration. |
| Setup under 30 minutes | README setup and unattended installer, assuming a Windows host, SDK and HTTPS certificate are available. E2E measures its own elapsed duration. |
| Design/threat-model/AI-use notes | `docs/architecture.md`, `docs/threat-model.md`, `docs/how-built.md`. |

## External validation still required

1. The actual reboot on a Windows 10/11 VM cannot be exercised on this Mac or by
   rebooting the managed GitHub runner mid-job. SCM auto-start is configured and
   service recovery is covered separately.
2. A live LLM run needs an API credential. Tests using canned provider responses
   verify integration behavior, not live model behavior.
3. GitHub's Windows runner is Windows Server, so Windows 10/11 installation should
   still be rehearsed before sending the take-home.

No email is sent automatically. Submission delivery remains with the user.
