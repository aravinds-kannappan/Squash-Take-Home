# Threat model

## Assets and boundaries

Assets: administrative execution authority on enrolled Windows devices, operator
API credential, device private keys, server signing key, exact script audit records
and endpoint output. Boundaries: caller → HTTPS API, API → SQLite, server → outbound
agent WSS, agent → LocalSystem PowerShell, and optional diagnostic caller → LLM.

Trusted: the authorized API caller, control-plane host, machine administrators,
initial identity preparation channel and OS certificate store. An authorized
script has LocalSystem privileges; this is remote administration, not a sandbox.
Compromise of an endpoint administrator or API credential is outside the boundary
we can contain in this prototype. Output from endpoints is always untrusted.

## Defenses

* All operator APIs require a constant-time checked high-entropy bearer credential.
  Unauthenticated enrollment is constrained by a key-bound, single-use expiring
  grant and cryptographic proof. Enrollment and connection handshakes are limited
  to 60 per minute per source IP. WebSockets authenticate by signing a fresh random
  challenge before jobs are sent. HTTPS is mandatory outside loopback development.
* A stolen bootstrap grant cannot enroll an unrelated key or establish standing
  access. It is atomically consumed and deleted from the endpoint after use.
  Stable IDs use MachineGuid only as a lookup, not as proof of machine identity.
* Device revocation closes active channels and rejects subsequent authentication.
  Re-enrollment requires a fresh operator-issued grant and disconnects the old key.
* Signed job envelopes bind source, target device, execution ID and deadline. A
  source hash is retained and echoed. This proves dispatch integrity for a trusted
  agent; it does not attest execution on an already-compromised operating system.
* Persistent local intent records and immutable server terminal states prevent
  duplicate delivery from repeating execution. Uncertain crash outcomes are
  explicitly terminal and never silently retried.
* Request, script, frame, runtime and output limits bound individual operations.
  Continuous output draining avoids pipe deadlock. Job Objects/process-tree kills
  contain normal child processes when execution ends or the agent dies.
* No shell interpolation is used to launch PowerShell. Endpoint output stays in
  data fields; neither component evaluates it as a command or instruction.
* Agent data is ACL-restricted to SYSTEM and Administrators. Device keys are
  non-exportable in the Windows software KSP. Server signing files are mode 0600
  on Unix and need an ACL-restricted data directory on Windows.

## Secrets and remaining limitations

The server redacts its API key and configured `Rmm__RedactSecrets__0`, etc. from
results before persistence, and rejects scripts containing its API key. The agent
never receives the operator or LLM API key. Bootstrap values and private
keys are not logged; errors omit sensitive request/exception content. Installer
bootstrap files must be transferred and staged securely, and are deleted after use.
The server must preserve exact submitted scripts, so callers must not embed secrets
in source. Arbitrary scripts can deliberately print arbitrary device secrets:
universal secret detection cannot be guaranteed. Avoid secret-producing scripts;
production requires caller-defined sensitive fields, secret injection separate
from script content, redaction and retention policy. Audit access is privileged.

Deferred: multi-tenant isolation, per-consumer scopes, TPM-backed attestation,
hardware identity across reimages, immutable remote audit storage, encryption at
rest, code signing/update delivery, proxy integration, queue/storage quotas and
distributed/high-availability dispatch. Rate and storage abuse by a stolen operator
key remain possible. Endpoint results are not malware-scanned or semantically
trusted. A malicious administrator can tamper with the local ledger or use the
key through the OS; non-exportable does not mean unavailable to an administrator.

PowerShell starts immediately before assignment to its Job Object. This leaves a
small launch-to-assignment race; production containment should use suspended
creation and assign before resume. It is not a defense against a deliberately
malicious authorized SYSTEM script, which can schedule work through other services.

The sample AI driver requests read-only diagnostics and ignores instructions in
endpoint output, but a prompt is not an enforcement mechanism. The calling
application owns approvals and policy. A production caller must apply its own
tool authorization and injection defenses before dispatching generated code.
