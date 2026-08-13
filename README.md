# limits

Windows tray app that shows Codex, Claude, Kimi, and DeepSeek allowance in
compact notification-area icons.

- Codex icon: centered remaining weekly percent and OpenAI-green marker
- Claude icon: remaining 5-hour percent on top, weekly percent on bottom, and
  Claude-orange marker
- Kimi icon: remaining 5-hour percent on top, 7-day percent on bottom, and
  Kimi-blue marker
- DeepSeek icon: remaining USD balance with a trailing `$` and DeepSeek-blue
  marker

Codex, Claude, and Kimi each show up to seven separated dots along the bottom
edge. The dots represent days until the weekly reset; one disappears as each
day expires. Exact percentages, balances, reset times, source paths, and manual
refresh commands remain available from each icon's popup menu.

Codex status is read from local session files under `%CODEX_HOME%\sessions` or
`%USERPROFILE%\.codex\sessions`. Claude status is read from Claude Code's
OAuth usage metadata endpoint; the app does not invoke `claude -p /usage`.

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

The same `limits.exe` process also performs the old limits watchdog work: it
monitors C: and D: free space and runs the pause/resume batch files when Claude
or disk thresholds cross.

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

Publish the native executable to `D:\Programs`:

```powershell
.\install.ps1
```

The installer publishes to `publish\limits`, asks an existing tray process to
shut down cleanly, copies only `limits.exe` into `D:\Programs`, updates the
Startup shortcut, removes old installed `gpt.exe` binaries, and starts it. The
installed binary is `D:\Programs\limits.exe`.
