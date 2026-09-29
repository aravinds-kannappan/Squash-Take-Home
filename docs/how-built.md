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

Validation is split honestly: API/WebSocket/store tests run on macOS; real
PowerShell/service/installer and reboot verification require Windows. The demo
driver's live LLM path requires user-supplied credentials. See the session handoff
for observed test counts and artifact paths. Record actual total time and Windows
validation results here before submitting.
