# UseNotch developer and agent build plan

Status: M00-M11 are complete. M12 is implemented and validated except for its disposable-environment install matrix. M12-M14 remain open.

Prepared: 2026-09-07.

Read [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) first. It defines product scope, architecture, security, UI, and acceptance expectations. This document turns that design into ordered, resumable work with verification and commit checkpoints. It is the authoritative implementation progress ledger.

## 1. Working agreement

### Scope and non-negotiable decisions

- Monitor only OpenAI GPT / ChatGPT / Codex and Anthropic Claude / Claude Code.
- Initially label OpenAI readings as Codex quota, not universal GPT or ChatGPT usage.
- Use C#, Avalonia UI 11, CommunityToolkit.Mvvm, isolated Win32 services, System.Text.Json, xUnit, WiX v5, and GitHub Actions.
- Start with the inspected .NET 8 baseline. Before production release, recheck runtime support. .NET 8 support ends on 2026-11-10; migrate to a supported LTS before that date or before a later release.
- Run the app non-elevated with `asInvoker`. Do not copy modshell-cs hardware-monitor privileges or dependencies.
- Keep the domain independent of Avalonia, Win32, HTTP, SQLite, and provider JSON formats.
- Borrow credentials read-only. Never own token refresh, sign another tool out, or persist borrowed OAuth tokens.
- No credentials in configuration, caches, logs, test fixtures, commits, or documentation. Do not copy global instruction files into the repository; they may contain unrelated secrets.
- Keep quota, activity, authentication, freshness, and reading fidelity distinct. Missing data is not zero.
- Route every quota request through one scheduler. Respect persistent rate-limit deadlines and connection/account generations.
- Validate no-activate and cross-process click-through behavior on Windows before live provider integration.
- Use no other providers, Electron, web frontend, browser-cookie scraping, or conversation-content monitoring.
- No em dashes, AI attribution, generated-by footers, or co-author trailers in any artifact or commit.
- Preserve required upstream license notices for adapted source.

### Git ownership

The user is handling Git setup. Do not initialize a repository, create a remote, choose a remote URL, or push on the user's behalf as part of this plan. Once the user has prepared Git, verify the repository root and work on the existing intended branch. Do not alter the nested reference repository in `.tmp/codenotch` or the separate `modshell-cs` repository.

If Git setup is not ready, finish independent planning or local work that is already authorized, record the missing checkpoint, and leave milestone completion unchecked. A successful local implementation without its required commit is not a completed milestone.

Local milestone commits are required during implementation. Remote pushes, tags, release publication, and installations outside a disposable test environment require applicable user authorization at execution time. An unchecked release checklist is not authorization to publish.

### How to read the checkboxes

- `[ ]` means unfinished or unverified. It does not mean failed.
- `[x]` means completed and backed by recorded evidence.
- Check individual task boxes only after that task is done, not when it starts.
- Check a milestone's master box only after all required work and acceptance checks pass and the milestone checkpoint commit succeeds.
- A skipped check is not a passing check. Explain environmental limits in the handoff section and leave the corresponding gate open.
- Deferred post-MVP work must remain unchecked. An explicit deferral decision may be checked separately.
- Do not mark a milestone complete merely because the implementer is stopping, has exhausted time, or has committed partial work.

## 2. Resume protocol and durable state

### Start every implementation session here

1. Read applicable instructions, `IMPLEMENTATION_PLAN.md`, and this entire document's current handoff section.
2. Inspect the working tree with `git status --short`, confirm the root with `git rev-parse --show-toplevel`, and inspect recent commits with `git log -12 --oneline` once Git exists.
3. Read the most recent milestone evidence and the first unchecked milestone. Do not restart completed milestones automatically.
4. Inspect uncommitted changes before editing. Preserve unrelated user changes and edits by other contributors. Do not use destructive cleanup to obtain a clean tree.
5. Compare checked milestones against their commits. If a referenced commit is missing or a cherry-pick was partial, reconcile the evidence before proceeding.
6. Select one concrete next action from the handoff. Update the session state before substantial implementation.
7. Run only the checks needed to establish the current baseline or verify new work. Do not repeat expensive completed checks without a reason.
8. Resume from the recorded state. If the implementation now requires a changed architectural decision, document the reason and impact before making the change.

### Session state

Update this block before stopping or handing off. Replace stale values instead of appending contradictory status.

| Field | Current value |
|---|---|
| Overall state | M00-M11 are complete. M12's packaging works and is validated structurally; its disposable-environment install matrix is outstanding, as is all of M13 and M14. `v0.1.1` is published as a **prerelease** on owner authorization and is publicly downloadable. It is unsigned and predates M14, so R01 stays unchecked |
| Active milestone | M12's install matrix, then M13 and M14. R01 stays unchecked: a prerelease published ahead of the gate does not satisfy an acceptance that requires a tested signed candidate |
| Last completed milestone | M11, MVP cache verification and explicit history deferral |
| Last validated checkpoint | Provider integration checkpoint `637d397` (`chore: checkpoint M07 provider integration`); Codex alternate-schema fix `655ad2f`; overlay regression fix `1203d52`; M06 synthetic checkpoint `929716a`; M05 `5586660`; M00 `4d10e83`; M01 `254447a`; M02 `c15d86a`; M04 `9fd1928`. |
| Branch | `main`, established by the user |
| Worktree | The Windows worktree `C:\Users\Noel Hermann\Projects\UseNotch`. The previous session's Linux-side changes were rebuilt and re-tested here and hold |
| Files currently changed | Committed. The package lock files carry a `net8.0/win-x64` section, and `Directory.Build.props` declares that runtime identifier so a plain restore and the self-contained publish agree. No provider credentials or raw account data are present in the repository |
| Last verification | Isolated SDK 8.0.424 (`.tmp\dotnet`). Locked restore, `dotnet build UseNotch.sln -c Release` with zero warnings and zero errors, and the full solution test run: Domain 5/5, Windows 33/33, Provider 75/75, UI 39/39, Application 144/144, 296 total. `dotnet format --verify-no-changes` and `git diff --check` passed. `installer/build-installer.ps1` produced `UseNotch-0.1.0-win-x64.msi` (36.4 MB, 231 files) from a clean checkout, and `installer/Test-Installer.ps1` passed every structural check against it |
| Next exact action | Run the install matrix in a disposable Windows environment, then close M13's outstanding hardware-dependent checks. `v0.1.1` is already public and unsigned, so a defect there is now user-visible and must be fixed forward rather than quietly replaced |
| Blocking condition | M12's install matrix needs a disposable Windows environment, which is not available on this machine |
| Known implementation failures | None outstanding. The overlay behaviour check's negative-coordinate variant still cannot run here because only one monitor is attached; it says so explicitly instead of timing out |
| Running processes / test environment | No UseNotch process is left running. The isolated SDK under `.tmp\dotnet` and `dotnet-dump` under `.tmp\tools` are ignored local artifacts, as is the `.tmp\hang.dmp` capture used to diagnose the earlier shutdown deadlock |
| Provider data accessed | The signed-in Codex and Claude Code accounts' own local credential files and usage endpoints were read live, as this milestone gate requires. Only percentages, window identifiers, and structural key names left the process; no token, account identifier, email address, or raw response body was printed into this repository |

### Checkpoint protocol for a completed milestone

1. Finish the milestone's tasks and run its required checks.
2. Record actual commands or manual scenarios, outcome, environment, and remaining limitations in the evidence log below. Never write only "tests passed".
3. Review the complete diff for scope, secrets, forbidden attribution, and unrelated changes. Check `git diff --check`.
4. Stage only the milestone's intended files plus this progress ledger. Do not use indiscriminate staging when unrelated changes exist.
5. Include checked task boxes and a completion entry in the milestone commit. The milestone ID in the commit subject is its durable checkpoint reference.
6. Commit using the prescribed subject or an equally clear subject containing the same milestone ID. Verify the commit exists and includes the intended files.
7. If committing fails, the milestone is not complete. Restore its master box to unchecked and record that implementation is ready but the checkpoint is pending.
8. Record the resolved short hash in the next ledger update or handoff commit. Do not repeatedly amend a commit merely to embed its own hash. Git history plus the milestone ID identifies the completing commit immediately.

Example history lookup after completion: `git log --oneline --grep='(M03)'`. Inspect the result rather than assuming the first matching subject is a valid completion.

Keep at least one completion commit per milestone. Additional narrowly scoped fix commits are allowed. Do not combine several milestones into one final commit or rewrite completed milestone history to hide intermediate work.

### If work stops mid-milestone

- Update Session state with exact files, what works, what fails, and the next action.
- Leave unfinished boxes unchecked and the milestone's master box unchecked.
- Record the last actual verification and any commands that are still running.
- Prefer a resumable local commit such as `chore: checkpoint M06 progress`, including the updated ledger, if the partial changes are appropriate to commit.
- Label partial commits clearly. They are not substitutes for the milestone completion commit.
- If partial work must remain uncommitted, list the files and explain why. Do not stash, discard, or stage another person's changes without authorization.
- Do not leave secrets or raw account data in a checkpoint, even temporarily.

### Milestone evidence log

Append one entry at each milestone completion or meaningful partial handoff. Keep this evidence in this file so it travels with the source.

