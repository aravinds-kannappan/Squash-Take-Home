# How this was built

Built with Codex from the supplied take-home brief and an initially empty local
repository. The user requested a plan, then asked for immediate implementation.
Codex wrote the .NET control plane, Windows service, installer, tests, Python demo
driver and documentation. No subagents were used. Human work: initial direction
and confirmation that a Windows VM was not yet available.

The implementation was organized around the execution path, then durable lifecycle
and enrollment security, then packaging and demo support. Restore/build/test
feedback drove corrections. The first package restore surfaced vulnerable baseline
dependencies; package versions were updated rather than suppressing security audits.

Validation began with API/WebSocket/store tests on macOS, then moved to GitHub's
Windows runner for real PowerShell, the installer, the service, crash recovery and
latency. Windows CI surfaced SQLite connection-pool retention and short-lived
antivirus file locks; these were fixed without suppressing failures. Its live AI
step uses the user-provided OpenRouter credential stored as an Actions secret.
The secret is not part of source or submission archives.

The source archive includes sanitized Git history. A demo renderer builds an
explicitly labeled replay from recorded Windows CI commands and results. An actual
Windows 10/11 reboot still needs a suitable VM and is not claimed by these tests.
See CI evidence and the session handoff for measured results. Work spanned the
initial build session plus the subsequent audit/CI iteration; Git commit timestamps
provide the recorded implementation timeline. Rough elapsed time was about 50
minutes from the initial source files to the Windows acceptance evidence,
including CI waits and the pause between the initial build and follow-up audit.
