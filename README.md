# limits

Windows background monitor for Codex, Claude, Kimi, DeepSeek, Devin, OpenCode,
OpenRouter, and hardware health. The CPU/GPU load-bars icon is always present in
the notification area so its context menu is always available.

- CPU/GPU load icon: always displayed and refreshed every 10 seconds. It shows
  four horizontal bars for CPU load, GPU load, RAM used, and VRAM used; the
  rows use distinct colors and the tooltip/menu contain exact percentages and
  memory amounts. Right-click it (or any other visible icon) and use `Visible
  icons` to show or hide the other icons.
- OpenCode icon: shows rolling 5-hour remaining percent above weekly remaining
  percent; the left dots show days until the weekly reset. It is enabled by
  default and can be hidden from `Visible icons`. Its monthly quota is available
  in the tooltip and WebSocket feed.
- Devin icon: shows daily agentic quota remaining above weekly agentic quota
  remaining, with reset-day dots. It reads the local Devin CLI credentials from
  `%APPDATA%\Devin\credentials.toml`; the credential is never shown or sent in
  the WebSocket payload. It can be hidden from `Visible icons`.
- OpenRouter icon: shows the whole-dollar balance that can still be spent with
  the configured key (the lower of the account credit balance and the key's own
  spending limit), or an infinity sign above $100. It is enabled by default and
  can be hidden from `Visible icons`.
- Codex, Claude, Kimi, DeepSeek, and CPU/GPU temperature icons are hidden by
  default and can be enabled independently from `Visible icons`.
- Disk icon: shown only while a selected drive is at or below its configured
  free-space limit, when the disk icon is enabled. It displays the red drive
  letters and sounds a warning on startup/recheck.
- Hover over any tray icon for a quick details card. Provider cards show the
  available quota windows in a table with used/remaining percentages, time until
  reset, and the reset time in local time. Monthly rows appear only when that
  provider reports a monthly window or monthly spend; Devin also shows its plan
  period end when the account status response includes it. The card opens after
  a short hover delay and does not take focus.
- The CPU/GPU/RAM usage details card lists the five largest resident-RAM
  processes, with process name, PID, and working-set size. The list refreshes
  every 10 seconds with the hardware readings.

Icon visibility is stored in `%LOCALAPPDATA%\limits\icon-settings.json`. The
CPU/GPU load icon is always on; the other nine icons have independent
show/hide settings.

Codex, Claude, and Kimi data each include up to seven vertical dots along the
left edge when rendered by a client page.
The dots represent days until the weekly reset; one disappears as each day
expires. The middle dot has two blank pixels above and below it, splitting the
strip into countable groups. Exact percentages, balances, reset times, and
source paths are available in the WebSocket counter document.

Codex status is read from the authenticated current-account usage endpoint
using `%CODEX_HOME%\auth.json` or `%USERPROFILE%\.codex\auth.json`. The regular
and Spark limits are read separately so concurrent sessions cannot replace the
regular allowance with Spark's allowance. Local session files are used only as
a fallback when the account endpoint is unavailable.

Claude status is read from Claude Code's OAuth usage metadata endpoint. An
expired access token is refreshed with the stored refresh token, and the last
valid usage remains visible during transient refresh failures. The app does not
invoke `claude -p /usage`.

Kimi quota status is read from the Kimi Code usage endpoint with the local Kimi
OAuth token or `KIMI_API_KEY`. Kimi local
`%KIMI_HOME%\sessions` or `%USERPROFILE%\.kimi-code\sessions`
`usage.record` events are used only for the token-count line in the popup. It
does not invoke `kimi -p`. The app does not write a usage state file.

