# Demo walkthrough (5–8 minutes)

Prerequisites: Windows VM, trusted HTTPS certificate, running control plane, agent
package, operator API key. Record no credentials or bootstrap token.

1. Show `install.ps1 -Mode Prepare` returning public identity. Issue a grant off
   camera. Run unattended install with bootstrap file; show the running service
   and the device's `online: true` API response. Reboot once during rehearsal to
   verify the service starts without an interactive login.
2. POST a PowerShell script with a fresh Idempotency-Key. Show the immediate job
   ID, then its structured exit code/stdout/stderr/duration/hash. Resubmit the same
   request and show the same job ID. Change the script with the same key: show 409.
3. Run `Start-Sleep 30` with a one-second timeout. Show `timed_out`. Stop the
   service, submit a job with a short dispatch deadline, and show `offline`.
   Restart the service and show reachability recover.
4. Run `python tools/rmm.py diagnose --device ID`. Explain how available memory
   determines the next command, and its top process ID determines the third.
   Show each measured round-trip time. Run the 20-command benchmark and report
   the actual median/p95, including any misses of the two-second target.
5. Set OpenRouter credentials off camera. Run the AI command with a plain-English
   problem. Show generated API investigations and the final evidence-based report.
6. Revoke the device. Show it offline and subsequent script submission rejected.

Before submission: run Windows CI/tests, replay this flow from a fresh install,
capture the real Windows timings, and record the video. No Windows VM or live LLM
credential was available during the initial macOS build. Subsequent GitHub Windows
CI exercises the actual service and uses the supplied OpenRouter secret. Do not
present a simulated protocol test as these real-world demo steps; use CI evidence.

Deliver a zip including `.git` history and source (exclude secrets, keys, databases,
`bin`, `obj` and local configuration), the Windows package, demo video, README,
design notes, threat model and AI-use note. Send to the address in the project brief.
