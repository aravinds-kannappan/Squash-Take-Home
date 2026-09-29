# Observed validation results

Code revision: `3ead0fa` (subsequent documentation changes do not change the code).

[Windows/Linux CI and downloadable evidence](https://github.com/aravinds-kannappan/Squash-Take-Home/actions/runs/36508348442)

## Automated tests

* Windows: **22 .NET tests passed, zero skipped**, plus **3 Python driver tests**.
* Linux: **20 .NET tests passed, 2 Windows-only tests skipped**, plus **3 Python tests**.
* Local macOS: same .NET/Python counts as Linux.
* Updated Docker image builds successfully. Package audit reports no known
  vulnerable direct or transitive dependencies in the resolved packages.

## Actual installed Windows service

The Windows acceptance script published a self-contained agent, generated and
trusted a temporary localhost TLS certificate, installed the service as LocalSystem
with automatic startup, and enrolled it. It verified:

* Device online through its outbound authenticated channel.
* Unicode stdout, stderr, exit code and structured result.
* Duplicate idempotency-key submissions return the same execution and produce
  exactly one observed file append.
* Hung script timeout, 64 KiB output truncation on both streams, and offline expiry.
* Uninstall/reinstall preserves the device ID.
* Killing the service process triggers automatic SCM recovery.
* Three dependent diagnostic commands execute correctly.
* Device revocation persists and closes the live connection.

Measured times on this hosted Windows Server runner with the control plane on the
same VM (these are not WAN latency measurements):

| Measurement | Result |
|---|---:|
| 20-command median | 538 ms |
| 20-command p95 | 735 ms |
| Slowest benchmark command | 1,043 ms |
| Dependent memory diagnostic | 1,514 ms |
| Dependent process selection | 646 ms |
| Dependent process inspection | 536 ms |
| Acceptance script to final revocation | About 54 seconds |

The generated `summary.json` reports `core_passed_ai_blocked`. The workflow is
intentionally **not green**: its live OpenRouter request returned **HTTP 401**.
The supplied key was independently rejected by OpenRouter's `/api/v1/key` endpoint.
No successful live AI investigation is claimed. The integration code is exercised
with offline provider fixtures, and the real invocation is wired into Windows CI.

## Remaining before a complete submission

1. Replace `OPENROUTER_API_KEY` in repository Actions secrets with a valid key and
   dispatch `build-and-test` again. The live AI step must complete successfully.
2. Rehearse on an actual Windows 10/11 VM and reboot it. The hosted runner uses
   Windows Server; automatic service startup and crash recovery were tested, but
   a whole-machine reboot was not performed.
3. The generated MP4 is an explicitly labeled replay of real CI commands/results.
   It includes the failed-provider segment. Replace or supplement it with the
   completed live AI segment before sending the final submission.

No secrets are included in the source zip, installer zip or recorded evidence.
The source archive retains Git history but replaces local Git auth configuration.