| Milestone | Date / environment | Changes and verification evidence | Checkpoint reference | Remaining issue / next action |
|---|---|---|---|---|
| Planning only | 2026-09-07 | Saved the original design and prepared this build ledger. No implementation checks claimed. | No repository commit created; Git setup belongs to user | M00 |
| M00 | 2026-09-07, Windows, user-created public repository on `main` | Verified root and initial commit `0dcc55d`; preserved local reference clone while removing its unconfigured gitlink from tracking; added ignore rules; made design references portable; validated document structure, links, checkbox state, whitespace, and credential-pattern scan. No application build/test claim. | `docs: establish implementation baseline (M00)` | Begin M01; no push or release publication performed |
| M01 | 2026-09-07, Windows 11 x64 build 26200, non-elevated shell, isolated SDK 8.0.424 | Seven source and five test projects; pinned Avalonia 11.3.20/MVVM 8.4.2; locked restore and clean Release build passed with zero warnings/errors; 10 tests discovered and passed; `dotnet format --verify-no-changes` passed; `scripts/Test-DesktopLaunch.ps1` verified native title binding, non-elevated token, PerMonitorV2, and exit code 0. Public-repository/whitespace checks passed. See M01 evidence below. | `chore: scaffold solution and CI (M01)` | M02 tray/lifecycle. No remote push, hosted CI run, provider access, overlay claim, or release performed |
| M02 | 2026-09-07, Windows 11 x64 build 26200, non-elevated shell, isolated SDK 8.0.424 | Added explicit tray-owned lifetime, lazily reused Settings window, close-to-hide behavior, disabled future tray actions, and a same-user/session mutex plus 750 ms named-pipe activation or shutdown signal. The normal Settings surface can be requested by a later launch if the tray is unavailable. Release build had zero warnings/errors; 17 tests passed; formatting and whitespace checks passed. Native smoke verified hidden owner, bounded second launch, status window binding, non-elevation, PerMonitorV2, close-to-hide, and clean shutdown. | `feat: add tray and app lifecycle (M02)` | M03 native overlay gate. No Explorer restart was forced in this interactive development session; the tray host remains Avalonia-owned and a later launch remains the normal-window recovery path. No remote push or provider access performed |
| M03 partial | 2026-09-07, Windows 11 x64 build 26200, non-elevated shell, isolated SDK 8.0.424 | Implemented static OpenAI and Anthropic overlay cells, transparent borderless Avalonia window configuration, pure four-edge placement, monitor fallback, DPI conversion, native no-activate behavior, and bounded Win32 input regions. Added an independent-process input harness. Release build had zero warnings/errors; 24 solution tests passed; formatting and whitespace checks passed. One interactive native run proved transparent-corner click and wheel pass-through, visible-cell expansion without foreground activation, and clean exit. | `wip: add M03 overlay prototype` | Do not complete M03 yet. Validate the harness at 100%, 150%, and 200%, including an interactive negative-coordinate secondary monitor. The harness foreground request can be rejected by Windows when user interaction changes foreground during the run |
| M03 partial continuation | 2026-09-07, Windows 11 x64 build 26200, non-elevated shell, isolated SDK 8.0.424 | Fixed the initial layout race by applying native input regions after `UpdateLayout` and a render-priority refresh. `pwsh -NoProfile -File scripts/Test-OverlayBehavior.ps1` passed at 100% on the primary monitor. `pwsh -NoProfile -File scripts/Test-OverlayBehavior.ps1 -NegativeMonitor` passed on the selected secondary monitor at negative coordinates `(-913,-1440)`, with overlay bounds `1387,-830 260x220`. Both runs preserved foreground focus, passed transparent corner click and wheel input, expanded the visible cell, and exited cleanly. | Uncommitted M03 continuation | 150% and 200% could not be honestly run: all three monitors reported 96 DPI, and changing `PerMonitorSettings\DpiValue` did not affect new processes without signing out. The original per-monitor values were restored. Next action is an actual sign-out-based DPI matrix, then M03 completion commit and push if both pass |
| M04 | 2026-09-07, Windows 11 x64, isolated SDK 8.0.424 | Added immutable domain models, two-provider registry, deterministic network-free fixtures for every required auth/quota/activity state, stable headline and reset-passed semantics, over-limit display clamping, explicit source fidelity, real overlay bindings for both provider cells, and an explicit `--mock-scenario=` development selector. Production has no implicit mock fallback. Release build passed with zero warnings/errors, 23 Application tests, 5 Domain tests, 11 Windows tests, and 8 UI tests passed, formatting passed, and `git diff --check` passed. | `feat: add domain and mock scenarios (M04)` | M03 150% and 200% remain intentionally deferred per the user's instruction. Begin M05 |
| M05 partial | 2026-09-07, Windows 11 x64, isolated SDK 8.0.424 | Initial state-store and polling implementation checkpoint. | `wip: start M05 polling state` | Continued into the M05 completion checkpoint below |
| M05 | 2026-09-07, Windows 11 x64, isolated SDK 8.0.424 | Added one independent worker per enabled provider, one in-flight operation per worker, coalesced refresh triggers, active and idle cadence control, linked 15-second attempt and 35-second operation budgets, transient and schema backoff with persisted next-attempt status, generation-safe publication, disconnect/pause/disabled-provider behavior, atomic versioned sanitized JSON cache, corruption/old/future schema recovery, disk-write recovery, cached-startup origin labeling, freshness normalization, reset-passed and expired-headline suppression, UI dispatch of normalized states, safe shutdown, and late-response suppression after disconnect or source rotation. Locked restore passed. Release build passed with zero warnings/errors. Test discovery found 72 executable tests and all 72 passed: Application 48, Domain 5, Windows 11, UI 8. Provider.Tests remains an intentional zero-test scaffold. Formatting verification and `git diff --check` passed. | `feat: add polling and state persistence (M05)` | M03 remains user-deferred and is not part of active work. Begin M06 |

| M06 partial | 2026-09-07, Windows 11 x64 build 26200, isolated SDK 8.0.424 | Added a Codex-only provider with explicit-root, inherited `CODEX_HOME`, then profile-default source resolution; pinned TOML parsing; file, direct Windows Credential Manager, auto, ephemeral, API-key, and unsupported-mode handling; no vault enumeration; a fixed `chatgpt.com` quota request with redirects disabled, 15-second timeout, bounded response, safe HTTP errors and token-rotation retry; primary and secondary quota parsing; opaque account partitioning; and optional `--enable-codex` polling that publishes to the overlay and M05 sanitized cache. Public OpenAI Codex storage source and the documented keyring Windows target convention were inspected. Locked restore, zero-warning Release build, all 94 tests, formatting verification, `git diff --check`, and desktop launch smoke passed. | `929716a` (`chore: checkpoint M06 provider integration`) | The live vertical slice is deliberately open: no local credential or usage endpoint was accessed. Validate the installed Codex version's file/keyring/auto behavior, current endpoint response, account quota agreement, cached offline restart, and shutdown in an authorized environment before checking M06 or starting M07. |
| M06/M03 regression fix | 2026-09-07, Windows 11 x64 build 26200, isolated SDK 8.0.424 | Replaced `SetWindowRgn` input clipping, which also clipped Avalonia painting at scaled display settings, with the existing exact `WM_NCHITTEST` click-through behavior. Null `used_percent` now becomes an honest unavailable quota value rather than a schema failure. Isolated App/UI Release builds passed with zero warnings/errors. Provider tests (23) and UI tests (9) passed, along with format and whitespace checks. | `1203d52` (`fix: preserve overlay cells at scaled DPI`) | Normal-output build and interactive smoke are open because process 4024, started by the user, locks the Release DLLs. The process did not respond to the normal bounded shutdown signal and was left running. Close it from the tray, then rebuild and rerun the native smoke. |
| M06 alternate response compatibility | 2026-09-07, Windows 11 x64 build 26200, isolated SDK 8.0.424 | The user-reported live overlay status still said its response format was unsupported after the high-DPI correction. Added bounded compatibility for `rate_limits`, `five_hour`, `weekly`, `percent_left`, millisecond or ISO reset timestamps, and a one-level `data` wrapper. No response data is retained or logged. An isolated Provider.Tests Release build had zero warnings/errors and all 25 tests passed. | Pending local checkpoint commit | Restart the user-launched app from normal Release output and run the live vertical slice. If it remains unsupported, capture no raw response, retain the safe error, and investigate only with an explicitly sanitized schema description. |
| M07 partial | 2026-09-07, Windows 11 x64 build 26200, isolated SDK 8.0.424 | Began Claude Code integration without live access. Added selected-root / `CLAUDE_CONFIG_DIR` / profile-default discovery, bounded shared credential-file reads with one partial-write retry, in-memory access-token use, isolated OAuth request construction, safe status mapping, and limits-array plus named-window parser merging. Isolated Provider.Tests Release build had zero warnings/errors and all 28 tests passed. | `637d397` (`chore: checkpoint M07 provider integration`) | Add synthetic credential and HTTP matrix tests, then wire opt-in polling. M06 remains unchecked and deferred by user direction. |
| M07 auth classification and account epoch | 2026-09-07, Linux (non-Windows continuation session), locally installed isolated SDK 8.0.424 (`~/.dotnet`, not system-wide) | Fixed a credential-reader bug where a persistently malformed `.credentials.json` escaped as a raw `JsonException` instead of an honest unsupported-format error. Added an opt-in `AuthenticationHint` on `ProviderReadException` (Application layer, additive; Codex is unaffected because it never sets it) and used it in the Claude adapter to classify missing/expired/rejected/access-denied/unsupported auth failures, with a past local expiry hint only relabeling a server-confirmed 401 as expired rather than deciding expiry locally. Added account-epoch bumping in the polling worker: `AccountGeneration` now increments whenever a successful read's account partition differs from the previously published one, so a real account change cannot show a stale reading as current; Claude's partition is still the access-token fingerprint (no stable non-token account id is exposed by the credential file), which is a deliberately conservative choice noted as a limitation in section 4. Built and ran only the platform-neutral projects, since this machine cannot target `net8.0-windows` and no Windows-targeting change was made to work around that: zero-warning Release builds, `UseNotch.Domain.Tests` 5/5, `UseNotch.Application.Tests` 51/51 (48 existing + 3 new covering the hint and the epoch bump), `UseNotch.Provider.Tests` 41/41 (28 existing + 13 new covering Claude credential/HTTP/auth-classification cases), `dotnet format --verify-no-changes`, and `git diff --check` all passed. No native, tray, overlay, Windows Credential Manager, or live-endpoint behavior was touched or exercised. | Pending local checkpoint commit | The remaining M07 checklist item needs a Windows host with a real installed Claude Code CLI: inspect an actual `.credentials.json` and a live `/api/oauth/usage` response, then decide whether Claude exposes a stable account identifier that should replace the token-fingerprint partition. Also re-run the full solution build/test/format sequence on Windows to confirm these changes hold there, since `UseNotch.App`, `UseNotch.Platform.Windows`, `UseNotch.UI.Tests`, `UseNotch.Windows.Tests`, and `OverlayInputHarness` were not built in this session. M06's live vertical slice and M03's native overlay gate are unrelated and remain separately open. |

