# V1 performance baseline

This is an external observation tool, not an autonomous browsing agent. It leaves
DevBrowser's application code unchanged. You perform the interactions while it
records application and descendant-process CPU/private memory, a window message
responsiveness probe, and the number of local requests served.

## Run

1. Launch the installed V1 DevBrowser. Do not use an old Debug executable left over
   from the reverted changes. Alternatively rebuild V1 before launching it.
2. In PowerShell, find the intended process:

   ```powershell
   Get-Process DevBrowser | Select-Object Id, Path
   ```

3. From the repository root, substitute its actual PID for `1234`:

   ```powershell
   dotnet run --project tests/DeveloperBrowser.PerformanceProbe -- 1234 1200
   ```

   Requires Windows and the .NET 9 SDK. The probe listens only on loopback. To use
   another port, append it after the duration, for example `1234 1200 8766`.

4. Open the three URLs printed by the probe in separate DevBrowser tabs.
5. Follow this repeatable 20-minute scenario:

   | Elapsed time | Actions |
   |---|---|
   | 0–2 minutes | Inspector closed: switch tabs, click the page's burst button, open and close five tabs, and use the sidebar. |
   | 2–5 minutes | Open the network inspector. Repeat the same interactions. |
   | 5–15 minutes | Leave the app untouched, with the inspector open and local pages polling. Do not put the PC to sleep. |
   | 15–20 minutes | Resume tab switching, sidebar use, bursts, and new-tab opening. Record delays and fan noise with their elapsed times. |

6. The probe stops automatically. Ctrl+C saves an early report. Results are stored
   under `artifacts/performance/<timestamp>-<pid>/`: `run.json`, `samples.csv`, and
   `report.md`. Add your action/issue observations to the report before sharing it.

For the next comparison, repeat with the inspector closed for the entire idle phase.
Use the same number of tabs and workload. Test sleep/wake separately and note the
sleep times. You can also monitor real websites; the collector doesn't require the
local test pages. Close test-page tabs afterward because their server stops with the probe.

## What the evidence means

- App CPU versus child CPU helps distinguish host work from browser-engine work.
  Children are identified by process ancestry, not just an executable name.
- CPU percentages are normalized across all logical processors. Private memory is
  not total system memory or GPU memory. Exited processes can be missed between samples.
- `timeout-or-error` means a bounded window-message probe failed. It is not proof
  of a particular root cause. `responding` does not prove menu clicks, new tabs,
  page rendering, or the WPF dispatcher are responding normally.
- Phase labels are schedule hints, not observations of what you actually did.
- Browser throttling can slow background polling. The synthetic page is a controlled
  starting point; it does not reproduce every real website or GPU workload.
- The probe does not gather stack traces, dumps, GPU metrics, screenshots, or page
  contents, and it does not fix detected issues. Its loopback traffic and monitoring
  have their own small cost. Keep the same probe running in before/after comparisons.

This establishes the baseline and reporting format. Automated desktop interaction
and hang-stack collection are subsequent work once the reproduction is established.
