# limits

Windows tray app that reads local Codex session files from `%CODEX_HOME%\\sessions` or `%USERPROFILE%\\.codex\\sessions` and shows remaining allowance as digits:

- Codex icon: centered remaining weekly percent

It also adds tray icons for Claude and Kimi usage:

- Codex icon: small OpenAI-green triangle in the bottom-right corner
- Claude icon: small Claude-orange triangle in the bottom-right corner
- Claude icon digits: remaining 5-hour percent on top and remaining weekly percent on bottom
- Claude popup menu: exact reset times and usage source
- Kimi icon: small Kimi-blue triangle in the bottom-right corner
- Kimi icon digits: remaining 5-hour percent on top and remaining 7-day percent on bottom
- Kimi popup menu: used and remaining percent, reset times, last-24h local token count, and usage source

Claude status is read from Claude Code's OAuth usage metadata endpoint. It does
not invoke `claude -p /usage`. Kimi quota status is read from the Kimi Code
usage endpoint with the local Kimi OAuth token or `KIMI_API_KEY`. Kimi local
`%KIMI_HOME%\\sessions` or `%USERPROFILE%\\.kimi-code\\sessions`
`usage.record` events are used only for the token-count line in the popup. It
does not invoke `kimi -p`. The app does not write a usage state file.

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