| M07 | 2026-09-07, Windows 11 x64 build 26200, non-elevated shell, isolated SDK 8.0.424 | Closed the live inspection gate against Claude Code 2.1.263 and a live `/api/oauth/usage` response, recording the real credential and response layout in the M07 checklist above. Added scoped-window identity so the observed repeated `weekly_scoped` entries no longer collapse into one, labelling each from `scope.model.display_name`. Wired opt-in `--enable-claude` polling next to `--enable-codex`, with one independent worker each, and corrected the overlay's Anthropic cell to show its headline rather than its status. Fixed a Codex credential bug where a signed-in ChatGPT installation was misread as API-key authentication because `auth.json` carries a null `OPENAI_API_KEY`; `auth_mode` now decides, and only a non-empty key counts. Fixed a shutdown deadlock, located with a full process dump, where exit blocked the UI thread on the polling dispose while a worker await needed that same thread: worker awaits no longer capture a synchronization context, and exit now disposes on the thread pool under a five-second budget. Added a regression test that disposes the coordinator from a thread owning a non-pumping synchronization context, and deflaked the account-epoch test. Locked restore, zero-warning Release build, 125 solution tests (Domain 5, Windows 11, Provider 47, UI 9, Application 53), `dotnet format --verify-no-changes`, and `git diff --check` all passed. `scripts/Test-LiveProviderSlice.ps1` phase 1 passed against both live accounts: Codex reported 53 percent of its 5h window and 8 percent weekly, Claude reported its session, all-models, and Fable scoped windows, both cache files were written with no credential material, both overlay cells opened the detail pane, and the app exited cleanly about 180 ms after the bounded shutdown signal. | `feat: integrate Claude usage (M07)` | M06's offline-restart leg is the only remaining live-slice item. The per-user WinINET proxy the script sets is restored after the run but does not actually fail .NET's requests, so phase 2 needs a different non-elevated network-failure mechanism |

| M06 | 2026-09-07, Windows 11 x64 build 26200, non-elevated shell, isolated SDK 8.0.424 | Closed the live vertical slice with `scripts/Test-LiveProviderSlice.ps1`, which passed every leg: local source discovery, live request, snapshot, overlay window, detail pane from both cells, sanitized cache files, offline restart served from that cache, and bounded shutdown. Running it exposed three real defects, all fixed here. A plain transport failure threw `HttpRequestException`, which neither adapter mapped, so the polling worker died silently and no failure was ever published; both adapters now turn connection, DNS, TLS, and proxy failures into transient network errors. The worker loop now also catches any unmapped exception, publishes a safe transient failure and backs off, so one adapter defect can no longer stop a provider permanently. A failed attempt used to relabel a restored reading as live; the cached-startup origin is now preserved until a successful read replaces it. The offline leg is simulated with an unreachable proxy in the launched process's environment, which needs no elevation and changes no machine or user network setting. Locked restore, zero-warning Release build, 129 solution tests (Domain 5, Windows 11, Provider 49, UI 9, Application 55), `dotnet format --verify-no-changes`, and `git diff --check` passed. Live agreement check: the snapshot percentages matched the values the provider endpoints reported for the same windows in the same run. | `feat: integrate Codex usage (M06)` | Begin M08. No unresolved M06 compatibility issue remains |

| M08 | 2026-09-07, Windows 11 x64 build 26200, non-elevated shell, isolated SDK 8.0.424 | Added activity monitoring as its own layer with its own workers. Inspected the real sources first: Claude Code 2.1.263 writes per-session records under `<ClaudeRoot>\sessions` carrying `pid`, `sessionId`, `procStart` as a Windows FILETIME string, `status`, and millisecond timestamps, while `<CodexRoot>\state_5.sqlite` holds a WAL-mode `threads` table whose `updated_at` is Unix seconds. The Claude monitor maps only recognized statuses, verifies each process by id and creation time, drops ended sessions, downgrades access-denied inspection to an estimate, and never carries a working directory, session name, socket path, or raw session id past the parser. The Codex monitor probes the schema before querying, opens the database read-only with a private cache and a 250 ms busy timeout, caps the query at eight rows, follows a record-supplied rollout path only when it resolves inside the Codex root and then reads only its metadata, and reports estimated recent activity or nothing at all, never idle or waiting. A `FileSystemWatcher` with a 150 ms debounce makes supported changes prompt, a watcher error requests a full pass, and a session lock pauses both coordinators. Added `Microsoft.Data.Sqlite` 10.0.11 and `Microsoft.Win32.SystemEvents` 8.0.0 with updated lock files; the audit-blocked SQLitePCLRaw versions are avoided by the 10.0.11 line. Locked restore, zero-warning Release build, 166 solution tests (Domain 5, Windows 11, Provider 75, UI 9, Application 66), `dotnet format --verify-no-changes`, and `git diff --check` passed. Ran both monitors against the live machine: Claude reported one working session as provider-reported, confirming the FILETIME comparison matches a real process, and Codex reported a supported source with no recent activity while it was not running. The live quota slice still passes with the activity workers running, and the quota cache contains no activity. | `feat: add conservative activity monitoring (M08)` | Begin M09. A Codex working state was observed only through synthetic fixtures, since Codex was not running during this session; the Claude path was confirmed live |

| M09 | 2026-09-07, Windows 11 x64 build 26200, non-elevated shell, isolated SDK 8.0.424 | Added the real settings surface and the security behavior behind it. Settings live in one validated JSON document under the known local application data folder, written through a temporary file and an atomic replace, migrated when the schema version is older, and preserved as `settings.json.invalid` beside recovered defaults when unreadable. Validation fixed a real defect found by its own test: a relative selected root was silently resolved against the process working directory, so a root must now already be absolute or it is dropped. The settings window carries the documented Status, Providers, Appearance, General, Privacy, and About navigation, shows each provider's resolved source, quota scope, authentication state, freshness, activity, and a recovery action that always points at the owning tool, and offers enable, disable, reconnect, pause, clear-data, and a manual refresh rate limited to one attempt per 15 seconds. Launch at login writes only the current user's Run key and always reports the registration that actually exists, including one that points somewhere else. `DpapiSecretStore` protects app-owned material with the current-user scope, proven by a machine-scope reader failing to open it, and the secrets directory is created with inheritance removed and access granted only to the current user. Diagnostics are opt-in, sanitized of tokens, JWTs, bearer headers, email addresses, and local paths, truncated, newline-safe, and size-capped; no code path writes a raw token or response body. Saved settings now decide which providers run, with the command-line switches kept as development overrides. Locked restore, zero-warning Release build, 213 solution tests (Domain 5, Windows 23, Provider 75, UI 22, Application 88), `dotnet format --verify-no-changes`, and `git diff --check` passed. A non-elevated run started both providers from saved settings with no switch, wrote both cache files, produced a secrets directory whose only access entry is this user, and exited cleanly. | `feat: add settings and privacy controls (M09)` | Begin M10. The settings document is written only once the user changes something, so a first run legitimately leaves no `settings.json` behind |

| M10 partial | 2026-09-07, Windows 11 x64 build 26200, non-elevated shell, isolated SDK 8.0.424 | Rebuilt the overlay around a pure presentation state machine covering Hidden, Collapsed, Expanded, ProviderDetailsOpen, and Pinned, with hover delays bounded on both sides. Added a purpose-built `QuotaRing` rather than a scaled-down hardware gauge: a zero reading draws no arc, a missing reading draws only the track, and full and over-limit readings are deliberately identical while the value text keeps them apart. Added `QuotaDisplay`, which turns a runtime state into value, scope, freshness, severity, and an automation name, keeping severity separate from provider identity. Applied the documented palette, typography, spacing, and sizes, added focus-visible styles, automation names, and a bounded scrolling detail panel that restates every compact number with the window it belongs to. Busy motion now requires fresh working activity, a visible overlay, and no reduced-motion preference. Settings drive edge, monitor, pin, visibility, scale, and reduced motion. Fixed a fixture defect: the estimated mock scenario had no activity state to label. Made the native region provider exception-safe, since throwing inside the hit test would silently make the whole transparent surface capture input. Locked restore, zero-warning Release build, 263 solution tests (Domain 5, Windows 23, Provider 75, UI 39, Application 121), `dotnet format --verify-no-changes`, and `git diff --check` passed. | `wip: rebuild overlay presentation (M10)` | Do not check M10 complete. Its interactive input and focus gate is unproven: `scripts/Test-OverlayBehavior.ps1` was updated for the new layout and now takes foreground reliably, and a live run confirmed the collapsed handle is interactive and expands the overlay on hover, but transparent-space pass-through could not be measured because the harness stops receiving clicks anywhere once the overlay process starts. The 150 and 200 percent display-scale matrix is also still outstanding |

| M10 | 2026-09-07, Windows 11 x64 build 26200, non-elevated shell, single monitor at 175 percent scaling, isolated SDK 8.0.424 | Completed the overlay work checkpointed above and closed its interactive gate, which required fixing a real defect that predated this milestone. Measurement showed the overlay captured every click inside its rectangle: returning `HTTRANSPARENT` from `WM_NCHITTEST` only forwards input to another window owned by the same thread, so it could never hand a click to a different application. The same measurement showed a second fault: Avalonia reported a render scaling of 1.75 for a window whose client area equalled its device-independent size, so multiplying by it placed every interactive region outside the window. Click-through now uses a cursor-polled `WS_EX_TRANSPARENT` toggle over a layered window, with `WM_NCHITTEST` retained for the precise per-region answer, and the window reports device-independent regions that the platform converts using the real client size. Both were confirmed against the build from the previous milestone, which failed the same way, so neither was introduced by the overlay rebuild. Added `OverlayRegionScaler` with tests covering 100 percent, scaled, and unusable client sizes. Locked restore, zero-warning Release build, 273 solution tests (Domain 5, Windows 33, Provider 75, UI 39, Application 121), `dotnet format --verify-no-changes`, and `git diff --check` passed. `scripts/Test-OverlayBehavior.ps1` passed on the primary monitor at 175 percent: a real click and wheel in transparent space reached an independent process, hovering the handle expanded the overlay without taking foreground, and the visible cell opened details without activating the overlay window. | `feat: polish overlay and accessibility (M10)` | Begin M11. The negative-coordinate variant of the overlay check could not run because only one monitor is attached; the script now reports that instead of timing out |

| M11 | 2026-09-07, Windows 11 x64 build 26200, non-elevated shell, isolated SDK 8.0.424 | Closed the persistence boundary for the first release. Fixed a real gap found by its own test: the JSON cache serialized whatever state it was handed, so a state carrying activity would have written a session label to disk. The cache now strips activity both when writing and when loading, which also guarantees a restored reading can never arrive with stale activity attached. Added a source-identifier check so a cached reading captured from a different credential source is discarded rather than shown while the first live read runs. Verified by test: offline restart serves the last good reading marked cached and stale; a reading older than 24 hours is expired and loses its headline; a window whose reset has passed loses its headline; disconnecting clears both state and cache; a save cannot land after a disconnect, because disconnect awaits the worker first; a corrupt or future-version cache is ignored while the provider still starts; a save leaves no partial file. Confirmed no chart package is referenced and no source mentions a history database or repository. Locked restore, zero-warning Release build, 285 solution tests (Domain 5, Windows 33, Provider 75, UI 39, Application 133), `dotnet format --verify-no-changes`, and `git diff --check` passed. A full application run left only the two sanitized JSON cache files under the application data root, each with a null activity value and an opaque account partition. | `fix: finalize cache recovery rules (M11)` | Begin M12. H01 remains deferred and unchecked; no history interface or implementation exists to maintain |