DeepSeek balance is read from DeepSeek's
[`/user/balance`](https://api-docs.deepseek.com/api/get-user-balance/) endpoint
using the API key and base URL configured by DeepCode in
`%USERPROFILE%\.deepcode\settings.json`. `DEEPCODE_API_KEY` and
`DEEPCODE_BASE_URL` override that file, matching DeepCode's environment
precedence. The key is used only as a bearer credential and is never displayed
or written by `limits`.

OpenCode usage is read locally from its database at
`%USERPROFILE%\.local\share\opencode\opencode.db` through the installed
`opencode` database command. The counter feed includes the rolling last-24-hour
session and token totals. When `%USERPROFILE%\.local\share\opencode\auth.json`
contains an `opencode-go` API key, the app also reads OpenCode Go's official
usage endpoint (`GET https://opencode.ai/zen/go/v1/usage`). Go provides a
rolling 5-hour quota, a weekly quota, and a monthly quota; it does not provide
a daily quota. These are exposed under
`openCode.go.rolling`, `openCode.go.weekly`, and `openCode.go.monthly`, each with
`status`, `usedPercent`, `remainingPercent`, `limitUsd`, and `resetAt`. OpenCode
states that included monthly usage varies by model, so the monthly `limitUsd` is
null rather than assuming one fixed dollar cap. The API key is used only as a
bearer credential and is never included in the WebSocket payload.

OpenRouter balance is read from `GET https://openrouter.ai/api/v1/key` (key
limit and daily/weekly/monthly key spend) and
`GET https://openrouter.ai/api/v1/credits` (account credits and total usage)
using the `openrouter` API key in
`%USERPROFILE%\.local\share\opencode\auth.json`, falling back to
`OPENROUTER_API_KEY`. The key is used only as a bearer credential and is never
displayed or included in the WebSocket payload.

Hardware readings use Windows `GetSystemTimes` for CPU usage,
`GlobalMemoryStatusEx` for physical RAM, `nvidia-smi` for NVIDIA GPU
temperature, usage, and VRAM, and the installed AMD Ryzen Master package
sensor for the CPU package temperature. The generic Windows ACPI thermal-zone
counter is deliberately not used because it can report a board/ambient zone
rather than the Ryzen package temperature. If a sensor is unavailable, that
value is shown as unknown instead of being guessed. Hardware readings are
refreshed every 10 seconds; the usage and disk checks keep their longer refresh
interval.

The app also runs a localhost-only WebSocket server on port `31001`. Connect a
page to `ws://127.0.0.1:31001/ws` (or `ws://localhost:31001/ws`) to receive the
current counter document immediately and after each refresh. The root URL
`http://127.0.0.1:31001/` returns the WebSocket URL. Messages are JSON with
`type: "limits.counters"`, `version: 1`, an `updatedAt` timestamp, and nullable
`codex`, `claude`, `kimi`, `deepSeek`, `devin`, `openRouter`, `unet`,
`openCode`, `hardware`, and `disk` objects. The `devin` object includes
`planName`, nullable `planEndAt`, `daily`, and `weekly`; each window has `usedPercent`,
`remainingPercent`, and `resetAt`. The `openRouter` object includes
`spendableBalance`, `accountBalance`, `totalCredits`, `totalUsage`, `keyLimit`,
`keyLimitRemaining`, `keyLimitReset`, `usageDaily`, `usageWeekly`,
`usageMonthly`, and `isFreeTier`.
The `hardware` object includes `cpuUsagePercent`,
`gpuUsagePercent`, `ramUsagePercent`, `vramUsagePercent`, `cpuTemperatureC`,
`gpuTemperatureC`, `ramTotalBytes`, `ramAvailableBytes`, `ramUsedBytes`,
`vramTotalBytes`, `vramAvailableBytes`, and `vramUsedBytes`.

UNET balances are read from `https://my.unet.by/login` using the credentials in
`%LOCALAPPDATA%\limits\unet-credentials.json`. Create or replace that file with
`.\configure-unet.ps1`; it prompts for up to two accounts and stores only
Windows-DPAPI-encrypted passwords. The WebSocket `unet` object contains an
`accounts` array with `username`, `balance`, `currency`, `isAvailable`, and a
safe `error` field; passwords and session data are never included. Balances are
refreshed with the regular background refresh.

The disk icon's context menu opens disk settings while the warning is visible.
When all disks are healthy, open settings with
`C:\Programs\limits.exe --disk-settings`. Select any detected drives and set an
individual red limit in GB of free space. The settings are stored in
`%LOCALAPPDATA%\limits\settings.json`; the default selection is C: and D: at
5 GB. A selected drive at or below its limit is shown as a red drive letter on
the icon and sounds a Windows warning. The same drive is not sounded more than
once per minute. The limits watchdog uses the same selected drives and limits
when it runs the pause/resume batch files.

The app uses raw Win32 tray APIs and does not depend on the Windows Desktop framework.

Download the latest released executable to the current directory:

```powershell
gh release download --repo flcl42/limits --pattern limits.exe --clobber
```

Publish the native executable to the current directory:

```powershell
dotnet publish .\limits.csproj -c Release -r win-x64 -o . -p:PublishAot=true -p:SelfContained=true -p:InvariantGlobalization=true
```

The published binary is `.\limits.exe`.

Publish the native executable to `C:\Programs`:

```powershell
.\install.ps1
```

The installer publishes to `publish\limits`, asks an existing tray process to
shut down cleanly, copies only `limits.exe` into `C:\Programs`, removes old
installed `gpt.exe` binaries, registers the `limits` logon task at the highest
run level, and starts that task. The installed binary is
`C:\Programs\limits.exe`.
