# Codenotch Windows Port Brief

Source reviewed: https://github.com/vinzdg/codenotch.git  
Local clone: `C:\Users\Noel Hermann\Projects\UseNotch\.tmp\codenotch`  
Reviewed commit: `743601acd69e701131602b88082fcaeee0c2e88b` (`Codenotch 1.5.0`)

## What Codenotch Is

Codenotch is a native macOS ambient status app for coding assistant usage limits. It draws a small black "notch" on a screen edge and shows one ring per connected assistant provider. Each ring communicates quota usage at a glance and can also show whether that assistant is currently working, idle, or waiting for user input.

The app is not a prompt proxy and does not send messages to assistants. Its job is strictly observability:

- Read usage data from credentials and local state already owned by installed coding tools.
- Poll provider usage endpoints carefully and cache the last good reading.
- Watch local app or CLI session state to infer live activity.
- Render a low-friction overlay that stays out of the way.
- Provide settings for integrations, placement, visibility, app presence, launch at login, and updates.

The README is explicit about the product principle: Codenotch does not sign in anywhere itself. It borrows existing local sessions from tools like Claude Code, Cursor, Codex, Antigravity, GLM, Grok, and OpenCode.

## User Experience

The UI has three main surfaces:

- Edge notch: a black rounded shape attached to one of the four screen edges.
- Provider rings: one cell per provider, showing either percent used, count used, remaining count, or a dash when no honest reading exists.
- Tooltip card: shown on hover, with detailed limit windows, reset times, block messages, and live sessions.

On macOS, the notch can sit on the right, left, top, or bottom edge. It normally appears as a small resting pill, expands on hover, and can be pinned open or always shown. It has a settings orb attached to the notch shape and a right-click menu for refresh, pinning, sign-in actions, and quit.

Activity states are layered inside provider rings:

- Busy: a thin inner arc spins while a local session appears active.
- Waiting: a pulsing amber indicator shows when the assistant needs user input.
- Idle: no activity indicator.

The tooltip combines two kinds of information:

- Usage windows, such as current session, weekly, monthly, or provider-specific quotas.
- Live sessions, such as a Claude Code session, Cursor composer, Codex thread, or Grok run.

## Current Tech Stack

Codenotch is a native macOS app:

- Swift 5
- SwiftUI for views
- AppKit for windows, panels, menu handling, screen geometry, cursor tracking, keychain, app activation policy, running app discovery, and launch-at-login
- XcodeGen via `project.yml`
- Sparkle for signed automatic updates
- XCTest unit tests
- SQLite C API for reading local state databases

Build commands in the source repo:

- `make gen`: generates `Codenotch.xcodeproj`
- `make build`: debug build
- `make test`: unit tests
- `make run`: build and launch
- `make release`: maintainer-only signed, notarized, Sparkle-enabled release

## High-Level Architecture

The app has these main areas:

- `Sources/App`: lifecycle, app delegate, updater, status item, logging.
- `Sources/Notch`: overlay window, shape, geometry, layout math, view model, root view.
- `Sources/Features`: visual pieces such as provider rings, tooltip cards, settings handle.
- `Sources/Model`: provider snapshots, limit windows, status, fidelity, polling store, archive, copy formatting.
- `Sources/Providers`: usage providers, credential readers, endpoint parsers, SQLite helpers, keychain wrappers.
- `Sources/Sessions`: activity monitors for local assistant sessions.
- `Sources/Settings`: preferences, settings window, app presence, release notes, visibility.
- `Tests`: focused unit tests for parsers, layout, geometry, provider behavior, session monitors, and copy.

The central runtime flow is:

1. `CodenotchMain` starts a SwiftUI app with an `AppDelegate`.
2. `AppDelegate.applicationDidFinishLaunching` creates `NotchWindowController`.
3. It migrates preferences, creates providers, creates `UsageStore`, sets up settings, updater, and status item.
4. `UsageStore` publishes provider snapshots into `NotchViewModel`.
5. Activity monitors publish live sessions into the same view model.
6. `NotchWindowController` owns the floating panel and cursor tracking.
7. `NotchRootView` renders the notch, rings, settings orb, and tooltip from the view model.

## Data Model

The usage model is intentionally provider-neutral.

`UsageProvider` is the protocol every provider implements:

- `id`
- `displayName`
- `glyph`
- `fetchSnapshot()`
- `account()`
- `signInRoute`
- `signOut()`
- `presentSignIn()`
- `forgetCachedCredential()`

`ProviderSnapshot` is what the UI consumes:

- Provider identity and glyph.
- `Fidelity`: `official`, `derived`, or `manual`.
- `ProviderStatus`: `ok`, `stale`, `needsAuth`, `accessDenied`, `unsupported`, or `error`.
- Limit windows.
- Optional `headlineID` selecting which window appears in the ring.
- Optional `UsageBlock` for a hard provider-side block even when the headline usage still looks available.

`LimitWindow` supports multiple reporting shapes:

- `usedFraction`: a true percentage, used to draw progress arcs and bars.
- `remaining`: when the provider reports only a remaining count.
- `used`: when the provider reports only a used count.
- `resetsAt`: optional because some providers do not publish reset timing.

This matters for a Windows port: the rendering layer should not assume every provider can produce a percent. A dash, count, stale value, or unsupported message is a first-class state.

## Polling And Caching

`UsageStore` is the central polling object. It is a `@MainActor ObservableObject`, not an actor, because its primary job is to publish into SwiftUI. The provider implementations are actors where they need async isolation.

Key behavior:

- Polls every 60 seconds while any assistant appears busy.
- Polls every 5 minutes when idle.
- Refreshes on machine wake.
- Lets the user refresh all providers or a single provider.
- Serially fetches live providers to reduce endpoint pressure.
- Tracks in-flight refreshes for UI spinners.
- Filters out disconnected providers before reading credentials.
- Invalidates pending responses when a provider is toggled off or signed out.
- Saves last-good readings in `UsageArchive`.
- Saves provider-specific backoff deadlines for rate limits.

Failure handling is deliberately conservative:

- It never invents usage.
- `needsAuth` and `unsupported` supersede old readings.
- `rateLimited` and expired credentials generally degrade to stale readings.
- `accessDenied` is tracked separately so Settings can offer an "Allow access" retry.

## Provider Implementations

### Claude Code

Provider: `ClaudeOAuthProvider`

Current behavior:

- Discovers Claude profiles under `~/.claude` and `~/.claude-<slug>`.
- Reads OAuth credentials from the macOS login keychain.
- Calls `GET https://api.anthropic.com/api/oauth/usage`.
- Uses Anthropic's `oauth-2025-04-20` beta header.
- Parses session and weekly windows.
- Uses the `session` window as the headline.
- Backs off on HTTP 429: starts at 60 seconds, doubles, caps at 15 minutes, persists across launches.

Important implementation details:

- It reads keychain item metadata first to avoid unnecessary prompts.
- It picks the newest duplicate keychain item because Claude Code may leave old items behind.
- It caches credentials until the keychain item changes.
- Expired tokens are not treated as full sign-out because Claude Code owns refresh.

Windows implications:

- macOS Keychain must be replaced with Windows Credential Manager, DPAPI-protected files, or direct parsing of Claude Code's Windows auth storage if Claude Code uses files there.
- Claude profile discovery needs Windows home path conventions.
- Process/session monitoring must replace macOS file event and process APIs.

### Cursor

Provider: `CursorLocalProvider`

Current behavior:

- Reads Cursor's own VS Code-derived SQLite state database:
  `~/Library/Application Support/Cursor/User/globalStorage/state.vscdb`
- Extracts `cursorAuth/accessToken` and `cursorAuth/stripeMembershipAuthId`.
- Builds a `WorkosCursorSessionToken=<account>::<token>` cookie.
- Calls `GET https://cursor.com/api/usage-summary`.
- Parses included usage and account details.

Important implementation details:

- The SQLite database is opened read-only but not immutable because Cursor uses WAL mode.
- The app reads Cursor's editor session, not a browser session, to avoid accidentally reading a different account.

Windows implications:

- Cursor state is likely under `%APPDATA%\Cursor\User\globalStorage\state.vscdb`.
- WAL handling remains important.
- Launch detection must use Windows process enumeration rather than `NSWorkspace`.

### Codex

Provider: `CodexLocalProvider`

Current behavior:

- Reads `~/.codex/auth.json`.
- Extracts `access_token`, `account_id`, and account labels from JWT claims.
- Calls `GET https://chatgpt.com/backend-api/wham/usage`.
- Sends `Authorization: Bearer <token>` and `ChatGPT-Account-Id: <account id>`.
- Parses primary and secondary usage windows from `rate_limit`.
- Labels windows from their duration, such as hourly, weekly, or monthly.
- Backs off on HTTP 429 and persists the deadline.

The older task history mentions Codex usage being read from rollout logs. The current source code now uses the ChatGPT usage endpoint for quota and still uses local Codex databases for activity monitoring.

Windows implications:

- Auth file location is likely `%USERPROFILE%\.codex\auth.json`, but this must be verified.
- Local Codex SQLite paths may differ.
- Any Windows port should keep endpoint and parser code isolated because this is an internal endpoint and can change.

### Antigravity

Provider: `AntigravityProvider`

Current behavior:

- Reads Antigravity credentials.
- Calls Google's Cloud Code Assist endpoint:
  `POST https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist`
- Tries Antigravity's local language server first for quota.
- Falls back to Google's quota endpoint where licensed:
  `POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary`
- Falls back again to a derived request count when no official quota is available.

Important implementation details:

- The provider ID remains `gemini` for archive and settings continuity.
- Localhost TLS trust is customized for the local language server.
- The derived fallback is explicitly marked as `derived`.

Windows implications:

- Credential storage and local language server discovery must be reimplemented.
- The current macOS discovery appears process and port based, with helper shell scripts.
- Windows needs process inspection and port discovery equivalents.

### GLM

Provider: `GLMProvider`

Current behavior:

- Reads a Z.ai GLM Coding Plan key from existing coding tools, such as Claude Code settings, ZCode, or OpenCode.
- Calls `<baseURL>/api/monitor/usage/quota/limit`.
- Sends the raw key in the `Authorization` header, without a `Bearer` prefix.
- Parses session and plan windows.
- Backs off on HTTP 429 with the same persistent exponential pattern.

Windows implications:

- Credential search paths need Windows equivalents for all source tools.
- Endpoint behavior can stay mostly cross-platform once credentials are found.

### Grok

Provider: `GrokLocalProvider`

Current behavior:

- Reads `~/.grok/auth.json`.
- Selects a trusted xAI issuer entry.
- Calls `GET https://cli-chat-proxy.grok.com/v1/billing?format=credits`.
- Sends `Authorization: Bearer <token>` and `X-XAI-Token-Auth: xai-grok-cli`.
- Shows weekly Grok Build credits.

Windows implications:

- Auth file is likely `%USERPROFILE%\.grok\auth.json`.
- Parsing and endpoint code should be portable.

### OpenCode

Provider: `OpenCodeProvider`

Current behavior:

- Reads `~/.local/share/opencode/auth.json`.
- Looks specifically for the `opencode-go` key.
- Calls OpenCode Go usage endpoint via `OpenCodeUsage.endpoint`.
- Treats 401 as no readable Go plan, 403 as a valid key without Go entitlement.
- Backs off on HTTP 429.

Windows implications:

- OpenCode's auth path may differ, likely under `%LOCALAPPDATA%` or `%APPDATA%`.
- Provider should support multiple known locations.

## Activity Monitoring

Activity monitoring is separate from usage polling. This is a major architectural point worth preserving.

`AgentActivityMonitor` publishes `[AgentSession]`, where each session has:

- `id`
- `name`
- `detail`
- `state`: busy, waiting, idle
- `waitingFor`
- `since`

Monitors included:

- `ClaudeSessionMonitor`: watches `~/.claude/sessions` JSON files and validates process liveness.
- `CursorActivityMonitor`: reads Cursor `composerHeaders` rows from SQLite.
- `CodexActivityMonitor`: combines rollout file modification times and desktop app catalog timestamps.
- `AntigravityActivityMonitor`: watches Antigravity local activity.
- `GrokActivityMonitor`: watches Grok activity.

Examples:

- Claude Code has session files and process IDs, so it can be fairly direct.
- Cursor has composer flags such as `unfinishedRunAt`, `hasBlockingPendingActions`, and `hasPendingPlan`.
- Codex has no direct status field, so the monitor uses recent writes as a heuristic and stops reporting busy after 8 seconds.

Windows implications:

- macOS `NSWorkspace.runningApplications`, `DispatchSourceFileSystemObject`, and POSIX process checks need replacement.
- Candidate Windows APIs:
  - .NET `Process` APIs or Win32 Toolhelp/WMI for process liveness.
  - `ReadDirectoryChangesW` or higher-level file watchers.
  - SQLite read-only access with WAL support.
  - File modification time heuristics for tools without explicit status.

## Overlay And Windowing

The macOS overlay is built from:

- `NotchPanel`: a borderless non-activating `NSPanel`.
- `NotchWindowController`: placement, hit regions, cursor tracking, right-click menu, click handling, timers.
- `NotchGeometry`: computes screen-edge panel frames using `NSScreen.frame`, `visibleFrame`, safe areas, and hardware notch data.
- `NotchPlacement`: maps orientation-agnostic layout space to screen coordinates.
- `SideNotchShape`: draws the black notch shape.
- `NotchRootView`: SwiftUI rendering.

The panel is configured to:

- Float at status bar level.
- Join all spaces.
- Appear in full-screen spaces.
- Avoid activating the app when clicked.
- Be transparent outside the notch and tooltip regions.
- Ignore mouse events outside active hit regions.

Windows implications:

- This is the hardest platform replacement.
- Need an always-on-top, transparent, borderless overlay window.
- Need click-through outside custom hit regions.
- Need non-activating interaction, or at least no focus theft in normal use.
- Need multi-monitor placement based on work area versus full monitor bounds.
- Need reliable behavior over full-screen apps where Windows allows it.

Candidate Windows approaches:

- Native .NET/WPF or WinUI 3 with Win32 interop:
  - Layered window (`WS_EX_LAYERED`)
  - Transparent hit testing (`WS_EX_TRANSPARENT` where appropriate)
  - Topmost window
  - No-activate extended style (`WS_EX_NOACTIVATE`)
  - Custom region or manual hit testing
- Tauri with a Rust backend and native window plugins:
  - Good for cross-platform UI, but custom click-through and no-activate overlay behavior may still need Win32 code.
- Electron:
  - Faster UI iteration, but heavier and less native.
  - Transparent always-on-top click-through windows are possible, but fine-grained mixed hit regions require careful handling.

For a Windows-first clone, native .NET plus Win32 interop is likely the most direct route for overlay quality.

## Settings And Preferences

Preferences are stored in `UserDefaults` on macOS:

- Disconnected providers.
- Notch visibility.
- Notch edge.
- App presence: Dock, menu bar, or neither.
- Last seen version for What's New.
- Launch at login.

Settings allows:

- Connecting or disconnecting providers.
- Opening the owning app or management URL.
- Retrying keychain access when permission was denied.
- Changing notch visibility and edge.
- Changing where the app itself appears.
- Enabling launch at login.
- Enabling automatic updates.
- Erasing app data.

Windows implications:

- Replace `UserDefaults` with JSON config, SQLite, Registry, or application settings.
- Replace launch-at-login with Startup folder shortcut or Registry Run key.
- Replace Dock/menu bar presence with tray icon behavior.
- Replace Sparkle settings with the chosen Windows update framework.

## Update And Distribution

macOS uses Sparkle with:

- EdDSA-signed appcast.
- Daily checks.
- Background install.
- Developer ID signing and notarization for releases.

Windows needs a different release pipeline:

- MSIX, Squirrel.Windows, Velopack, WinSparkle, or a custom updater.
- Code signing certificate.
- Installer or portable distribution decision.
- Auto-update trust and rollback behavior.

If using .NET, Velopack or MSIX are plausible starting points. If using Tauri, use Tauri's updater and signing model.

## Test Coverage

The repo has broad focused unit tests. Important test areas:

- Provider response parsing: Claude, Codex, Cursor, GLM, Grok, OpenCode, Antigravity.
- Usage summary and reset copy.
- Status degradation.
- Rate-limit backoff.
- Provider disconnection.
- Credential/profile handling.
- Notch geometry, placement, edge behavior, and layout math.
- Tooltip rendering logic.
- Activity session parsing.

For the Windows port, preserve these categories even if the UI framework changes. Provider parsers and activity monitors should be testable without starting the overlay.

## What Is Portable

These concepts can transfer almost directly:

- Provider abstraction.
- `ProviderSnapshot`, `LimitWindow`, status, and fidelity model.
- Last-good reading archive.
- Polling cadence and rate-limit backoff.
- Parser tests with recorded responses.
- Separation between usage polling and activity monitoring.
- User-facing rule that no number is invented.
- Settings model for connected providers, visibility, edge, startup, and updates.
- Most endpoint request construction after credentials are available.

## What Must Be Rebuilt For Windows

These pieces are macOS-specific and need Windows-native equivalents:

- AppKit `NSPanel` overlay.
- SwiftUI/AppKit rendering and event model.
- `NSScreen` geometry and hardware notch handling.
- macOS Keychain access.
- `UserDefaults`.
- `NSWorkspace` app discovery, app opening, running app list, wake notifications.
- `SMAppService` launch-at-login.
- Sparkle updates.
- macOS unified logging.
- POSIX file watch APIs used by Claude session monitoring.
- macOS application bundle IDs.
- macOS-specific credential paths under `~/Library`.

## Key Product Rules To Preserve

- Do not own provider sign-in unless a provider explicitly needs an embedded web session.
- Prefer the same account the local tool is already using.
- Stop reading credentials entirely when a provider is disconnected.
- Do not hide failures by making up percentages.
- Mark derived data visibly.
- Cache last-good readings, but drop them when the account state makes them untrue.
- Back off on 429s and persist the backoff across relaunches.
- Keep usage polling separate from live activity detection.
- Make the overlay non-intrusive and avoid focus theft.
- Keep provider parsers pinned by tests because endpoints are not stable public APIs.

## Suggested Windows Architecture Direction

A pragmatic Windows app can keep the same domain architecture and replace only the platform shell.

Recommended shape:

- Language/runtime: C#/.NET 8 or newer.
- UI: WPF for mature transparent overlay support, or WinUI 3 if modern styling is more important than overlay control.
- Native interop: Win32 for no-activate, click-through, topmost, monitor work-area, tray icon, startup registration.
- Data: small JSON config plus JSON archive, or SQLite if history grows.
- HTTP: `HttpClient` provider clients.
- SQLite: `Microsoft.Data.Sqlite` or SQLitePCLRaw with read-only/WAL-aware access.
- Credential access: Windows Credential Manager where tools use it, otherwise read tool-owned files read-only.
- Tests: xUnit or NUnit for providers, parsers, archive, layout math, and activity monitors.

Possible module layout:

- `UseNotch.App`: startup, dependency wiring, tray, settings window, updater.
- `UseNotch.Overlay`: overlay window, placement, hit testing, rendering.
- `UseNotch.Model`: snapshots, windows, statuses, bands, reset copy.
- `UseNotch.Providers`: provider interfaces and implementations.
- `UseNotch.Activity`: session monitors.
- `UseNotch.Platform.Windows`: Win32, Credential Manager, known folder paths, process and file watching.
- `UseNotch.Tests`: parser, store, monitor, and geometry tests.

## Open Questions For The Build Plan

- Which providers are required for v1 on Windows?
- Should the Windows app clone the notch look exactly, or adapt to the Windows taskbar and screen-edge conventions?
- Should the overlay support all four edges in v1, or start with right edge only?
- Which framework should be used: WPF, WinUI 3, Tauri, or Electron?
- Should there be a tray icon from day one?
- Should the app be portable, installer-based, or MSIX-packaged?
- How should updates work?
- Which credential locations exist on Windows for Claude Code, Cursor, Codex, Antigravity, GLM, Grok, and OpenCode?
- Can Windows reliably show the overlay over full-screen exclusive apps, and is that a requirement?
- Should activity monitoring be part of v1 or come after usage rings?

## Initial Porting Strategy

The safest sequence is:

1. Build a Windows prototype overlay with static fixture data.
2. Implement the shared model: snapshots, windows, statuses, bands, reset copy.
3. Implement config and archive.
4. Port one file-based provider first, likely Codex or Grok if their Windows auth paths match the Unix-style home paths.
5. Port Cursor because it validates SQLite/WAL access and an installed GUI app session.
6. Port Claude after confirming Windows credential storage.
7. Add activity monitors after usage rings are stable.
8. Add settings and tray behavior.
9. Add packaging and updates.

This keeps the riskiest Windows-specific work visible early: transparent overlay behavior and credential discovery.