| M12 partial | 2026-09-07, Windows 11 x64 build 26200, non-elevated shell, isolated SDK 8.0.424 | Added the per-user WiX v5 MSI, its build script, a structural validator, and the packaging and release workflows. `installer/build-installer.ps1` restores locked dependencies, restores pinned tools, runs the full test suite, publishes self-contained, and packages, stopping at the first failure so partial output cannot be uploaded as success; every directory it clears is proven to be inside the repository before any recursive delete. `installer/Test-Installer.ps1` inspects the built MSI without installing it and checks product identity, the stable upgrade code, version agreement with the application, per-user scope with no Program Files directory, a self-contained payload including the .NET host and runtime, downgrade rejection, and removal of the launch-at-login value on uninstall. The packaging workflow runs for every pull request with bounded retention and no secrets; the release workflow runs only for a pushed tag, keeps read-only default permissions, and creates a draft release. Repository tests enforce these invariants so drift is caught by the ordinary test run. Locked restore, zero-warning Release build, 296 solution tests (Domain 5, Windows 33, Provider 75, UI 39, Application 144), `dotnet format --verify-no-changes`, and `git diff --check` passed. The build produced `UseNotch-0.1.0-win-x64.msi`, 36.4 MB and 231 files, and the validator passed against it. No tag was created or pushed and nothing was published. | `wip: add MSI and packaging pipeline (M12)` | Do not check M12 complete. Clean install, N-1 upgrade, downgrade rejection, uninstall, reinstall, and interrupted upgrade still need a disposable Windows environment, and the plan forbids using the developer's daily environment to close that gate |

| M12 lock-file fix | 2026-09-08, Windows 11 x64 build 26200, isolated SDK 8.0.424 | Both hosted workflows failed on the M12 commit with NU1004: the self-contained publish had rewritten every lock file with a `net8.0/win-x64` section, those were committed, and no project declared that runtime identifier, so `dotnet restore --locked-mode` refused them. The M12 entry's locked-restore claim was true when it was run and stopped being true when the publish rewrote the lock files afterwards, which the evidence should have caught. `Directory.Build.props` now declares `RuntimeIdentifiers` as win-x64, and the six test lock files gained the matching section. Verified by running the hosted sequence locally: `dotnet restore --locked-mode` clean, zero-warning Release build, 310 solution tests, `dotnet format --verify-no-changes`, then `installer/build-installer.ps1` producing the MSI and `installer/Test-Installer.ps1` passing, with no lock file left modified afterwards. | `fix: keep lock files consistent for a locked restore` | None |

| v0.1.0 candidate | 2026-09-08, Windows 11 x64 build 26200, isolated SDK 8.0.424, plus the hosted windows-2022 runners | Tagged `v0.1.0` on request so the hosted pipeline builds the candidate. The release workflow verifies version agreement, builds and validates the MSI, uploads it, and opens a **draft** release only. This is not R01: the artifact is unsigned, M12's clean install, upgrade, downgrade, uninstall and interrupted-upgrade matrix has not been run in a disposable environment, and M14 has not started. Before the tag, the hosted sequence was reproduced locally: locked restore, zero-warning Release build, 310 solution tests, `dotnet format --verify-no-changes`, `git diff --check`, `installer/build-installer.ps1` producing `UseNotch-0.1.0-win-x64.msi`, and `installer/Test-Installer.ps1` passing against it. | Tag `v0.1.0` and the commit it points at | Do not publish the draft. Close M12's install matrix, M13, and M14 first; R01 remains unchecked |

| Product identity | 2026-09-08, Windows 11 x64 build 26200, isolated SDK 8.0.424 | Named the product consistently everywhere it surfaces. `Directory.Build.props` now defines Product, Company, Authors, Copyright, and Description once, and `UseNotch.App` sets AssemblyTitle, so the built executable reports ProductName `UseNotch`, FileDescription `UseNotch`, CompanyName `Noel Hermann`, verified with `Get-Item ... .VersionInfo`. The MSI carries Manufacturer `Noel Hermann`, ARPPRODUCTICON, ARPURLINFOABOUT, ARPHELPLINK, and ARPNOREPAIR, verified by reading the Property table out of the built package. About reads the same values back from assembly attributes rather than repeating them. The mark was redrawn: `scripts/New-AppIcon.py` now generates the SVG logo and the multi-size .ico from one set of constants, so they cannot drift, and the shape is a quota ring with a notch cut through it in the two provider accents. Legibility was checked by rendering 256 down to 16 pixels on light and dark grounds. README rewritten. No screenshots were committed: the available captures show real quota values, which is account data. Version bumped to 0.1.1. | Locked restore, zero-warning Release build, 310 tests, `dotnet format --verify-no-changes`, `git diff --check`, `Test-VersionAgreement.ps1 -Tag v0.1.1`, `build-installer.ps1` producing `UseNotch-0.1.1-win-x64.msi`, `Test-Installer.ps1` passing | Settings visual design is deliberately untouched and is the next pass. The install matrix, M13, and M14 are unchanged and still open |

| v0.1.1 published | 2026-09-08, published from the hosted windows-2022 runner, verified from this workstation | The owner could not find a download because both releases were drafts, which GitHub hides from everyone without push access and never attaches to their tag. On authorization, `v0.1.1` was published as a prerelease and the stale `v0.1.0` draft was deleted with its tag; that tag's commit `1a8da6e` remains on `main`, so no history was lost. Verified after publishing: the release reports `isDraft: false`, `isPrerelease: true`, tag `v0.1.1` at `https://github.com/FIEF-nohell/UseNotch/releases/tag/v0.1.1`; an unauthenticated request for the asset returns HTTP 302 to the signed asset host, so the download works logged out. The published asset was downloaded and checked on its own rather than trusting the local copy: 38,281,216 bytes, SHA256 `9c25802368cdce8008111da27390db8e707f99c0898bd4d106d2970baf05f07c`, and `installer/Test-Installer.ps1` passes against the downloaded file, reporting UseNotch 0.1.1, the expected upgrade code, and 231 files. The local build of the same version hashes differently (`4e210cd4...`): the MSI is not byte-reproducible between a runner and a workstation, so the hash attests to the published artifact only, not to equality with a local build. No standalone .exe is produced; the MSI is the only artifact by decision. | Tag `v0.1.1`, commit `04ad1ae`, and the published release URL | R01 is still unchecked. Its acceptance requires a tested signed candidate and this artifact is unsigned with M12's install matrix unrun, so the release notes state both. A published version cannot be silently replaced: any fix ships as a new version |

Use milestone IDs as checkpoint references until a short hash can be recorded in a later update. For manual tests, include Windows build, app build, DPI/monitor layout, and result. Store only sanitized evidence; no screenshots with account data or conversation content.

### Decisions and deviations log

| Date | Decision | Reason / impact |
|---|---|---|
| 2026-09-07 | Git initialization and remote setup belong to the user | Implementation begins by verifying their setup, not creating it |
| 2026-09-07 | Persistent history is post-MVP | Last-good JSON cache satisfies initial recovery needs |
| 2026-09-07 | Native overlay correctness is an early blocking gate | Prevent provider work from hiding an unreliable desktop foundation |
| 2026-09-07 | Repository is public; baseline includes portable source links and local-data ignore rules | User established Git; preserve the local clone but do not distribute it as an unconfigured gitlink |
| 2026-09-07 | Use SDK 8.0.424 and a centrally pinned Avalonia 11.3.20 package set with FluentTheme | Remain within the planned .NET 8/Avalonia 11 stack while updating the inspected 11.2.5 baseline; compatible package restore, build, headless binding, and native startup were verified |
| 2026-09-07 | Install the validation SDK under ignored `.tmp/dotnet` | System SDK is 9.0.313; use an isolated .NET 8 SDK without changing system installation. Resume with `.\.tmp\dotnet\dotnet.exe` or install the SDK from global.json |
| 2026-09-07 | M01 layer projects and Provider.Tests remain scaffolds where behavior is scheduled later | No invented domain/provider implementations or placeholder passing tests. System.Text.Json is in the framework; repository implementations remain M05 work |
| 2026-09-07 | Process DPI awareness is established by the manifest; Avalonia owns scaling | Native startup confirmed PerMonitorV2. M03 must coordinate window placement without a competing process-wide DPI setter |
| 2026-09-07 | No upstream source files or visual assets copied in M01 | Foundation uses normal framework wiring informed by the references; no upstream source notice is required for a copied file at this stage. Preserve notices when later adapting source |
| 2026-09-07 | Use an ICO for the Win32 tray asset | Avalonia's Win32 tray host rejects SVG icon data at startup. Reused the existing Avalonia foundation's raster app icon after checking that project for a license or notice file and finding none. Replace it with finalized UseNotch branding before release |
| 2026-09-07 | Launch at login uses the current user's `Run` registry value rather than a Startup-folder shortcut | A shortcut needs `IShellLink` COM interop for no functional gain. The Run value is per-user, needs no elevation, and can be read back exactly, which the milestone requires |
| 2026-09-07 | Added `Microsoft.Data.Sqlite` 10.0.11, `Microsoft.Win32.SystemEvents` 8.0.0, and `System.Security.Cryptography.ProtectedData` 8.0.0 | Read-only activity queries, session lock notifications, and current-user DPAPI. The 10.0.11 SQLite line was chosen because earlier lines resolve SQLitePCLRaw versions that the repository's dependency audit blocks |
| 2026-09-07 | Saved settings decide which providers run; the `--enable-codex` and `--enable-claude` switches remain as development overrides | Settings became the real configuration surface in M09. Keeping the switches avoids changing a user's stored configuration during a development or smoke run |
| 2026-09-07 | Cross-process click-through uses a cursor-polled `WS_EX_TRANSPARENT` toggle, with `WM_NCHITTEST` kept for precision | Returning `HTTRANSPARENT` only forwards a click to another window owned by the same thread, so it cannot hand input to another application. This was found by measurement after the M06 change from `SetWindowRgn`, and it meant the overlay silently swallowed every click inside its rectangle |
| 2026-09-07 | The overlay window reports interactive regions in device-independent units and the platform converts them | Avalonia reported a render scaling of 1.75 for a window whose client area matched its device-independent size, which placed every region outside the window. Converting with the real client size is correct regardless of what scaling is reported |
| 2026-09-07 | Persistent history stays deferred as H01 and is not implemented for the first release | The sanitized last-good JSON cache covers the recovery the MVP needs, verified in M11. No history interface, database, or chart dependency exists, so nothing has to be maintained or removed before release |
| 2026-09-07 | A cached reading is restored only when its source identifier matches the current connection | Otherwise a reading captured from a different credential source, and possibly a different account, would be shown while the first live read is still running |
| 2026-09-07 | The installer uses the WiX v5 `Files` element instead of a separate harvest step | WiX v5 has no `harvest` command in its CLI, and globbing the published output in the authoring removes the class of failure where a harvested file list drifts from what was actually published |
| 2026-09-07 | Pinned WiX 5.0.2 as a local tool in `.config/dotnet-tools.json` | A local tool manifest pins the packaging toolchain the same way the SDK and packages are pinned, so a clean checkout builds the same MSI |
| 2026-09-07 | The release workflow creates a draft release only | Publication is R01 and needs explicit authorization. A draft keeps the pipeline verifiable without publishing anything |
| 2026-09-08 | The release workflow publishes a prerelease instead of a draft | The owner authorized publication. A draft is only reachable by accounts with push access, so the artifact was undownloadable in practice, which is a worse outcome than publishing with the gaps stated plainly. Prerelease, not a full release, because the MSI is unsigned and the install matrix has not been run |
| 2026-09-08 | `Directory.Build.props` declares `RuntimeIdentifiers` as win-x64 | The M12 self-contained publish rewrote every lock file with a runtime-specific section. Those were committed while no project declared a runtime identifier, so a later `dotnet restore --locked-mode` rejected them as inconsistent and both hosted workflows failed with NU1004. Declaring the identifier makes the lock files correct for a plain restore and for the publish, and stops the packaging script from dirtying the working tree |

