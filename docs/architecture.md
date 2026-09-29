# Design notes

## Connection model

The Windows service initiates TLS WebSockets to the control plane. This works
behind NAT and avoids command polling. A random challenge is signed by the
device's P-256 key. The server verifies it against its enrolled public key before
creating a dispatch session. A second authenticated connection replaces the first.
Five-second heartbeats update presence; 20 seconds of silence means offline and
25 seconds closes an idle connection. Reconnect uses jittered exponential backoff.

A 100 ms server dispatcher scans durable queued jobs and pushes to online devices.
Only one job per device is in flight. API clients poll job results at 50 ms in the
sample client. This yields a small transport budget while retaining a simple REST
contract. Fresh PowerShell processes avoid cross-job state and make timeouts easier.
Measure Windows startup time before choosing a persistent runspace: the benchmark
reports median/p95 rather than assuming the two-second goal is met.

## Lifecycle and reliability

Jobs progress queued → dispatched → running → terminal. The original script,
hash, timestamps, caller and idempotency key are retained in SQLite. API duplicate
requests are serialized and use a unique database key. The same key with different
parameters fails. SQLite WAL/FULL sync persists state before dispatch.

The agent persists execution intent before starting PowerShell. Its on-disk ledger
prevents reexecution. Completed results survive a network loss and are replayed
after reconnect. The server accepts completion once and only from the assigned
device, after checking the source hash and output bounds. An agent crash with an
incomplete ledger entry becomes interrupted; the script is never restarted.

Dispatch deadlines terminate jobs waiting for an offline/busy device. Execution
timeouts are enforced locally. A server watchdog terminates the record after
timeout plus five seconds even if the endpoint disappears. It explicitly reports
uncertain remote outcome. Server restart marks previously dispatched jobs
interrupted and preserves queued jobs. Revocation closes the connection and marks
outstanding jobs revoked; cancellation interrupts the agent's running process.
Neither this record nor a lost network connection proves a hostile endpoint stopped.

These are at-most-once *attempts*, not transactional exactly-once side effects.
A crash after recording intent but before launch can produce zero executions.
A crash after a script changes external state but before reporting can produce
unknown outcome. Retrying under a new ID is an explicit caller decision.

## Device identity and enrollment

Preparation creates a machine-level non-exportable CNG signing key. The operator
issues a short-lived grant bound to its public key and hashed Windows MachineGuid.
Enrollment proves key possession and atomically consumes the grant. Tokens are
stored only as hashes and cannot authenticate the permanent channel. The server
returns a stable ID and its signing public key over verified TLS. Re-enrollment is
an operator-authorized key rotation preserving the device ID. Default uninstall
preserves the key and ledger; explicit purge removes them.

MachineGuid is a lookup attribute, not authentication. Possession of the device
private key is the security boundary. The identity preparation step must take
place on the intended endpoint through the MSP's existing trusted tooling.

## Execution

The server signs the exact serialized job envelope (including target, ID, script,
hash and deadline). The agent verifies it and the script hash before execution.
Script files contain the exact characters plus a UTF-8 encoding BOM. PowerShell
arguments use argument-list APIs. A fixed encoded bootstrap sets UTF-8 output,
invokes the unchanged script file through a quoted path, and propagates exit codes.
A Windows
Job Object kills child processes when its handle closes; timeout also kills the
process tree. Each stream is drained continuously and retained up to 64 KiB.
No interpretation or automatic remediation occurs in the control plane or agent.

## Scope and next work

One process, one operator credential, one SQLite database. This is deliberately a
single-tenant take-home. The initial implementation scans jobs in memory through
SQLite; add indexed active-state columns/pagination and queue quotas before a
large fleet. Production work includes per-consumer authorization and RBAC, secret
broker integration, credential rotation, stronger isolation of the API process,
encrypted retention, signed installers, signed updates, and fleet-scale routing.
Add Windows installer/reboot stress tests and benchmark on the deployment network.
Device inventory would be the first extra-credit capability.
