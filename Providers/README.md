# Providers

`master` stays fully unaware of personal integrations. Any provider that is
not of common interest lives on the `flcl` branch as one or more files in
this directory, wired in with a single call site each in `Program.cs`.

## Contract

`Program.cs` declares these `partial` extension points on `TrayApplication`
(no-ops on `master` — unimplemented partial calls evaporate at compile time):

- `partial void RefreshExtraFeeds();`
  Called at the end of `RefreshUsage()`. Fetch off-thread, then
  `PostMessage(ExtraFeedResultMessage)` with the result queued.
- `partial void ApplyExtraFeedResults();`
  Handles `ExtraFeedResultMessage` in the window procedure (a
  `PublishCounterState()` follows automatically). Drain whatever was queued.
- `partial void PublishExtraCounters(JsonObject root);`
  Called at the end of `BuildCounterJson()`. Add this provider's keys.
- `partial void NotifyClaudeSnapshotChanged(ClaudeUsageSnapshot? snapshot);`
  Called in `ApplyClaudeResult()` for fresh, stale, and missing snapshots.
- `partial void NotifyDiskSnapshotChanged();`
  Called at the end of `ApplyDiskResult()`.

## Rules for `flcl`

- Put each personal provider in its own file here
  (`Unet.cs`, `Rates.cs`, `Gcal.cs`, `Watchdog.cs`, ...).
- Extend `TrayApplication` via `internal sealed partial class TrayApplication`
  in the `Limits` namespace — all private members of `Program.cs` are visible.
- Reader/record classes can be copied verbatim; only the `TrayApplication`
  integration goes through the partials above.
- `ExtraFeedResultMessage` (`WM_APP + 15`) is the single shared result message;
  multiplex providers through a queue, don't add per-provider messages.
- Never merge `flcl` into `master`. Merge `master` into `flcl` freely:
  `Program.cs` stays identical on both branches, personal files never collide.