Add dated entries for dependency upgrades, platform limitations, changed provider contracts, and scope changes. Update both plans if a product or architecture decision changes. Do not silently replace requirements with easier behavior.

## 3. Milestone tracker

Execute in order. Each required milestone depends on its predecessor unless an explicitly independent task is documented. Tests are part of each milestone, not postponed until M13.

- [x] M00: Verify user Git setup and baseline planning checkpoint.
- [x] M01: Solution, dependencies, non-elevated manifest, and CI build.
- [x] M02: Avalonia shell, tray, and application lifecycle.
- [x] M03: Native overlay prototype and Windows behavior gate.
- [x] M04: Shared domain, mock provider, and complete state fixtures.
- [x] M05: Polling, concurrency, state store, and JSON cache.
- [x] M06: OpenAI / Codex usage integration.
- [x] M07: Anthropic / Claude Code usage integration.
- [x] M08: Conservative local activity monitoring.
- [x] M09: Settings, privacy, source selection, and startup.
- [x] M10: Overlay polish, detailed status, and accessibility.
- [x] M11: MVP cache verification and explicit history deferral.
- [ ] M12: WiX packaging and build/release pipeline validation.
- [ ] M13: End-to-end, resource, recovery, and Windows matrix validation.
- [ ] M14: Release hardening and validated release candidate.

Deferred scope:

- [ ] H01: Optional persistent history and charts after MVP.
- [ ] R01: Publish and verify a release after explicit publication authorization.

## 4. Ordered implementation work

### M00: Verify user Git setup and baseline

Objective: establish a safe, recoverable starting point without taking over Git setup.

Areas: repository root, `.gitignore`, the two root planning documents, existing user-provided repository configuration.

- [x] Verify Git exists and its top-level path is the intended UseNotch root. If it is absent or points to an unintended ancestor, stop dependent Git operations and record the condition.
- [x] Inspect branch, existing history, and uncommitted changes; preserve the user's choices.
- [x] Exclude `.tmp/`, build output, installer output, local credentials, diagnostic captures, and transient test artifacts from tracking. Do not untrack existing user files without understanding them.
- [x] Verify neither the nested Codenotch clone nor unrelated files from the user's profile are staged.
- [x] Confirm the inspected reference commits are recorded in `IMPLEMENTATION_PLAN.md`.
- [x] Commit the root planning documents and intended baseline ignore rules, unless already committed by the user; record their existing commit instead of duplicating it.

Acceptance and tests: correct root/branch; no source clone or secrets staged; plans readable; clean intended diff. This milestone requires no application tests.

Main risks: accidentally committing a nested repository, modifying the user's remote, or treating missing Git as permission to initialize it.

Completion commit: `docs: establish implementation baseline (M00)`.

### M01: Project foundation

Objective: create a buildable, testable solution with enforced boundaries.

Areas: `UseNotch.sln`, `src/`, `tests/`, `Directory.Build.props`, `Directory.Packages.props`, SDK/tool version files, app manifest, `.github/workflows/ci.yml`.

- [x] Scaffold the project structure from the design. Domain and Application remain framework-independent; Windows-specific projects use the appropriate Windows target.
- [x] Pin one compatible Avalonia 11 package set and one theme stack. Use the inspected modshell-cs versions as evidence, not automatic latest-version selection.
- [x] Add CommunityToolkit.Mvvm, System.Text.Json-based repositories as later implementations, logging abstractions, and xUnit projects.
- [x] Keep LiveCharts, hardware-monitor packages, and optional history dependencies out until justified.
- [x] Enable nullable analysis, compiled XAML bindings, consistent formatting, and appropriate analyzers.
- [x] Create an `asInvoker`, `uiAccess=false` manifest and establish DPI initialization ownership.
- [x] Add CI restore, Release build, and test discovery. Add test execution as tests arrive; do not claim an empty suite as coverage.
- [x] Check source licensing before adapting files. Preserve required notices; do not add AI attribution.
- [x] Record the runtime support deadline and dependency compatibility decisions in the evidence log.

Acceptance: a clean clone restores and builds on Windows; application starts without UAC; project references follow the design.

Tests: `dotnet restore UseNotch.sln`, `dotnet build UseNotch.sln -c Release --no-restore`, initial test discovery, standard-user launch. Inspect generated output for unexpected dependencies.

Main risks: copying modshell-cs elevation or hardware collection; mixing incompatible Avalonia packages; creating too many speculative abstractions.

Completion commit: `chore: scaffold solution and CI (M01)`.

#### M01 verification evidence

All CLI checks used `.\.tmp\dotnet\dotnet.exe` (SDK 8.0.424). The equivalent `dotnet` commands are in README.md. Windows reports build 26200; the invoking shell and launched app were non-elevated.

| Check | Command / method | Actual result |
|---|---|---|
| Restore | `dotnet restore UseNotch.sln` | All 12 projects restored; lock files generated; no audit warnings |
| Build | `dotnet build UseNotch.sln -c Release --no-restore` | Passed; zero warnings and zero errors |
| Discovery | `dotnet test UseNotch.sln -c Release --no-build --no-restore --list-tests` | 10 tests discovered across Domain, Application, UI, and Windows test projects |
| Tests | `dotnet test UseNotch.sln -c Release --no-build --no-restore --logger trx --results-directory TestResults` | 10 passed, zero failed, zero skipped. Provider.Tests has zero tests and is explicitly not counted as coverage |
| Formatting | `dotnet format UseNotch.sln --verify-no-changes --no-restore` | Passed without edits |
| Native startup | `pwsh -NoProfile -File scripts/Test-DesktopLaunch.ps1` | Actual HWND/title loaded; process token non-elevated; native context PerMonitorV2; CloseMainWindow ended process within five seconds, exit 0 |
| Clean source | Exported staged Git tree with `git archive` into ignored `.tmp/m01-clean`; ran locked restore, Release build, and tests using the isolated SDK | Passed without existing bin/obj output: zero build warnings/errors, same 10 passing tests |
| Dependency inspection | `dotnet list src/UseNotch.App/UseNotch.App.csproj package --include-transitive` | Expected Avalonia.Desktop rendering/native dependencies, MVVM, and logging abstractions; no chart, hardware-monitor, SQLite, or extra provider package |
| Repository hygiene | `git diff --cached --check` and staged-text credential/attribution/dash scan | Passed; SDK, captures, TRX, reference clone, and build output remain ignored |
| Cleanup | Queried remaining `UseNotch.App` processes after smoke check | Zero |

The CI workflow is configured for `windows-2022` with pinned action commits, locked restore, Release build, discovery, tests, formatting, and seven-day TRX retention. It has not been executed remotely in this milestone. No remote push was authorized or performed.

Native focus preservation, click-through, tray lifecycle, multiple monitors, provider credentials, live quotas, performance soak, signing, and packaging were not tested in M01. They remain unchecked in their assigned milestones.

### M02: Shell and tray lifecycle

Objective: keep the app reachable and make shutdown deterministic.

Areas: App startup, lifetime coordinator, tray menu, settings shell, single-instance service, lifecycle tests.

- [x] Use explicit desktop shutdown with application-owned background-service lifetime.
- [x] Implement one instance per user/interactive session and bounded same-user activation forwarding.
- [x] Implement tray Show status/Settings, Show/hide overlay, Pin, Refresh, Pause, and Quit commands; disable commands until their services exist.
- [x] Create settings lazily and reuse it. Closing it hides it without terminating the tray app.
- [x] Make a second launch expose the existing settings/status window without creating another poller.
- [x] Handle tray loss or Explorer restart; keep a recoverable normal window if no tray surface is available.
- [x] Implement cancellation/disposal ownership and a bounded exit path.

Acceptance: repeated show/hide and second launch work; closing settings preserves the app; Quit removes process and tray icon; no orphan background operation remains.

Tests: lifecycle and command tests plus interactive Explorer-restart, duplicate-launch, and quit verification. Record actual process cleanup.

Main risks: hidden unmanageable process, accidental shutdown, leaked tray handlers, duplicate instances.

Completion commit: `feat: add tray and app lifecycle (M02)`.

### M03: Native overlay prototype

Objective: prove Windows overlay correctness with static cells before live accounts are involved.

Areas: `OverlayWindow`, `OverlayController`, `IOverlayPlatform`, Windows host, monitor service, placement geometry, interactive test harness.

- [x] Display a static two-cell borderless transparent overlay with topmost and no-activate behavior applied before first show.
- [x] Isolate HWND access and hooks behind the platform boundary. Verify hook APIs exist in the pinned Avalonia version.
- [x] Implement tightly bounded native regions for the visible shell and detail area. Do not rely on Avalonia hit testing or `HTTRANSPARENT` alone.
- [x] Preserve Avalonia renderer ownership; do not force layered-window operations without validating compatibility.
- [x] Implement pure placement math for all four edges, work-area offsets, selected monitor, negative coordinates, and DIP/pixel conversion.
- [x] Handle monitor removal and DPI changes without placing the overlay off-screen.
- [x] Add a standard-user independent window to validate clicks and wheel input behind transparent regions in a disposable interactive environment.
- [ ] Record foreground HWND before and after show, hover, expansion, clicking, and refresh.
- [ ] Validate pointer input at visible controls, transparent corners, detail edges, and after hiding.

Acceptance: no focus theft; input outside the region reaches another process; inside controls work; selected edge placement survives mixed DPI. Record the exact tested Avalonia version, renderer, Windows build, and monitor layout.

Tests: pure geometry suite; interactive cross-process input and activation checks at 100%, 150%, and 200%, including a negative-coordinate secondary display. Headless tests alone cannot complete this gate.

Main risks: first-show activation, invisible interception, region/rendering mismatch, native handle leaks.

Gate: do not mark M03 complete or start live integration with an unresolved focus or input bug. Produce a minimal reproducible issue before considering any framework alternative.

Completion commit: `feat: validate native overlay behavior (M03)`.

### M04: Domain and mock-driven UI states

Objective: define honest readings and make the complete experience testable offline.

Areas: Domain, Application contracts, mock provider, fixture catalog, basic provider cell/detail view models.

- [x] Define provider connection, account scope, authentication, snapshot, quota window/limit, usage block, activity, status/error, freshness, and fidelity models.
- [x] Keep tokens, raw JSON, UI glyphs, native handles, and database rows outside Domain.
- [x] Implement stable headline selection, optional values/units, over-limit display rules, and UTC timestamps.
- [x] Distinguish provider-reported fidelity from documented versus unpublished source contracts.
- [x] Build synthetic fixtures for loading, authenticated, missing, expired, rejected, forbidden, unsupported, stale, reset-passed, rate-limited, and error states.
- [x] Add observed working/waiting and estimated/unknown activity fixtures.
- [x] Bind fixtures through the real presentation path and provide a development-only scenario selector.
- [x] Make mocking explicit; production must not silently replace failed live requests with demo values.

Acceptance: every required visible state is reproducible without file access or network; missing values never render as authoritative zero; absent headline does not promote a different window.

Tests: domain invariants, number/unit validation, state formatting, missing/unknown values, 0/100/over-limit rings, and view-model state transitions.

Main risks: smuggling provider schema into Domain, mistaking percentages for billing, presenting demo data as real.

Completion commit: `feat: add domain and mock scenarios (M04)`.

### M05: Polling, concurrency, and durable cache

Objective: establish scheduling and state guarantees before reading real credentials.

Areas: polling coordinator, state store, TimeProvider use, UI dispatcher, JSON settings/cache/backoff repositories.

- [x] Run one worker and one in-flight quota operation per provider; allow the two providers to run independently.
- [x] Use 60-second active and five-minute idle/unknown polling, with bounded coalesced manual/resume/source-change refreshes.
- [x] Implement 15-second HTTP attempt and 35-second total-operation budgets, linked cancellation, and one owned retry policy.
- [x] Persist 429 deadlines; local exponential delay starts at 60 seconds and caps at 15 minutes, while a longer server deadline always wins.
- [x] Add bounded transient retries, schema-failure cooldown, and clear next-attempt reporting.
- [x] Reject results with obsolete connection, credential, or account generations before UI publication and persistence.
- [x] Implement disconnect clearing, pause behavior, and zero reads for disabled providers.
- [x] Store sanitized last-good data atomically with schema versions and recovery behavior. Store no tokens or raw payloads.
- [x] Implement freshness, cached-startup labeling, reset-passed handling, and compact-value expiry exactly as defined in the design.
- [x] Marshal only normalized updates to the UI thread; bound/coalesce updates.
- [x] Cancel and await owned work at shutdown without synchronous UI-thread waits.

Acceptance: delayed requests cannot resurrect disconnected state; repeated triggers do not duplicate requests; restart preserves backoff; corrupted cache does not crash the tray app.

Tests: fake-time cadence, Retry-After date/seconds/zero/long values, source rotation, rapid off/on, pending-response shutdown, corrupted/old/future JSON schemas, disk-write failures, UI dispatch.

Main risks: hidden retry multiplication, unsafe stale account reuse, cache writes reversing disconnect, relaunch bypassing a penalty.

Completion commit: `feat: add polling and state persistence (M05)`.

### M06: OpenAI / Codex integration

Objective: fetch and display honestly scoped Codex quota from a supported native Windows source.

Areas: OpenAI adapter, source discovery, supported TOML parsing, credential-store bridge, auth/usage DTOs, HTTP mapping, fixtures.

- [x] Resolve explicit root, effective `CODEX_HOME`, then known user-profile `.codex`; source discovery is available to the M09 Settings surface but Settings itself is not scheduled until M09.
- [x] Verify the `file`, `keyring`, and `auto` rules against OpenAI Codex `storage.rs` inspected on 2026-09-07 and synthetic TOML fixtures.
- [x] Implement the exact Windows Credential Manager target derivation used by the documented current keyring mapping, without vault enumeration or stale-file preference.
- [x] Parse supported auth variants; tokens remain in memory; JWT expiry is a local unverified hint only.
- [x] Distinguish API-key and unsupported modes from ChatGPT-backed quota authentication.
- [x] Implement the isolated usage endpoint request with host allowlisting, disabled authenticated redirects, timeout, bounded body, and safe errors.
- [x] Parse main primary/secondary windows and variable durations; exclude unrelated quota categories from the headline.
- [x] Handle 401, 403, account changes, and one retry only when re-read credentials actually changed. Expiry remains service-confirmed, not a local JWT decision.
- [x] Verify the first live vertical slice: source -> request -> snapshot -> overlay -> details -> sanitized cache -> offline restart -> shutdown. `scripts/Test-LiveProviderSlice.ps1` passed every leg on 2026-09-07 against the signed-in Codex and Claude Code accounts.
- [x] Record targeted source behavior, supported storage modes, quota scope, and unresolved compatibility explicitly in this ledger.

Acceptance: a supported Windows account matches the owning tool's corresponding quota within retrieval timing; every unsupported mode explains itself; account changes do not show old readings as current. File-only success is not general OS-store compatibility.

Tests: synthetic auth/JWT/TOML fixtures, HTTP status and timeout matrix, monthly/weekly/null windows, source precedence, unchanged-token rejection, old-generation delayed response, redirect-host protection, redaction.

Main risks: unpublished API changes, wrong-account source selection, unverified credential-store formats, privilege or token leakage.

Completion commit: `feat: integrate Codex usage (M06)`.

### M07: Anthropic / Claude Code integration

Objective: fetch supported Claude account quota using the native Windows credential source.

Areas: Anthropic adapter, file discovery, credential parser, usage parser, recovery guidance, fixtures.

- [x] Resolve explicit root, effective `CLAUDE_CONFIG_DIR`, then user-profile `.claude`.
- [x] Validate `.credentials.json` and its supported Windows payload using synthetic or sanitized fixtures. Do not port macOS Keychain lookup or profile suffix rules blindly. File-only source, matching the documented Claude Code storage format; no macOS Keychain path exists in this codebase. Not verified against a real Windows-installed Claude Code credential file; see remaining issue below.
- [x] Support bounded shared file reads and atomic replacement/partial-write recovery. Shared read (`FileShare.ReadWrite`), bounded size, and a single retry-with-delay on `JsonException`/`IOException` recover a partial write; a still-invalid file after retry now fails as an honest unsupported-format error instead of an unhandled `JsonException` (fixed alongside this checkpoint).
- [x] Keep access-token leases in memory; never copy refresh tokens or run helper commands. The reader never parses or forwards `refreshToken`; the access token exists only as an in-memory `ClaudeCredential`.
- [x] Implement the isolated OAuth usage request and supported beta header with the same network safeguards as OpenAI. Fixed allowlisted endpoint, `AllowAutoRedirect = false`, 15-second timeout, and a 256 KiB bounded response, matching the Codex adapter.
- [x] Merge supported limits-array and named-window shapes; deduplicate; preserve the session headline and adapter-specific reset semantics.
- [x] Distinguish missing, expired, rejected, forbidden, and unsupported auth methods. Added an explicit `AuthenticationHint` on `ProviderReadException` (shared Application-layer addition, additive and opt-in; existing Codex behavior is unchanged because it never sets the hint): missing credential file, a server-confirmed 401 refined by a past local expiry hint into expired-versus-rejected, a 403 as access-denied, and unsupported credential/response shapes.
- [x] Start a new account epoch and clear old readings when changed credentials cannot be safely matched to the previous account. The polling worker now bumps `AccountGeneration` whenever a successful read's `AccountScope.Partition` differs from the previously published one (`StateContracts.cs`), which also benefits Codex. Claude's partition remains the access-token fingerprint because the credential file exposes no stable non-token account identifier; treating every token rotation as a new epoch is deliberately conservative so a stale reading is never shown as current, at the cost of also invalidating on a routine token refresh. Revisit if a live response is found to carry a stable account/organization id.
- [x] Validate expiry guidance and owner-managed renewal without writing provider state. A past local `expiresAt` only relabels a server-confirmed 401 as "session expired" for the UI; no token is refreshed, decoded for authorization, or written back by this application.
- [x] Record targeted Claude Code versions, source format, supported quota windows, and observed limits of compatibility. Verified on 2026-09-07 against Claude Code 2.1.263 on Windows 11 x64. The real `%USERPROFILE%\.claude\.credentials.json` carries a `claudeAiOauth` object holding `accessToken`, `refreshToken`, `expiresAt` and `refreshTokenExpiresAt` as 13-digit millisecond epochs, `scopes`, `subscriptionType`, and `rateLimitTier`, next to an unrelated `mcpOAuth` section that this adapter ignores. A live `GET /api/oauth/usage` returned both supported shapes at once: named `five_hour` and `seven_day` objects using `utilization` plus an ISO `resets_at`, and a `limits` array using `kind`, `percent`, `severity`, `is_active`, `scope` and `resets_at`. Observed kinds were `session`, `weekly_all`, and `weekly_scoped`, the last carrying `scope.model.display_name`; several unknown named windows and an `extra_usage`/`spend` section were present and are deliberately not read. Limits of compatibility: the credential file exposes no stable account or organization identifier, so the account partition stays a token fingerprint; unknown named windows are ignored rather than guessed at; and no `refreshToken` is ever parsed or sent.

Acceptance: supported readings agree with Claude's corresponding usage panel; token replacement is handled safely; a long Retry-After is never shortened; OpenAI remains operational when Claude fails.

Tests: credential-file missing/locked/truncated/replaced, milliseconds-versus-seconds expiry, merged window fixtures, duplicate/missing headline, ISO timestamps, 401/403/429/HTML/redirect failures, account-epoch invalidation.

Main risks: format drift, endpoint access changes, unsupported auth modes, ambiguous account identity.

Completion commit: `feat: integrate Claude usage (M07)`.

### M08: Local activity monitoring

Objective: add useful activity without overclaiming accuracy or reading conversation content.

Areas: provider activity readers, FileSystemWatcher service, process inspector, read-only SQLite support, activity aggregation.

- [x] Keep activity workers independent from quota workers and able to report unsupported capability. `ActivityCoordinator` owns its own worker per provider, separate from `PollingCoordinator`, and every reading carries an explicit `ActivityCapability`.
- [x] Probe Claude session-record support; use approximately 150 ms debounce and five-second process reconciliation. `<ClaudeRoot>\sessions` is probed for records this adapter recognizes, a `FileSystemWatcher` signals changes through a 150 ms debounce, and the reconciliation cadence is five seconds when nothing is happening.
- [x] Validate PID plus creation time where available; map access denial and unknown status to uncertainty. The record's `procStart` FILETIME is compared with the live process creation time; a missing or unreadable creation time is uncertainty rather than a match, access-denied inspection downgrades the reading to an estimate, and an unrecognized status maps to unknown.
- [x] Probe only approved Codex paths and known schemas before database queries. Only `<CodexRoot>\state_5.sqlite` is opened, and only after `threads` is confirmed to expose `id`, `rollout_path`, and `updated_at`.
- [x] Read external SQLite databases read-only with short waits, bounded results, and live WAL awareness. Do not apply a generic immutable fallback. The connection is `Mode=ReadOnly` with a private cache and a 250 ms busy timeout, the query is capped at eight rows, and no immutable flag is used, which a test confirms by observing a write made through the owning connection in WAL mode.
- [x] Constrain record-supplied paths to approved local roots. Do not read rollout content merely to infer activity. A rollout path is followed only when it resolves inside the Codex root, and only its last-write metadata is read.
- [x] Use two-second active metadata scans and an eight-second expiry as the initial Codex heuristic; label it estimated. Codex activity is `ReadingFidelity.Derived` and the overlay renders it with an explicit estimated label.
- [x] Never infer waiting from silence or reliable idle from an unsupported source. Codex only ever reports working or no session, an unsupported source reports unavailable rather than idle, and the aggregator lets waiting win only when a source actually reported it.
- [x] Handle missing directories, watcher overflow, process exit, owner shutdown, and schema changes without breaking quota. Each of these produces an honest unavailable or empty activity reading; a watcher error requests a full pass; and a monitor that throws is reported as unavailable activity with no message of its own, leaving the quota state untouched.
- [x] Stop activity workers on pause/disconnect and suspend unnecessary work while locked. Pausing stops observation, disconnecting stops the worker and clears the reading, and a session lock pauses both the activity and quota coordinators until the session returns.

Acceptance: recognized Claude working/waiting states update promptly; estimates expire; unsupported formats remain useful unavailable states; monitoring is bounded and read-only.

Tests: session records, PID reuse, access denial, partial writes, duplicate/lost events, future timestamps, stale boundaries, missing schema, live WAL changes, locks, and bounded path traversal.

Main risks: false status, unsafe database access, unbounded scans, private titles or paths appearing in the UI or logs.

Completion commit: `feat: add conservative activity monitoring (M08)`.

### M09: Settings, security, and startup

Objective: make connection, recovery, placement, and privacy manageable without surprises.

Areas: settings/status windows, provider configuration, JSON migration, known folders, DPAPI service, startup integration, diagnostics.

- [x] Implement Status, Providers, Appearance, General, Privacy, and About navigation with aligned compact controls. The settings window uses a compact left navigation column with aligned rows and dividers rather than oversized cards.
- [x] Show the selected credential source, supported quota scope, authentication state, and safe recovery action for each provider. The resolved root is shown rather than assumed, because a terminal's environment can differ from the one a tray application inherits.
- [x] Add enabled/disabled, pause, manual refresh, clear-cache, and reconnect behavior with accurate command labels. Manual refresh is rate limited to one attempt per 15 seconds and the button says how long the wait is; disabling a provider disconnects it rather than leaving a stale reading on screen.
- [x] Add monitor, edge, offset, pin, visibility, UI scale, reduced-motion, and full-screen visibility preferences. Each is validated on load and on save, so one bad value falls back to its default instead of discarding the document.
- [x] Add opt-in per-user Startup shortcut registration; read back actual registration state and handle failure honestly. Registration writes only the current user's Run key, the state is always read back from the registry, and a registration pointing at another location is reported as such rather than shown as this installation's.
- [x] Use known local folders, atomic writes, migration/recovery, and appropriate ACLs for app-owned data. Paths resolve through the known local application data folder, settings are written through a temporary file and an atomic replace, an older schema is migrated, an unreadable document is preserved as `settings.json.invalid` beside recovered defaults, and the secrets directory is created with inheritance removed and access granted only to the current user.
- [x] Implement current-user DPAPI for any app-owned partition secret; no borrowed token persistence. `DpapiSecretStore` protects with `DataProtectionScope.CurrentUser`, and a test proves a machine-scope reader cannot open the result. No borrowed provider token is ever passed to this store.
- [x] Add sanitized diagnostics and bounded logs; provide no raw token/body dump option. Diagnostics are off until the user enables them, every line is stripped of tokens, JWTs, bearer headers, email addresses, and local paths, newlines cannot forge entries, lines are truncated, and the file is capped with at most one rolled-over predecessor. No code path anywhere writes a raw token or response body.
- [x] Verify all settings and security behavior under a standard user account. All checks in this milestone ran non-elevated, confirmed by the session's own elevation check, including the registry registration, the DPAPI round trip, the restricted-directory ACL, and a full application run.

Acceptance: valid settings survive restart; disconnect stops file/network access; clear-data scope is limited to UseNotch; launch-at-login is opt-in; recovery guidance never silently modifies an owning tool.

Tests: view models, settings migration, known-folder/path validation, DPAPI round trip, isolated cross-user protection check, log-secret assertions, registration add/remove, restart and disconnect races.

Main risks: normal config becoming a secret store, misleading sign-out behavior, startup pointing at a development path, partial persistence failure.

Completion commit: `feat: add settings and privacy controls (M09)`.

### M10: Overlay polish and accessibility

Objective: complete a stable, dense, everyday utility interface.

Areas: styles/resources, provider ring/bar controls, details view, expansion state machine, accessible status page.

- [x] Apply the restrained palette, typography, spacing, sizes, and contrast rules from the design. The overlay uses the documented surface, text, divider, focus, and status tokens, a monospaced numeric face, the 4 DIP spacing scale, and the suggested handle, cell, and detail sizes.
- [x] Implement Hidden, Collapsed, Expanded, ProviderDetailsOpen, and Pinned states with bounded hover delays. `OverlayPresentation` is a pure state machine with its own tests, and hover is delayed on both sides so neither a crossing pointer nor a pointer moving between cells can flicker the surface.
- [x] Keep native regions synchronized with visible geometry. Do not introduce invisible hover-capture padding. Regions are rebuilt whenever the presentation state changes, only visible controls contribute, and a headless test asserts the collapsed handle's region sits where the handle is drawn rather than at the window origin. The window now reports device-independent regions and the platform converts them with the window's real client size, so a mismatched render scaling can no longer place a region outside the window.
- [x] Show all authentication, quota, stale, loading, unavailable, error, working, waiting, and unknown states clearly. Each authentication problem reads as itself instead of as a quota value, and every state is covered by a test.
- [x] Keep provider color separate from status severity and include readable scope/freshness text. Severity drives the ring colour while the provider is identified by its label and automation name, and every cell carries scope and freshness text.
- [x] Implement supported rings at 0%, 100%, over-limit, and no-reading; keep numerical and graphical meaning consistent. `QuotaRing` is purpose-built rather than a scaled-down hardware gauge: a zero reading draws no arc, a missing reading draws only the track, and a full and an over-limit reading are deliberately identical while the text keeps them apart.
- [x] Bound details to the work area; preserve pointer transitions without flicker or focus changes. The detail panel is width- and height-bounded and scrolls, and leaving the overlay never closes an open detail panel.
- [x] Provide automation names, visible focus, keyboard-accessible status/settings equivalents, larger UI scale, high contrast, and reduced motion. Cells carry automation names containing provider, scope, value, and freshness, focus-visible styles use the documented focus token, the settings window offers the same data by keyboard, and UI scale and reduced motion are applied from settings.
- [x] Stop hidden animations and unnecessary presentation timers. Busy motion requires fresh working activity, a visible overlay, and no reduced-motion preference; the hover timer is stopped on shutdown.

Acceptance: no clipped labels or inaccessible actions at supported text scales; no hover flicker; no focus/input regression; every compact number has a named meaning in details.

Tests: Avalonia headless controls and view models, pure layout tests, real input/focus regression, keyboard/Narrator, contrast inspection, high contrast, reduced motion, and 200% display scale.

Main risks: visual polish breaking hit regions, inaccessible non-activating surface, decorative animation cost, low-contrast secondary text.

Completion commit: `feat: polish overlay and accessibility (M10)`.

### M11: MVP cache and history boundary

Objective: close persistence gaps while keeping optional charts out of the first release.

Areas: last-good cache, cache/privacy UI, freshness policy, decision log; no production history database required.

- [x] Verify offline restart, expired tokens, missing sources, reset-passed windows, and 24-hour compact-reading expiry. Each is covered by a test, and the offline restart was also confirmed live in the M06 vertical slice.
- [x] Verify old-account data cannot return after source change, disconnect, cache restoration, or a delayed save. A cached reading whose source identifier does not match the current connection is now discarded instead of shown, disconnecting clears both the state and the cache, and disconnect awaits the worker before clearing so a late save cannot resurrect a reading.
- [x] Confirm last-good persistence stores normalized quota only, without activity text or account secrets. The cache now strips activity when writing and when loading, which fixed a real gap: a state carrying activity would previously have written its session label to disk. Only the opaque account partition, which section 7 allows, is stored.
- [x] Confirm the production build functions without creating a history database or loading a chart library. A test asserts no chart package is referenced and no source mentions a history database or repository, and a full application run left only the two sanitized JSON cache files under the application data root.
- [x] Record the explicit H01 history deferral and keep H01 unchecked. See the decisions log entry dated 2026-09-07; H01 remains unchecked in the tracker.
- [x] Keep any history interface minimal and optional; remove unused speculative implementation if it adds maintenance cost. No history interface or implementation exists, so there was nothing speculative to remove and nothing to maintain.

Acceptance: the first release has reliable recovery with JSON only; no hidden historical retention; history remains clearly post-MVP.

Tests: cache corruption and future versions, account partition mismatch, old and reset-passed data, failed write, disconnect/save race, no unexpected SQLite history files.

Main risks: interpreting cache as current data, resurrected account state, optional history delaying MVP.

Completion commit: `fix: finalize cache recovery rules (M11)`.

### M12: Packaging and pipeline

Objective: produce repeatable, installable Windows artifacts without publishing them yet.

Areas: `installer/UseNotch.wxs`, `installer/build-installer.ps1`, tool pinning, CI/release workflows, version metadata.

- [x] Adapt the modshell-cs self-contained publish/harvest pattern using a dedicated UseNotch product identity and stable new UpgradeCode. The publish is self-contained; WiX v5 globs the published output with its `Files` element, so no separate harvest step can drift from it. The UpgradeCode is new and belongs to UseNotch alone.
- [x] Implement a per-user install under LocalAppData with consistent per-user shortcuts and registration; do not copy mixed per-machine component assumptions. The package is `Scope="perUser"`, installs under `LocalAppDataFolder`, and its shortcut and registry values are all per user. A test rejects any Program Files or per-machine authoring.
- [x] Pin WiX v5 and verify architecture/version constraints. Keep installer ProductVersion valid for MSI; map prerelease labels separately if needed. WiX 5.0.2 is pinned in `.config/dotnet-tools.json`, the MSI is built for x64, and the version is validated as major.minor.patch within MSI field ranges. A prerelease label belongs to the release tag, not the ProductVersion.
- [x] Verify resolved output/cleanup paths stay inside intended workspace directories before recursive cleanup in build scripts. `Assert-InsideWorkspace` resolves a path first and refuses it unless it is inside the repository, and no recursive delete runs without passing through it.
- [x] Make builds stop on publish, test, packaging, or signing failures rather than uploading partial output as success. The script sets a stopping error preference and checks the exit code of every step, and the packaging workflow validates the MSI before the upload step runs.
- [x] Add pull-request restore/build/test/publish/package validation and bounded artifact retention. `.github/workflows/package.yml` builds and validates the installer for every pull request, uploads it with a 14-day retention, and fails when no artifact is produced.
- [x] Configure least-privilege release workflow permissions and safe secret handling. Do not expose signing credentials to untrusted PRs. The release workflow triggers only on a pushed tag, never on a pull request, defaults to read-only permissions and raises them only for the release job, and the packaging workflow references no secret at all. A test enforces each of these.
- [x] Implement version agreement between application, installer, and eventual release tag without creating or pushing a tag now. The installer takes its version from the build rather than a copy, and `scripts/Test-VersionAgreement.ps1` checks the application, installer, and an optional tag agree. No tag was created or pushed.
- [x] Define restart/locked-file behavior and uninstall data retention, including removal of startup registration owned by UseNotch. Restart Manager is asked to close a running UseNotch rather than requiring a reboot, uninstall removes the launch-at-login value UseNotch owns, and the user's settings and cache are deliberately retained. All of this is documented in `installer/README.md`.
- [ ] Validate clean install, upgrade, downgrade rejection, and uninstall in disposable Windows environments.

Acceptance: a clean checkout creates the MSI; standard-user install works without a separately installed runtime; N-1 upgrade preserves settings; uninstall removes installed files and owned startup integration as designed.

Tests: clean-VM installer matrix, missing runtime, running app upgrade, invalid version, downgrade, uninstall/reinstall, and interrupted upgrade. Do not install into the user's daily environment just to complete this gate.

Main risks: unstable MSI identity, wrong install scope, unsafe output cleanup, locks, release workflow publishing untested artifacts.

Completion commit: `build: add MSI and pipeline validation (M12)`.

### M13: End-to-end and resource validation

Objective: establish evidence for the complete product under realistic failures and Windows configurations.

Areas: all test projects, interactive Windows harness, sanitized performance evidence, provider compatibility notes in this ledger.

- [ ] Run the complete unit, view-model, parser, HTTP, credential, file, and SQLite fixture suites.
- [ ] Run targeted real-account validation in an authorized local environment; record only sanitized results, never credentials or raw responses.
- [ ] Exercise disconnect/reconnect, account switching, offline recovery, long 429, schema failure, and shutdown during pending operations.
- [ ] Test 100%, 125%, 150%, 175%, and 200% scaling and mixed-DPI monitors, including negative coordinates and portrait orientation.
- [ ] Test primary-monitor change, docking, taskbar auto-hide, resolution change, sleep/resume, lock/unlock, and RDP transition.
- [ ] Test Explorer restart, hidden overlay, duplicate launch, and Quit with no orphan process.
- [ ] Recheck focus and input regions against an independent process using the selected renderer and a supported fallback renderer.
- [ ] Run an eight-hour soak and capture idle CPU, working set, handle/task counts, request rate, and disk growth on a named reference machine.
- [ ] Investigate deviations from the design's resource targets; record measured values and any accepted limitation explicitly.
- [ ] Close discovered release-blocking defects with focused fixes and rerun affected checks, not blindly every unrelated suite.

Acceptance: required Windows behaviors pass; both providers recover independently; no sustained resource growth; actual measured results support low-resource claims.

Tests: full design section 10 checklist and the scenarios above. Unavailable interactive hardware or accounts leave corresponding checks open with a precise external requirement.

Main risks: CI success mistaken for real desktop validation, account-specific gaps, sleep races, slow leaks, renderer-specific faults.

Completion commit: `test: validate end-to-end Windows flows (M13)`.

### M14: Release hardening and release candidate

Objective: produce a validated, trustworthy release candidate. This milestone does not itself authorize public publication.

Areas: app/package signing, release workflow, update checking, installer recovery, runtime/dependency versions, diagnostics.

- [ ] Confirm the runtime and dependencies remain supported for the intended shipping date. Complete the required LTS migration if applicable, then rerun affected build/native/package gates.
- [ ] Audit production output for development fixtures, secrets, demo switches, excessive logging, and unintended dependencies.
- [ ] Sign application binaries before packaging, then sign and timestamp the MSI using authorized signing infrastructure.
- [ ] Verify publisher/signature and test the actual signed artifact on a clean VM. Missing signing access is a blocker, not a passed check.
- [ ] Implement explicit update checking with trusted origins and meaningful version information; no unsigned self-update or automatic installation in MVP.
- [ ] If the app downloads an installer, verify expected origin and publisher before offering launch; reject tampering or unexpected redirects.
- [ ] Validate interrupted upgrade, locked files, migration failure, and recovery to a usable state.
- [ ] Verify source notices and support information, and remove all attribution forbidden by the project instructions.
- [ ] Record the release-candidate artifact identity, version, signature verification, test environment, and remaining non-blocking limitations.
- [ ] Confirm every required master milestone M00-M13 is complete and no release-blocking issue remains.

Acceptance: signed, supported, standard-user release candidate passes clean install and upgrade tests; recovery and privacy are verified; publication remains a separate action.

Tests: actual signed MSI install/upgrade, signature/publisher checks, tampered-download rejection, runtime/dependency audit, sanitized output inspection, final targeted smoke test.

Main risks: signing unavailable, untrusted updater, unsupported .NET runtime, final package differing from the tested build.

Completion commit: `chore: harden release candidate (M14)`.

## 5. Deferred follow-on work

### H01: Optional persistent history

Prerequisite: M14 complete and history explicitly selected as the next product scope. Do not start merely because its checkbox exists.

Areas: history repository, optional SQLite schema and migrations, opt-in privacy settings, chart presentation.

- [ ] Reconfirm the user need, retention period, and resource/privacy budget.
- [ ] Implement the minimal normalized schema from design section 7; never store tokens, raw responses, titles, or prompts.
- [ ] Partition by provider/account/window; persist accepted readings only, at most once per minute per window.
- [ ] Add seven-day default retention, storage cap, clear-history command, and safe migration/recovery.
- [ ] Show chart gaps and reset boundaries without implying billing or interpolating unknown usage as fact.
- [ ] Reuse a small native drawing control where sufficient; add LiveCharts only if its measured benefit justifies the dependency.
- [ ] Verify disabled history performs no history writes and does not create a database.
- [ ] Run schema, migration, retention, partition, reset, disk-full, corruption, and resource tests.

Acceptance: opt-in history is accurate, bounded, removable, and independent of current quota availability.

Main risks: privacy retention, misleading charts, schema complexity, optional feature regressing idle resource use.

Completion commit: `feat: add optional quota history (H01)`.

### R01: Authorized release publication

Prerequisite: M14 complete and explicit authorization to publish to the intended repository/channel. User Git setup does not automatically authorize remote publication.

Status note, 2026-09-08: `v0.1.1` was published as a prerelease on explicit owner authorization while M14 was still open, because drafts were not reachable by the owner in the GitHub UI. That publication does not satisfy this gate. The artifact is unsigned and untested against the install matrix, both stated in the release notes, so every box below stays unchecked until a signed candidate is published and verified.

- [ ] Confirm target repository, version, channel, and publication authorization.
- [ ] Confirm the intended release commit is the validated candidate and the working tree does not hide uncommitted release changes.
- [ ] Prepare a clear release description and the verified artifacts without prohibited attribution or secrets.
- [ ] Use the authorized tag/workflow path and monitor it through completion.
- [ ] Verify the published version, asset checksums/signatures, and download links against the candidate evidence.
- [ ] Record the release tag, commit, URL, verification, and support limitations in the evidence log.
- [ ] If publication fails, record the failure and recover without overwriting an already distributed same-version artifact.

Acceptance: the authorized release is downloadable and matches the tested signed candidate. This gate stays unchecked until publication and verification actually finish.

Checkpoint: release tag plus its source commit and a follow-up ledger commit such as `docs: record verified release (R01)`; no source changes are required merely to manufacture a release commit.

## 6. Completion and handoff checklist

Use this checklist at the end of each work session. These are session checks; reset them when a new session starts rather than presenting an old completed handoff as current.

- [x] Session state names the current milestone and last validated checkpoint.
- [x] Individual tasks reflect actual work; incomplete or unverified tasks remain unchecked.
- [x] Evidence log records actual checks, results, environment, and any limitations.
- [x] Changes are committed at a completed milestone or explicitly described as partial/uncommitted.
- [x] No secrets, private provider content, forbidden attribution, or em dashes were introduced.
- [x] Other contributors' edits are preserved and not misrepresented as this session's completed work.
- [x] Running processes and disposable test environments are recorded or stopped appropriately.
- [x] The next action is concrete enough for a new implementer to execute without reconstructing the conversation.
- [x] Any blocker states exactly what is missing and which checks remain open.

The first coding task is M01 after the user's Git setup and M00 baseline. The first technical risk gate is M03. The first live vertical slice is M06. Do not trade these checkpoints for a large unverified final implementation.
