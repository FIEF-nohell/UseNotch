# UseNotch Windows implementation plan

Build UseNotch as a non-elevated Windows tray application using C#, Avalonia UI 11, CommunityToolkit.Mvvm, and isolated Win32 services. Preserve Codenotch's compact usage display, conservative caching, and separation of usage from activity. Use modshell-cs as the desktop and packaging foundation.

This plan is based on:

- [Windows port brief](</C:/Users/Noel Hermann/Projects/UseNotch/CODENOTCH_WINDOWS_PORT_BRIEF.md>) and Codenotch commit `743601acd69e701131602b88082fcaeee0c2e88b`.
- [modshell-cs README](</C:/Users/Noel Hermann/Projects/modshell-cs/README.md>), [project dependencies](</C:/Users/Noel Hermann/Projects/modshell-cs/modshell-cs.csproj>), and [repository instructions](</C:/Users/Noel Hermann/Projects/modshell-cs/AGENTS.md>), at commit `bdaa8979db9b2e006d0f32f9c7dab716630ccd33`.
- Inspected lifecycle, tray, MVVM, gauge, chart, manifest, installer, and CI source in modshell-cs.
- Inspected Codenotch provider adapters, credential readers, usage store, archive, activity monitors, overlay geometry, and relevant tests.

The recommendations below distinguish observed repository behavior from proposed Windows behavior. Provider compatibility remains subject to validation against actual Windows tool versions. No implementation code or live credentials are included.

## 1. Product definition

### Purpose

UseNotch provides ambient visibility into usage limits and local activity for exactly two service families:

1. OpenAI GPT / ChatGPT / Codex.
2. Anthropic Claude / Claude Code.

It reads supported local authentication state, fetches available quota information, and displays a small overlay at a selected screen edge. It does not submit prompts, proxy traffic, generate responses, or modify another application's authentication.

### Core experience

The application starts in the notification area and displays a compact edge handle. Hovering expands it into two provider cells. Users can pin the expanded overlay.

Each cell communicates:

- The explicitly identified quota window, usually a primary or session window.
- Percentage used when the source provides a valid percentage or denominator.
- A count or `-` when a percentage is unavailable.
- Freshness and authentication problems.
- Local activity, where observable, with estimated readings labeled accordingly.

Hovering a provider opens a compact detail panel containing its quota windows, reset times, reading age, activity summary, and relevant recovery action.

Settings and a keyboard-accessible status view remain available from the tray.

### Scope of the readings

The OpenAI integration initially reports **Codex quota associated with a ChatGPT account**. The inspected source does not establish a comprehensive source for every GPT model's ChatGPT message allowance. The UI must therefore say "OpenAI / Codex" and identify the quota scope instead of presenting a universal "GPT usage" percentage.

The Anthropic integration initially reports the supported account usage windows accessible through the Claude Code OAuth session. Local activity describes observed Claude Code sessions. It must not imply complete visibility into all Claude web or desktop activity.

API billing, subscription quota, token counts, and model-specific limits are separate concepts. They must never be combined into one percentage.

### Carry over from Codenotch

Preserve:

- Edge attachment, compact rings, hover details, and pinning.
- Stable headline-window selection.
- Separate usage and activity streams.
- Last-good readings with visible age.
- Persistent rate-limit deadlines.
- Refresh on resume.
- Per-provider disconnection that stops credential reads.
- Invalidation of responses from previous connection generations.
- Honest unavailable, unsupported, expired, and stale states.

Adapt the visuals to Windows work areas and taskbars. Do not reproduce hardware-notch behavior or macOS activation policies literally.

### Intentionally excluded

Exclude every other provider, embedded browser authentication, conversation content, prompt interception, automated token refresh, browser-cookie extraction, cloud synchronization, and provider account management.

### MVP boundary

The first usable release supports:

- Windows 11 x64 as the primary validated platform.
- One selected native Windows credential profile per provider.
- Two provider cells.
- One overlay on one selected monitor.
- Four screen edges using shared placement logic.
- Tray lifecycle, settings, safe discovery, usage polling, and conservative activity indication.
- JSON settings and last-good cache.
- Signed WiX MSI installation and user-initiated upgrades.

Persistent history charts, multiple simultaneous accounts, WSL activity, and automatic update installation are post-MVP.

## 2. Recommended architecture

### Technology baseline

| Area | Recommendation |
|---|---|
| Runtime | C# and .NET 8 for initial parity with modshell-cs |
| UI | Avalonia UI 11 with centrally pinned, mutually compatible package versions |
| MVVM | CommunityToolkit.Mvvm |
| HTTP | Long-lived `HttpClient` instances with separate provider handlers |
| Serialization | System.Text.Json |
| SQLite | Microsoft.Data.Sqlite only for verified provider databases or optional history |
| Secrets | Windows Credential Manager access and DPAPI with current-user scope |
| Logging | Microsoft.Extensions.Logging with a bounded Serilog file sink |
| Tests | xUnit; Avalonia.Headless.XUnit for suitable UI tests |
| Packaging | WiX Toolset v5, initially pinned to the inspected project's `5.0.2` |
| CI | GitHub Actions on a pinned Windows runner image |

modshell-cs currently uses Avalonia `11.2.5`, CommunityToolkit.Mvvm `8.4.2`, FluentAvaloniaUI `2.4.0`, and LiveCharts2 `2.0.5`. Treat these as an inspected compatibility baseline, not an instruction to freeze every dependency indefinitely.

Use a single Avalonia theme stack. Start with Fluent styling and application-specific resource tokens. Add FluentAvalonia controls only where their value is concrete. Do not import hardware-monitor dependencies or LiveCharts for the two quota rings.

.NET 8 reaches end of support on November 10, 2026. Because this plan is dated September 2026, schedule a supported-LTS migration before that date. If the first production release occurs afterward, perform that migration before shipping. This is a release-maintenance requirement, not a reason to abandon the C#/Avalonia architecture. [Microsoft lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core)

### Application lifecycle

Use `IClassicDesktopStyleApplicationLifetime` with explicit shutdown.

Startup sequence:

1. Establish a single instance scoped to the current user and interactive session.
2. Load and validate settings.
3. Initialize redacted logging.
4. Create application services and the state store.
5. Create the tray icon.
6. Restore eligible cached readings as cached, unverified data.
7. Create and position the overlay without activation.
8. Start enabled provider workers and activity monitors.
9. Open onboarding settings on first launch only.

Closing settings must not terminate the application. Hiding the overlay must not disconnect providers. Quitting must cancel and await owned background work.

The inspected [modshell-cs lifecycle](</C:/Users/Noel Hermann/Projects/modshell-cs/App.axaml.cs>) and [close-to-tray handling](</C:/Users/Noel Hermann/Projects/modshell-cs/Views/MainWindow.axaml.cs>) provide useful patterns. Replace their dependence on one main window with an application-owned lifetime coordinator.

### Tray behavior

Use Avalonia `TrayIcon` and `NativeMenu` first.

Menu commands:

- Show status / Settings.
- Show or hide overlay.
- Pin or unpin overlay.
- Refresh enabled providers.
- Pause or resume monitoring.
- Quit.

Pausing stops provider requests and activity watchers while retaining settings and clearly marked cached data. Disconnecting additionally removes that provider's live state and last-good cache.

Test tray restoration after Explorer restarts. Introduce native `Shell_NotifyIcon` handling only if Avalonia fails that test on supported configurations.

If the tray becomes unavailable while the overlay is hidden, expose a normal recoverable window. Never leave an invisible process without a user-accessible exit.

### Overlay and settings ownership

`OverlayController` owns placement and visibility. It consumes state and sends semantic commands such as refresh, pin, or open settings.

`SettingsWindow` is a normal activating window. It is created lazily and reused. Settings contains an accessible status page so keyboard users can inspect everything shown in the non-activating overlay.

The overlay never owns provider clients, reads files, or schedules polling.

### MVVM structure

Recommended view models:

- `OverlayViewModel`: visibility, expansion, selected provider, and detail presentation.
- `ProviderCellViewModel`: formatted headline, status, freshness, and activity.
- `ProviderDetailsViewModel`: quota rows and recovery actions.
- `SettingsViewModel`: navigation and settings commands.
- `ProviderSettingsViewModel`: discovery, connection state, source selection.
- `DiagnosticsViewModel`: sanitized support information.

Use immutable domain snapshots and update view models through an injected UI dispatcher.

Retain modshell-cs's `ObservableObject` and generated-property approach. Avoid copying its pattern of starting the entire collection loop inside a view-model constructor.

### Provider and application services

Each adapter handles one service family's integration details. Shared application services handle scheduling, cache policy, state transitions, and publication.

Separate:

- Usage retrieval.
- Credential discovery.
- Activity monitoring.
- User guidance for authentication.
- Optional local database inspection.

Expected integration failures return typed results. Cancellation remains cancellation. Unexpected exceptions are caught at the provider-worker boundary and converted to safe errors without terminating the other provider.

### Proposed structure

The proposed root is `C:\Users\Noel Hermann\Projects\UseNotch`.

| Proposed area | Responsibility |
|---|---|
| `src/UseNotch.Domain` | Immutable models, identifiers, units, invariants |
| `src/UseNotch.Application` | Interfaces, polling coordinator, state store, freshness policy, settings operations |
| `src/UseNotch.Providers.OpenAI` | Codex discovery, credential parsing, HTTP mapping, activity readers |
| `src/UseNotch.Providers.Anthropic` | Claude discovery, credential parsing, usage mapping, activity readers |
| `src/UseNotch.Infrastructure` | JSON repositories, logging, HTTP setup, optional history repository |
| `src/UseNotch.Platform.Windows` | Win32 windows, monitors, credential APIs, startup, session and process services |
| `src/UseNotch.App` | Avalonia composition root, views, view models, controls, styles, tray |
| `tests/UseNotch.Domain.Tests` | Models, formatting policies, geometry invariants |
| `tests/UseNotch.Application.Tests` | Scheduling, state races, cache and lifecycle |
| `tests/UseNotch.Provider.Tests` | Parsers, mock HTTP, credential and activity fixtures |
| `tests/UseNotch.UI.Tests` | View models and headless control tests |
| `tests/UseNotch.Windows.Tests` | Native window, DPI, credential, and installer integration tests |
| `tests/Fixtures` | Synthetic or sanitized versioned fixtures |
| `installer` | WiX source and packaging script |
| `.github/workflows` | Build, test, package, and release workflows |

Dependency direction:

- Domain has no dependencies on Avalonia, Win32, HTTP, or storage.
- Application depends on Domain and owns integration interfaces.
- Provider, Infrastructure, and Windows projects implement Application interfaces.
- App composes implementations and contains presentation code.
- Provider projects do not reference each other or the UI.
- Windows services do not reference provider implementations.

A narrowly scoped Avalonia window bridge may live inside Platform.Windows to attach native behavior to Avalonia top levels. Keep that dependency out of its credential and process services.

## 3. Windows port mapping

| Codenotch/macOS concept | Windows recommendation | Porting rule |
|---|---|---|
| `NSPanel` | Avalonia `Window`, `Topmost`, `ShowActivated=false`, plus an isolated Win32 overlay host | Validate native focus and input behavior before provider integration |
| `NSScreen` | Avalonia `Screens`; `EnumDisplayMonitors`, `GetMonitorInfo`, and display configuration APIs where needed | Separate physical pixels from Avalonia logical units |
| `NSWorkspace` | Split into application launcher, process catalog, power/session notifications, and desktop visibility services | Avoid one platform object with unrelated responsibilities |
| Keychain | Exact provider credential-store access where verified; Credential Manager or DPAPI for app-owned secrets | macOS service names and authorization prompts do not transfer |
| `UserDefaults` | Versioned JSON settings and sanitized cache | Atomic writes, validation, migration, and recovery |
| Sparkle | Signed MSI releases; optional later updater behind `IUpdateService` | MVP offers explicit update checking and installer launch |
| `SMAppService` | Per-user Startup-folder shortcut through `IStartupRegistration` | Opt-in, no elevation, stable installed executable target |
| Application Support | `Environment.SpecialFolder.LocalApplicationData` for UseNotch state | Never store mutable state under Program Files |
| macOS process checks | `Process`, limited-query process handles, `GetProcessTimes` where required | Check PID plus creation time; tolerate access denial |
| File events | `FileSystemWatcher`, backed by bounded reconciliation | Events are hints and can be lost or duplicated |
| Bundle identifiers | Provider-owned launch descriptors using verified executable, protocol, or package identity | Do not assume a process name uniquely identifies an account |
| Wake notifications | `WM_POWERBROADCAST` behind a system-events service | Coalesce resume refreshes and honor backoff |
| Spaces/full-screen behavior | Normal Windows desktop z-order and optional desktop visibility service | Do not promise secure-desktop or exclusive-full-screen visibility |

Use documented Avalonia Win32 hooks where the pinned Avalonia 11 version provides them. Confirm availability against that exact version; current documentation alone does not establish API availability in `11.2.5`. [Avalonia Win32 customization](https://docs.avaloniaui.net/api/avalonia/controls/win32properties)

## 4. Provider implementation design

### Common rules

Both adapters must:

- Read only explicitly enabled sources.
- Respect one selected source per provider in MVP.
- Borrow credentials without refreshing, replacing, or deleting them.
- Separate local credential presence from server-confirmed authentication.
- Keep credential objects out of domain snapshots and view models.
- Bound file sizes, response sizes, parser depth, and directory scans.
- Bind results to source, account, and connection generation.
- Treat unknown formats as unsupported rather than guessing.
- Expose capability availability independently for quota and activity.
- Use only allowlisted HTTPS endpoints for credential-bearing requests.

Provider discovery must not scan browsers, arbitrary repositories, shell history, or the complete credential vault.

### OpenAI GPT / ChatGPT / Codex

#### Credential discovery and paths

Resolve the selected Codex root in this order:

1. Explicit path selected in UseNotch.
2. Effective `CODEX_HOME` visible to UseNotch.
3. `Environment.SpecialFolder.UserProfile` plus `.codex`.

Show the resolved source in Settings. Explain that environment variables set only inside a terminal may differ from the environment inherited by a tray application.

Inspect the selected root's supported configuration and credential source:

- `auth.json`.
- `config.toml`, limited to authentication-storage settings needed for discovery.
- A verified Windows OS credential-store entry when the selected Codex version uses one.

Codex documents `file`, `keyring`, and `auto` storage modes; `auth.json` is not guaranteed to exist. Exact Windows target names, key derivation, and serialized formats remain adapter compatibility details that must be verified before release. [Codex authentication](https://learn.chatgpt.com/docs/auth)

Use a proper pinned TOML parser for supported configuration fields rather than regular-expression parsing. Do not execute configuration helpers or interpret unrelated configuration.

For `keyring` or `auto`, implement the owning tool's verified selection rules. Do not choose whichever credential happens to be easiest to read, and do not silently use an obsolete file when the active account lives in the credential store.

If the installed storage format is not supported, show "Credential storage not supported" with the selected source. Do not instruct users to weaken their credential storage as the default remedy.

#### Authentication representation

The inspected [Codex credential reader](</C:/Users/Noel Hermann/Projects/UseNotch/.tmp/codenotch/Sources/Providers/CodexCredentials.swift>) expects a token object containing an access token and account ID. JWT claims supply optional labels and an expiry hint.

The Windows adapter should:

- Parse only supported authentication variants.
- Treat JWT claims as unverified metadata until the service accepts the credential.
- Avoid displaying email addresses in the overlay by default.
- Distinguish missing, unreadable, expired, rejected, and unsupported credentials.
- Recognize API-key authentication as a different mode, not as a ChatGPT quota credential.
- Reject unknown token modes without testing them against unrelated endpoints.

#### Usage source

The inspected adapter calls:

- `GET https://chatgpt.com/backend-api/wham/usage`
- Bearer authorization.
- `ChatGPT-Account-Id`.
- JSON accept header.

This is an undocumented integration dependency observed in the repository, not a stable public contract. Keep the endpoint, headers, DTOs, and parsing entirely inside the OpenAI adapter. [Inspected OpenAI adapter](</C:/Users/Noel Hermann/Projects/UseNotch/.tmp/codenotch/Sources/Providers/CodexLocalProvider.swift>)

Disable automatic redirects for authenticated requests. A redirect, HTML challenge, or login page must become a typed failure, not a browser-scraping fallback.

#### Quota mapping

Map the main `rate_limit` primary and secondary windows.

- Convert `used_percent` to a fraction.
- Derive labels from reported duration.
- Preserve missing secondary windows.
- Support durations other than five hours or seven days.
- Prefer an absolute reset timestamp; otherwise resolve a relative reset against response receipt time.
- Do not combine additional limits, review limits, or credits with the main quota.
- Do not infer billing spend from quota percentages.

Maintain an explicit headline ID. A missing primary window must not silently turn the ring into a weekly-limit display.

These rules follow important cases in [Codex parser tests](</C:/Users/Noel Hermann/Projects/UseNotch/.tmp/codenotch/Tests/CodexUsageTests.swift>).

#### Activity detection

The inspected implementation consults:

- `<CodexRoot>\state_5.sqlite`.
- A thread's rollout path.
- `<CodexRoot>\sqlite\codex-dev.db`.
- Desktop catalog update timestamps.

These are candidate sources, not promised Windows contracts.

Implement a capability probe that checks approved filenames and required tables/columns before enabling an activity reader. Do not select an arbitrary newest `state_*.sqlite` and assume its schema matches.

Prefer bounded file metadata observation and supported structured activity events. Database-derived paths must resolve inside approved local roots before being followed.

The original two-second scan and eight-second activity expiry are useful starting points. Recent writes mean "Recent activity, estimated"; they do not prove continuous work or detect waiting reliably.

Never infer waiting from silence. If available data cannot distinguish idle from unknown, report unknown.

#### Polling, failures, and stale data

Use the shared scheduler:

- 60 seconds during observed local activity.
- Five minutes otherwise.
- 15-second HTTP timeout.
- One credential re-read after rejection, with another request only if the credential changed.
- Persistent 429 backoff.

Map 401 to rejected authentication after the bounded re-read. Treat 403 separately as forbidden, entitlement-related, or unclassified based on supported response evidence. The original adapter merges 401 and 403; the Windows design should preserve that distinction.

Retain eligible same-account readings after network failures or expiry. Clear current readings after confirmed account change, disconnection, or sign-out evidence.

#### Privacy and ownership

The adapter owns auth DTOs, JWT parsing, account-header mapping, SQL, local schema probes, and activity heuristics.

Shared code owns scheduling, freshness, retries, publication, and cache invalidation. Raw tokens, rollout text, thread titles, and provider database rows never enter shared persistence.

### Anthropic Claude / Claude Code

#### Credential discovery and paths

Resolve the selected Claude root in this order:

1. Explicit path selected in UseNotch.
2. Effective `CLAUDE_CONFIG_DIR`.
3. `Environment.SpecialFolder.UserProfile` plus `.claude`.

The documented Windows credential file is:

`%USERPROFILE%\.claude\.credentials.json`

A custom `CLAUDE_CONFIG_DIR` changes the location. Claude Code owns the file and manages authentication. This differs from the macOS Keychain implementation in Codenotch. [Claude Code authentication](https://code.claude.com/docs/en/authentication)

Do not port `.claude-<slug>` directory discovery or Keychain suffix hashing as Windows guarantees. MVP supports one selected root; additional profiles are deferred.

#### Credential parsing and secure storage

The inspected macOS payload parser recognizes `claudeAiOauth`, an access token, an expiry expressed in milliseconds, and optional subscription information.

Use these as fixture candidates, then validate the native Windows file shape. Handle additional fields tolerantly, but reject missing or ambiguous required fields.

- Read with file sharing compatible with owner replacement.
- Retry a partial write after a short bounded delay.
- Cache the access token only in memory.
- Re-read on source change or bounded authentication recovery.
- Never copy the refresh token into UseNotch storage.
- Do not run `apiKeyHelper`, arbitrary commands, or provider login commands automatically.

An API-key-only or other authentication mode must show "Quota unavailable for this authentication method" unless that exact method has a verified quota source.

#### Usage source

The inspected [Claude OAuth adapter](</C:/Users/Noel Hermann/Projects/UseNotch/.tmp/codenotch/Sources/Providers/ClaudeOAuthProvider.swift>) calls:

- `GET https://api.anthropic.com/api/oauth/usage`
- Bearer authorization.
- `anthropic-beta: oauth-2025-04-20`

Treat the endpoint and beta header as unstable, versioned adapter behavior. The app must degrade gracefully if access changes.

#### Quota mapping

Support the inspected response families:

- A `limits` array with kind, percentage, and reset.
- Named `five_hour` and `seven_day` windows.

Normalize supported kinds into stable domain IDs. Merge recognized named windows when absent from the array, and deduplicate them.

Keep the session headline stable. Display model-specific windows separately when their meaning is understood.

Preserve the general ability to represent an unknown reset. For a specific response shape where omission means a window is inactive, keep that behavior inside the adapter and pin it with tests. Do not make "missing reset means discard usage" a universal domain rule.

Do not reconstruct subscription limits from token logs or monetary usage.

#### Activity detection

Probe `<ClaudeRoot>\sessions` for supported per-session records.

The inspected records include PID, optional process start time, status or tempo, and timestamps. [Claude session parsing](</C:/Users/Noel Hermann/Projects/UseNotch/.tmp/codenotch/Sources/Sessions/ClaudeSessionRecord.swift>)

Windows behavior:

- Watch the directory with a short debounce.
- Reconcile process liveness every five seconds.
- Compare process creation time when available to defend against PID reuse.
- Treat a missing process as ended.
- Treat access-denied process inspection as uncertain.
- Map recognized active and blocked states.
- Map unknown status values to unknown, rather than automatically idle.
- Do not display working directories, conversation titles, or free-form waiting text by default.

If native Windows Claude Code does not emit the expected records, usage remains available while activity reports unsupported. Optional explicit hooks can be investigated post-MVP.

#### Polling, failures, and stale data

Use the same network cadence and timeout as OpenAI.

On 429:

- Start local backoff at 60 seconds.
- Double consecutive penalties to a local ceiling of 15 minutes.
- Honor a larger valid server `Retry-After`.
- Persist the deadline.

This deliberately improves on the inspected implementation, whose final cap can shorten a longer server-requested wait.

On expiry, show "Open Claude Code to renew its session" and retain only eligible cached readings. On repeated rejection of unchanged credentials, stop network retries until source change or an explicit retry.

A token change without trustworthy account identity starts a new account epoch. Clear the old cached quota before accepting readings for that epoch. This may sacrifice cache continuity, but prevents mixing accounts.

#### Privacy and ownership

The adapter owns credential-file parsing, OAuth headers, supported usage shapes, session-record interpretation, and source-specific recovery guidance.

Shared code owns quota invariants, retry scheduling, cache rules, activity aggregation, and safe UI status.

## 5. Shared domain model

Keep domain objects immutable and free of secrets, framework controls, SQL, and raw JSON.

| Model | Recommended contents and meaning |
|---|---|
| `ProviderDefinition` | Fixed provider ID, display-name key, supported capabilities |
| `ProviderConnection` | Provider ID, selected-source identifier, enabled state, connection generation |
| `AccountScope` | Opaque local account partition and identity confidence; no access token |
| `AuthenticationState` | Disabled, discovering, missing, present-unverified, authenticated, expired, rejected, access-denied, unsupported |
| `UsageSnapshot` | Provider, account scope, retrieval time, windows, explicit headline ID, source descriptor, optional usage block |
| `QuotaWindow` | Stable ID, semantic scope, optional duration/start/reset, usage limit |
| `UsageLimit` | Optional used amount, remaining amount, capacity, used fraction, and unit |
| `UsageBlock` | Supported restriction category, affected capability, optional end time |
| `ActivitySession` | Opaque session ID, provider, state, start/last-observed times, fidelity, optional safe label |
| `ProviderStatus` | Connection health, last attempt, last success, next attempt, refresh-in-progress, optional error |
| `ErrorState` | Typed category, retryability, safe message key, HTTP/OS code if appropriate, retry deadline |
| `HistoricalSample` | Account-partitioned accepted reading for an identified window and observation time |
| `DataFreshness` | Fresh, stale, expired, or unknown, derived from timestamps and validation state |
| `ReadingFidelity` | Provider-reported, derived, or unknown, with an optional reason |
| `SourceDescriptor` | Source kind and compatibility identifier; whether the contract is documented or unpublished |

### Invariants

- Missing values remain missing.
- Zero usage is valid only when observed or validly calculated.
- An absent capacity is not unlimited.
- A percentage is calculated only with compatible units and a valid denominator.
- Preserve valid over-limit values; clamp only the drawn arc.
- Reject non-finite values and invalid negative counts.
- Store times as UTC `DateTimeOffset`; format locally in presentation.
- A reset passing does not prove usage has become zero.
- A secondary window never silently replaces an explicit headline.
- Provider-reported data can come from an undocumented endpoint. Fidelity and API stability are separate attributes.
- Activity confidence is independent of quota fidelity.
- Authentication, transport health, and freshness remain separate so the UI can represent combinations honestly.

### Interfaces

| Interface | Responsibility |
|---|---|
| `IUsageProvider` | Retrieve and normalize one provider reading |
| `ICredentialSource` | Discover/read an adapter-private credential lease and detect changes |
| `IActivityMonitor` | Publish normalized activity observations |
| `IProviderRegistry` | Expose exactly the two supported provider definitions |
| `IUsageStateStore` | Atomically accept current-generation results |
| `IPollingCoordinator` | Schedule, deduplicate, cancel, and apply backoff |
| `ISettingsRepository` | Load, validate, migrate, and save settings |
| `IUsageCache` | Store sanitized last-good readings and retry deadlines |
| `IHistoryRepository` | Optional post-MVP history |
| `ISecretStore` | Protect app-owned secret material |
| `IOverlayPlatform` | Attach topmost, no-activate, and input-region behavior |
| `IMonitorService` | Report monitor identity, bounds, work areas, and scale |
| `IStartupRegistration` | Read and update launch-at-login state |
| `IProcessInspector` | Bounded liveness and creation-time checks |
| `ISystemSessionEvents` | Suspend, resume, lock, and unlock |
| `IUiDispatcher` | Publish presentation changes on the UI thread |

Use .NET `TimeProvider` for testable time and monotonic scheduling.

Authentication DTOs, refresh tokens, JWT claims, file offsets, native process handles, database versions, and HTTP response bodies stay inside adapters or infrastructure.

## 6. Polling and concurrency

### Scheduling defaults

| Operation | Default |
|---|---|
| Initial quota retrieval | Once after connection/source validation |
| Active provider quota | Every 60 seconds |
| Idle or activity-unknown provider quota | Every five minutes |
| Resume/unlock/network recovery | One coalesced refresh after 2-5 seconds |
| Manual refresh | Minimum 15 seconds between accepted attempts |
| Credential metadata reconciliation | Every 30 seconds while enabled, plus file events |
| Activity file-event debounce | Approximately 150 ms |
| Claude process reconciliation | Every five seconds |
| Codex estimated-activity scan | Every two seconds while an eligible source is active |
| Idle activity-source rediscovery | Every 30 seconds |
| Estimated Codex activity expiry | Eight seconds after last credible observation |

Local inactivity does not prove quota is unchanged because the account may be used elsewhere. Retain the five-minute idle poll.

### Concurrency ownership

Run the two providers independently with a maximum of two simultaneous quota operations.

Within each provider:

- Permit exactly one in-flight quota operation.
- Coalesce timer, manual, resume, and credential-change triggers.
- Route all retries through the same worker.
- Avoid nested retry policies in both `HttpClient` and the scheduler.
- Publish each provider result immediately without waiting for the other.

Capture connection generation, credential generation, and account scope at request start. Check them again before updating the state store, cache, or history.

Disabling and immediately re-enabling a provider must never admit a response from its previous connection.

### Timeouts and retry policy

- HTTP connect timeout: approximately five seconds.
- Full attempt timeout, including body reading: 15 seconds.
- Total operation budget, including one bounded retry: 35 seconds.
- Local credential-file read: two-second budget.
- SQLite lock wait: approximately 100-250 ms.
- Bound overall database work independently of lock waiting.

Retry one transient network or 5xx failure after a randomized 2-5 second delay. Repeated transient failures move to scheduled backoff rather than an immediate loop.

For 429, the next attempt is no earlier than the greater of:

- Exponential local delay, capped at 15 minutes.
- The valid server deadline.

Add jitter after this minimum. Never shorten the server deadline.

Do not retry malformed successful responses immediately. After three consecutive schema failures, slow compatibility probes to once per hour and keep manual diagnostics available.

### Freshness

- Restored cache is stale until its source/account can be matched and a request succeeds.
- An accepted live snapshot is fresh.
- A failed refresh immediately adds a stale indicator and preserves the last-success timestamp.
- A reading also becomes stale after 15 minutes without success.
- After 24 hours, suppress the compact headline and retain the reading only as dated detail.
- When a window's reset has passed without a new reading, remove its claim to current availability. Show "Awaiting updated window."
- Clear current and cached readings on confirmed account change or disconnect.

Do not refresh the timestamp when reusing cached data.

### UI publication

Perform HTTP, file parsing, database work, and persistence outside the UI thread. Publish immutable state through a bounded channel or equivalent serialized dispatcher.

Coalesce repetitive activity updates. Stop animations and presentation timers while the overlay is hidden or the session is locked.

### Shutdown

On Quit:

1. Stop accepting refresh requests.
2. Cancel provider and activity operations.
3. Dispose watchers and unsubscribe system notifications.
4. Await owned work within a five-second shutdown budget.
5. Flush pending atomic settings/cache writes and logs.
6. Dispose tray and native window hooks.
7. Complete desktop shutdown.

Do not block the UI thread with synchronous waits or leave unobserved background tasks.

## 7. Persistence and security

### Storage allocation

| Storage | Contents |
|---|---|
| In memory | Access-token leases, current snapshots, activity sessions, debounce state, in-flight operations |
| JSON configuration | Enabled providers, selected roots, placement, scale, visibility, startup preference, privacy settings |
| JSON cache | Sanitized last-good windows, timestamps, compatibility version, opaque account partitions, retry deadlines |
| SQLite provider access | Verified read-only activity queries only |
| Optional history SQLite | Explicitly enabled normalized historical samples |
| DPAPI | App-owned account-partition key or other small secret material |
| Credential Manager | Exact verified provider entries when needed; optional future app-owned credentials |

Recommended application paths under `%LOCALAPPDATA%\UseNotch`:

- `settings.json`
- `cache\usage-cache.json`
- `cache\backoff.json`
- `logs\`
- `secrets\`
- Optional post-MVP `history\usage.sqlite`

Resolve the location through .NET known folders. Keep settings local because they contain machine-specific paths.

### JSON reliability

Use schema versions and validated defaults.

Write to a temporary file in the same directory, flush, and replace atomically. Retain one recovery copy. Serialize writes so a late cache operation cannot undo a disconnect.

For malformed configuration, preserve the damaged file and show a recovery message. For an unsupported future schema, avoid overwriting it silently.

No tokens, raw responses, email addresses, conversation titles, or full provider database rows belong in these files.

### Credential protection

MVP does not persist borrowed OAuth tokens. Credential Manager and DPAPI protect app-owned material and enable verified reads of provider-managed storage.

- Use current-user DPAPI scope.
- Keep credential leases short-lived.
- Prevent automatic object serialization and unsafe `ToString()` output.
- Clear disposable byte buffers where practical.
- Do not promise complete erasure of managed strings.
- Do not use clipboard-based token entry in MVP.
- Do not refresh or revoke the owning tool's token.
- Do not broaden source ACLs or request elevation to bypass access restrictions.

The [modshell-cs manifest](</C:/Users/Noel Hermann/Projects/modshell-cs/app.manifest>) requests administrator privileges. UseNotch must instead use `asInvoker` and `uiAccess=false`.

### Local file protection

Create app-owned directories with permissions appropriate to the current user and required system principals, without broad user-group access.

Treat source files as sensitive and read-only. Detect unsupported network locations and unexpected reparse targets. Do not traverse arbitrary paths discovered in untrusted records.

When a source is missing, watch an existing approved parent or reconcile periodically. Do not create provider directories or files.

### Provider SQLite access

Use short-lived read-only connections with pooling disabled for external databases.

- Probe required schema.
- Use bounded queries and row limits.
- Keep transactions short.
- Never migrate, checkpoint, vacuum, change journal mode, or repair another application's database.
- Fail gracefully on locks or unsupported schemas.
- Do not copy only the main file of a live WAL database.

The inspected Codenotch helper falls back to `immutable=1` after a read-only open fails. Do not copy that generic fallback. A failed open does not prove the database is static, and WAL handling is essential for current data. Use immutable mode only for a verified static fixture or consistent snapshot. [SQLite WAL behavior](https://sqlite.org/wal.html)

### History decision

Persistent history is not required for the first release. Last-good JSON is sufficient for immediate status and restart recovery.

History becomes justified when users need to understand quota consumption across a session or day. Implement it as an optional post-MVP feature, disabled by default.

Minimal logical schema, one row per accepted quota window:

| Column | Type / purpose |
|---|---|
| `sample_id` | Integer primary key |
| `provider_id` | Supported provider identifier |
| `account_partition` | Opaque local identifier |
| `window_id` | Stable quota-window identifier |
| `observed_at_utc_ms` | Observation time |
| `window_start_utc_ms` | Nullable |
| `reset_at_utc_ms` | Nullable |
| `used_fraction` | Nullable numeric value |
| `used_amount` | Nullable numeric value |
| `remaining_amount` | Nullable numeric value |
| `capacity` | Nullable numeric value |
| `unit` | Supported unit identifier |
| `fidelity` | Provider-reported or derived |
| `source_version` | Adapter compatibility identifier |

Add an index on provider, account partition, window, and observation time. Record accepted readings only, at most once per minute per window. Default to seven-day retention with a size cap.

Charts must show gaps and reset boundaries. They must not interpolate missing observations as authoritative usage or present cumulative-window differences as billing.

### Logging and network privacy

Log operation names, provider IDs, safe status codes, elapsed time, parser versions, and retry decisions.

Exclude:

- Authorization and cookie headers.
- Tokens and JWT payloads.
- Raw HTTP bodies.
- Full paths and account labels.
- Session titles, prompts, or generated text.
- Unfiltered exception objects from parsers.

Use bounded rotation, initially five files of up to 2 MB each. Normal polling success should not generate verbose logs.

No telemetry or third-party analytics by default. Network traffic is limited to enabled provider endpoints and explicit update checks. Honor normal Windows TLS validation and system proxy behavior without disabling certificate checks.

## 8. Overlay and UI implementation plan

### Native window behavior

Create a transparent, borderless Avalonia window with:

- No system decorations.
- No taskbar button.
- No resize or drag behavior.
- `Topmost=true`.
- `ShowActivated=false`.
- Transparent background and supported transparency hints.

Attach Windows behavior before first visible presentation:

- `WS_EX_NOACTIVATE`.
- `WS_EX_TOOLWINDOW`.
- `SetWindowPos` with topmost positioning and `SWP_NOACTIVATE`.
- `WM_MOUSEACTIVATE` handling using `MA_NOACTIVATE` where needed.

Preserve Avalonia's existing style flags and renderer ownership. Do not force layered-window APIs onto the HWND without verifying compatibility with the selected rendering backend.

Opening Settings is an explicit activating action. Hovering, expanding, refreshing, or pinning the overlay must preserve the foreground application.

### Click-through and interactive regions

Use tightly bounded native windows and explicit native regions.

The preferred MVP approach:

1. Keep overlay content and provider details in one controlled top level.
2. Compute the union of the visible notch, detail panel, and any visible connecting area.
3. Apply an equivalent native region through the platform service.
4. Keep everything outside that region outside the interactive window shape.
5. Perform control-level hit testing only within that region.

`SetWindowRgn` defines the native window shape and requires careful ownership of region handles. It also clips rendering, so shadows cannot be assumed to extend beyond it. [Windows region API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowrgn)

Do not rely solely on Avalonia `IsHitTestVisible=false`, transparent brushes, or returning `HTTRANSPARENT`. Microsoft's documented `HTTRANSPARENT` behavior refers to underlying windows in the same thread, which is insufficient as proof of click-through into unrelated applications. [Windows hit testing](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest)

Validate clicks and scrolling against an independent test application and real editors.

`WS_EX_TRANSPARENT` is a whole-window behavior in relevant layered-window configurations. It is unsuitable as a permanent setting on an overlay that also contains buttons. Reserve a fully passive overlay mode for later validated implementation. [Layered-window input behavior](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)

Avoid global mouse hooks and synthetic forwarding of clicks.

### Placement and monitors

Use a pure placement function accepting:

- Monitor identity and full bounds.
- Work area.
- DPI scale.
- Edge and normalized position along the edge.
- Overlay size in logical units.
- User offset.
- Taskbar auto-hide exclusion margin.

Default to the top center of the primary monitor.

Anchor inward placement to the work area. Center along the full monitor bounds, then clamp into the usable area. This preserves Codenotch's useful distinction between edge attachment and stable centering.

Support negative virtual-desktop coordinates. Store monitor identity and logical placement preferences, not absolute screen pixels.

Persist a stable display device identifier when available. On removal:

- Move to the primary monitor.
- Retain the preferred monitor identity.
- Restore placement when that monitor returns.

MVP has one overlay, not one per monitor.

### Taskbar and full-screen handling

Respect work-area changes for taskbars and other reserved desktop areas.

For an auto-hidden taskbar, preserve a small tested edge gap so UseNotch does not block taskbar activation. Do not reserve an AppBar area or alter other windows' work areas.

Default to hiding over a confidently identified full-screen application on the selected monitor. Treat detection as heuristic and expose an override.

Do not promise visibility over exclusive full-screen rendering, UAC secure desktop, lock screen, or other protected surfaces. Do not repeatedly fight another topmost application for z-order.

### DPI behavior

Configure per-monitor DPI awareness before creating any windows, consistent with Avalonia's Windows initialization.

Maintain:

- Logical units for control layout.
- Physical pixels for HWND placement and native regions.
- One explicit conversion boundary.

Coordinate Avalonia scaling updates with `WM_DPICHANGED`. Recompute placement, region geometry, and detail bounds once after scale changes settle. Do not independently apply both Avalonia's and a native handler's resize calculations. [DPI change handling](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged)

### Expansion state machine

Use explicit states:

- Hidden.
- Collapsed.
- Expanded.
- ProviderDetailsOpen.
- Pinned.

Suggested behavior:

- Expand after approximately 150 ms of intentional hover.
- Collapse 350 ms after leaving all visible overlay regions.
- Keep open while a provider detail panel is hovered.
- Pinning prevents automatic collapse.
- Hiding dismisses details but retains pin preference.

Avoid invisible hover-catching rectangles. Use the visible edge handle as the acquisition target.

For MVP, commit native bounds and input regions together, then animate content within them. Avoid continuously morphing native geometry until correctness is proven.

### Details and settings access

Implement the detail surface as controlled overlay content rather than an unverified default tooltip popup.

Include:

- Provider and quota scope.
- Each supported window.
- Used and remaining values where valid.
- Local reset time.
- Last successful retrieval age.
- Activity fidelity.
- One primary recovery action.

Open Settings from the tray and an explicit overlay control. Provide the same data in a normal keyboard-accessible status page.

### Visual state treatment

| State | Compact treatment | Detail treatment |
|---|---|---|
| Loading | `-` and a subtle loading indicator | "Reading usage" |
| Authenticated | Valid quota ring | Source, windows, and reset times |
| Missing credentials | `-` and key/status symbol | Owning-tool sign-in guidance |
| Expired credentials | Stale value if eligible | Explain owner-managed renewal |
| Unavailable | `-` with neutral status | Specific unsupported source or quota scope |
| Stale | Clock/status marker and dated value | Last success and current failure |
| Error | Error symbol, never fake 0% | Safe reason and useful recovery action |
| Working | Thin neutral inner arc | Observed or estimated activity label |
| Waiting | Amber symbol | Supported waiting category |
| Unknown activity | No busy animation | "Activity unavailable" or "Unknown" |

### Practical Avalonia style guide

| Element | Recommendation |
|---|---|
| Direction | Restrained, dense desktop utility with clear hierarchy |
| Overlay surface | Near-black `#101216` |
| Detail/settings surface | `#191D24` |
| Primary text | `#F2F4F7` |
| Secondary text | `#B3BBC7` |
| Dividers | `#343B46`; decorative rather than the sole control boundary |
| Essential control outline | Brighter token such as `#687588`, verified for contrast |
| Focus | `#8AB4FF` |
| Success | `#67D6A3` |
| Warning/waiting | `#F4C66B` |
| Error | `#FF8585` |
| Provider identity | OpenAI mint; Anthropic muted warm orange |
| Typography | Segoe UI for interface text; Cascadia Mono or Consolas for numbers |
| Text sizes | 13-14 DIP body, 12 DIP secondary, 16-18 DIP ring value, 18 DIP section title |
| Spacing | 4 DIP base scale: 4, 8, 12, 16 |
| Corners | 6 DIP controls, 8 DIP details, approximately 10 DIP exposed notch corners |
| Borders | Mostly 1 DIP; avoid nested bordered containers |
| Shadows | None on the edge shell; at most one subtle detail shadow if native clipping permits |
| Rings | 34-38 DIP diameter, approximately 3 DIP stroke |
| Bars | 4-6 DIP height, restrained corner rounding |
| Hover | Slightly lighter solid background |
| Pressed | Darker solid background with unchanged geometry |
| Errors | Small icon and readable text; no flashing red surfaces |

Keep provider identity separate from warning severity. A quota ring can use the provider color while a warning symbol communicates exhaustion. Text must identify the meaning.

Use a consistent "% used" convention. Details may additionally show "% remaining". Label the selected window so the percentage has context.

Adapt [modshell-cs gauge geometry](</C:/Users/Noel Hermann/Projects/modshell-cs/Views/GaugeArc.cs>) into a smaller purpose-built control. Its existing gauge is 108 DIP with a 270-degree sweep and fixed hardware thresholds. Do not simply scale that control down. Test 0%, 100%, over-limit values, and full-circle geometry explicitly.

Suggested sizes:

- Collapsed top handle: approximately 64 x 12 DIP.
- Expanded two-provider shell: approximately 152 x 56 DIP.
- Detail panel: 300-340 DIP wide, bounded by monitor work area.
- Settings: approximately 820 x 620 DIP, resizable with a practical 640 x 480 minimum.

At large text settings, allow controls to grow and details to scroll.

Animation rules:

- 120-180 ms opacity and short translation transitions.
- No spring overshoot or decorative bounce.
- Stop continuous animation when hidden.
- Busy motion only while fresh activity evidence exists.
- Honor reduced-motion preference.
- Avoid animation that changes the meaning of a percentage during account switching.

Accessibility:

- Aim for 4.5:1 text contrast and 3:1 meaningful non-text contrast.
- Use labels and symbols in addition to color.
- Give rings automation names including provider, scope, value, and freshness.
- Provide a visible keyboard focus indicator.
- Ensure all overlay functionality has a keyboard-accessible equivalent in the status/settings window.
- Use approximately 28 DIP minimum pointer targets in compact mode and offer a larger scale option.
- Support high-contrast presentation and text scaling.

Settings layout uses a compact left navigation column: Status, Providers, Appearance, General, Privacy, and About. Use aligned rows and dividers rather than oversized cards.

## 9. Implementation sequence

All proposed areas below refer to the structure in section 2. Testing accompanies every milestone.

### Milestone 1: Project setup

- **Objective:** Establish the solution, dependencies, and architectural boundaries.
- **Areas:** Solution, project files, manifest, central package settings, initial CI.
- **Details:** Add nullable analysis, compiled Avalonia bindings, `asInvoker`, dependency pinning, and test projects. Preserve applicable source license notices when adapting code.
- **Acceptance:** Clean checkout restores, builds, and runs as a standard user.
- **Tests:** Build all projects; verify dependency direction and launch without UAC.
- **Risks:** Copying hardware dependencies or elevated manifest behavior; unsupported runtime at release.

### Milestone 2: Avalonia shell and tray lifecycle

- **Objective:** Establish an application that remains controllable throughout its lifetime.
- **Areas:** App startup, lifetime coordinator, tray, settings shell, single-instance service.
- **Details:** Explicit shutdown, lazy settings window, close-to-hide, duplicate-launch forwarding, tray recovery.
- **Acceptance:** Repeated show/hide works; second launch does not create another poller; Quit ends the process.
- **Tests:** Lifecycle unit tests and interactive Explorer-restart verification.
- **Risks:** Hidden orphan process, accidental shutdown, duplicate instances.

### Milestone 3: Overlay prototype

- **Objective:** Resolve the highest-risk Windows behavior before live integration.
- **Areas:** Overlay window, platform host, monitor service, pure geometry.
- **Details:** Static two-cell shell; topmost, no-activate, transparency, native region, work-area placement.
- **Acceptance:** Hover and pointer actions preserve editor focus; transparent areas pass input to another process; placement survives mixed DPI.
- **Tests:** Geometry tests and a real Windows input/focus harness.
- **Risks:** First-show activation, renderer incompatibility, click interception, clipped content.

### Milestone 4: Shared domain and mock provider

- **Objective:** Define honest state semantics and a complete visual test surface.
- **Areas:** Domain models, provider interfaces, mock adapter, fixture catalog.
- **Details:** Cover quota, missing values, expiry, stale cache, unsupported auth, rate limits, and activity fidelity.
- **Acceptance:** Every required state can be exercised deterministically without credentials or network.
- **Tests:** Domain invariants, state transitions, formatting, missing-headline behavior.
- **Risks:** Treating missing data as zero or encoding provider JSON directly in shared models.

### Milestone 5: State store, polling, and cache foundation

- **Objective:** Implement concurrency and persistence guarantees before using real credentials.
- **Areas:** Polling coordinator, state store, JSON repositories, time abstraction.
- **Details:** Per-provider single-flight workers, bounded parallelism, generation checks, persistent backoff, atomic cache.
- **Acceptance:** Manual refresh, timers, resume, and rapid toggles never duplicate requests or resurrect old state.
- **Tests:** Fake-time scheduling, race scenarios, delayed HTTP completion, cache corruption.
- **Risks:** Late responses, stale timestamps, relaunch bypassing penalties.

### Milestone 6: OpenAI integration

- **Objective:** Display verified Codex quota from a selected native Windows source.
- **Areas:** OpenAI discovery, auth parsing, credential-store reader, HTTP client, mapper.
- **Details:** Validate file and OS-store selection, account scope, variable window durations, expiry and rejection recovery.
- **Acceptance:** A supported account matches the owning tool's displayed quota within retrieval timing; unsupported modes explain themselves.
- **Tests:** Synthetic auth fixtures, parser regressions, mock HTTP, account switching, changed versus unchanged token retry.
- **Risks:** Undocumented endpoint, wrong account selection, unverified keyring mapping.
  
Release gate: document and test the supported Codex storage modes. A file-only prototype is not sufficient proof of general Windows compatibility.

### Milestone 7: Anthropic integration

- **Objective:** Display supported Claude account quota from native Windows Claude Code credentials.
- **Areas:** Anthropic file discovery, parser, usage mapper, recovery guidance.
- **Details:** Validate the Windows file format, merge supported usage shapes, retain stable headline, respect longer server backoff.
- **Acceptance:** Supported windows match Claude's usage display; expiry does not trigger app-owned refresh.
- **Tests:** File replacement, milliseconds versus seconds, null windows, duplicate windows, fractional timestamp formats, 401/403/429.
- **Risks:** Credential format drift, unavailable OAuth endpoint, ambiguous account continuity.

### Milestone 8: Activity monitoring

- **Objective:** Add useful local status without overstating certainty.
- **Areas:** Provider activity readers, watcher infrastructure, process inspector, aggregation.
- **Details:** Claude session probes, PID reuse handling, Codex supported-schema probes, bounded metadata monitoring.
- **Acceptance:** Supported Claude waiting states appear promptly; Codex estimates expire; unsupported activity does not break quota.
- **Tests:** Process fixtures, truncated records, watcher overflow, database locks, timestamp boundaries.
- **Risks:** False busy or idle states, schema drift, excessive scanning.

### Milestone 9: Settings, privacy, and startup

- **Objective:** Make configuration and recovery understandable.
- **Areas:** Settings views and view models, source selection, startup service, privacy controls.
- **Details:** Two provider rows, source explanation, enable/disable, monitor and edge selection, reduced motion, cache clearing.
- **Acceptance:** Changes survive restart; disconnect stops all provider reads; startup is opt-in and accurately reflects registration.
- **Tests:** View-model validation, JSON migrations, startup registration, disconnect/reconnect races.
- **Risks:** Misleading "sign out" language, stale source paths, leaked diagnostics.

### Milestone 10: Overlay polish and accessibility

- **Objective:** Complete the daily-use interaction.
- **Areas:** Ring control, styles, detail panel, expansion state machine, accessible status page.
- **Details:** Dense typography, state-specific visuals, stable pointer transitions, bounded details, larger-scale support.
- **Acceptance:** No hover flicker, focus theft, clipped text, unexplained percentage, or inaccessible recovery action.
- **Tests:** Headless control tests, visual regression, keyboard and Narrator checks, real input-region tests.
- **Risks:** Native and visual geometry diverging; inaccessible non-activating UI.

### Milestone 11: History decision and bounded implementation

- **Objective:** Prevent historical reporting from delaying reliable status.
- **Areas:** Optional history interface and repository, future chart view model.
- **Details:** For MVP, validate last-good cache and keep disk history disabled. After MVP, implement the schema and retention defined in section 7.
- **Acceptance:** MVP works without a history database; any enabled history stores only normalized samples and shows gaps.
- **Tests:** Retention, partition isolation, reset boundaries, migration and disk-full recovery when implemented.
- **Risks:** Unnecessary disk growth, privacy exposure, misleading charts.

### Milestone 12: Packaging and CI

- **Objective:** Produce repeatable installable artifacts.
- **Areas:** WiX source, packaging script, CI and release workflows.
- **Details:** Adapt modshell-cs self-contained publish and harvesting. Choose a per-user MSI under LocalAppData with a new permanent UpgradeCode, correct per-user shortcuts, and stable product identity.
- **Acceptance:** Clean install, upgrade, and uninstall work as a standard user; application and MSI versions agree.
- **Tests:** Package builds on pull requests, clean-VM installation, N-1 upgrade, downgrade rejection, uninstall.
- **Risks:** Incorrect MSI component identity, mixed installation scope, locked files.

Do not copy modshell-cs's UpgradeCode, manufacturer metadata, per-machine assumptions, or same-version-upgrade behavior blindly.

### Milestone 13: End-to-end testing and resource validation

- **Objective:** Verify the complete application under normal and adverse conditions.
- **Areas:** All test projects, Windows harness, performance captures.
- **Details:** Exercise real account flows locally without recording secrets; run offline, resume, source rotation, multi-monitor, and overnight scenarios.
- **Acceptance:** Both providers recover independently; no sustained growth in handles, memory, tasks, or requests.
- **Tests:** Full matrix in section 10 plus an eight-hour soak.
- **Risks:** Renderer-specific faults, slow leaks, race conditions visible only after sleep.

### Milestone 14: Release hardening

- **Objective:** Ship a trustworthy first release.
- **Areas:** Signing pipeline, MSI validation, release metadata, update-check service, support diagnostics.
- **Details:** Sign application binaries before packaging, sign and timestamp the MSI, verify signatures, protect release credentials, and confirm runtime support.
- **Acceptance:** A clean VM installs the signed release, runs without elevation, upgrades correctly, and retains usable settings.
- **Tests:** Tampered-download rejection, interrupted upgrade, publisher verification, actual signed-package smoke test.
- **Risks:** Signing failures, update trust, runtime end of support, unrecoverable migration.

Use a new patch release to correct a published release. Do not replace an already distributed installer under the same version.

## 10. Testing strategy

### Unit and view-model tests

Test:

- Usage invariants and units.
- Headline selection.
- Freshness transitions and reset expiry.
- Error precedence without hiding authentication state.
- Activity aggregation, with waiting prioritized only when explicitly observed.
- Poll scheduling, jitter bounds, and long `Retry-After`.
- Connection/account generation invalidation.
- Settings validation and command availability.
- UI-dispatch boundaries and disposal.

Use fake time instead of real waits.

### Provider parser and mock HTTP tests

OpenAI fixtures include:

- Primary and secondary windows.
- Weekly-only or monthly primary quota.
- Unknown valid duration.
- Missing percentage.
- Null window.
- Relative and absolute reset.
- Extra unrelated quota categories.
- Malformed JWT metadata and unsupported auth mode.

Anthropic fixtures include:

- Limits-array shape.
- Named-window shape.
- Both shapes together.
- Deduplication.
- Missing session headline.
- Reset omission under each supported shape.
- Unknown kinds and timestamp formats.
- Expired or partially replaced credential files.

HTTP tests cover 200, empty body, HTML body, redirect, 401, 403, 404/410, 429, 5xx, timeout, cancellation, oversized body, and dropped connection.

Assert that unauthorized hosts never receive credentials.

### Credential tests

Use synthetic tokens and test-specific credential targets only.

Verify:

- Disabled provider causes zero credential reads.
- Expired versus absent is distinguished.
- Read access denial never triggers elevation.
- Token rotation invalidates obsolete responses.
- No token appears in logs, JSON, exceptions, or diagnostics.
- DPAPI round trips under the test user.
- Cross-user isolation is checked in a disposable Windows environment.
- App shutdown disposes credential leases.

CI must never use production provider accounts.

### File and SQLite fixtures

Exercise:

- Atomic replacement and truncated writes.
- Missing directories appearing later.
- Duplicate and lost watcher events.
- Reparse points and unapproved paths.
- UTF-8 names, spaces, long paths, and non-ASCII usernames.
- Unknown SQLite version.
- Missing tables/columns.
- Live WAL updates, lock contention, and owner shutdown.
- Query cancellation and bounded result sizes.
- PID reuse and inaccessible process metadata.

### Overlay tests

Headless tests validate layout and semantics. They cannot prove native focus or cross-process input behavior.

Use a real interactive Windows session to verify:

- Foreground HWND remains unchanged during hover, refresh, and expansion.
- Underlying windows receive clicks, drag initiation, and wheel input outside the region.
- Overlay controls receive input inside the region.
- Transparent corners do not block other applications.
- Detail panels remain within the work area.
- Hiding removes all input interception.
- Native resources are released over repeated create/hide/show cycles.

Do not run automated pointer tests against an active user's working desktop.

### DPI and multi-monitor matrix

Test at 100%, 125%, 150%, 175%, and 200% scale, including:

- Mixed-scale monitors.
- Secondary display left of or above primary.
- Portrait display.
- Primary-monitor change.
- Dock/undock.
- Resolution change.
- RDP connection and disconnection.
- Sleep/resume and lock/unlock.
- Taskbar auto-hide.
- GPU rendering and a supported software-rendering fallback.

### Tray and packaging tests

Verify:

- Close Settings.
- Hide overlay.
- Pause monitoring.
- Explorer restart.
- Second launch.
- Quit while requests are pending.
- Windows logoff.
- Install with no separate .NET runtime.
- Per-user scope.
- Upgrade while running.
- Interrupted upgrade and rollback.
- Downgrade rejection.
- Startup shortcut removal.
- Uninstall with explicit documented data-retention behavior.

Normal GitHub-hosted build success is not evidence that interactive overlay behavior passed. Use a dedicated interactive test environment for those checks.

### Manual verification checklist

- [ ] Both supported accounts display correctly scoped quota.
- [ ] Missing credentials show useful owner-tool guidance.
- [ ] An API key is not mistaken for subscription quota.
- [ ] Account switching never shows the previous account's number as current.
- [ ] Offline and rate-limited states retain correctly dated data.
- [ ] Reset expiry does not produce an invented zero.
- [ ] Disconnected providers cause no subsequent file or network access.
- [ ] Estimated activity is visibly identified.
- [ ] Hovering and clicking do not steal editor focus.
- [ ] Input outside the overlay reaches the underlying application.
- [ ] All edges work with taskbar offsets and mixed DPI.
- [ ] Keyboard users can inspect status and recover through Settings.
- [ ] Reduced motion, high contrast, and larger text remain usable.
- [ ] Quit leaves no process or tray artifact.
- [ ] Signed install and upgrade pass on a clean machine.

## 11. MVP definition

### Required for first usable release

- Exactly two provider families.
- One selected Windows source/account per provider.
- Verified file and supported OS-store discovery for the targeted Codex versions.
- Verified native Claude Code credential-file support.
- Explicitly scoped quota, reset times, and stable headline semantics.
- Clear missing, expired, rejected, forbidden, unsupported, stale, and unavailable states.
- Independent provider polling with bounded retries and persistent 429 backoff.
- Conservative activity indication with capability and fidelity labels.
- One polished, non-activating overlay with reliable input regions.
- Four edge placements and one selected monitor.
- Tray, accessible settings/status, pause, refresh, and quit.
- Atomic JSON settings and last-good cache.
- No persisted borrowed tokens.
- Non-elevated operation.
- Signed WiX MSI and tested upgrade path.
- Unit, parser, mock HTTP, and Windows interaction coverage.

Initial performance targets, measured on a declared reference machine:

- Idle average CPU below 0.5%.
- No continuous redraw while collapsed and idle.
- Working-set target below approximately 150 MB after warm-up.
- No sustained memory or handle growth during an eight-hour soak.
- No recursive scans of entire provider session archives.
- Tray and cached UI available within roughly two seconds of startup.

These are engineering targets, not claims established by modshell-cs.

### Post-MVP improvements

- Multiple accounts or Claude profiles.
- Optional seven-day history and compact charts.
- WSL and explicitly selected remote-session sources.
- Validated hook-based activity for better fidelity.
- Full keyboard interaction with an explicitly activated overlay surface.
- Optional passive click-through mode.
- Multiple overlays and advanced virtual-desktop behavior.
- Windows ARM64 packaging.
- Trusted automatic update installation and recovery.
- Additional quota categories within the same two service families when verified.

### Explicitly out of scope

- Every other provider.
- Electron, a web frontend, and a framework rewrite without a demonstrated Avalonia blocker.
- Browser-cookie scraping or embedded authentication.
- Owning or racing provider token refresh.
- Sending prompts or controlling assistant execution.
- Reading conversation content for status inference.
- General API billing dashboards.
- Token-to-subscription-quota estimates.
- Cloud telemetry or account synchronization.
- Elevated background services, drivers, or input injection.
- Guaranteed overlay display on protected or exclusive-full-screen surfaces.

## 12. Risks and decisions

### Ranked risks

| Rank | Risk | Impact | Likelihood | Mitigation |
|---|---|---|---|---|
| 1 | Undocumented usage endpoint changes | High | High | Isolated adapters, versioned fixtures, bounded compatibility probes, honest unavailable state |
| 2 | Authentication/storage changes | High | High | Verified mode selection, exact OS-store access, source generations, no token refresh |
| 3 | Click-through blocks user input | High | Medium | Native regions, cross-process tests, hidden-window escape path, early prototype gate |
| 4 | Overlay steals focus | High | Medium | Pre-show no-activate styles, mouse-activation handling, foreground-window assertions |
| 5 | Wrong-account cached readings | High | Medium | Account partitioning, conservative identity epochs, atomic invalidation |
| 6 | Provider local schemas change | Medium | High | Capability probes, schema-specific readers, activity failure isolated from quota |
| 7 | DPI or monitor changes break placement | High | Medium | Pure geometry, one conversion boundary, hotplug/RDP tests |
| 8 | Rate limits become stricter | Medium | High | Five-minute idle cadence, single-flight requests, persisted server deadlines |
| 9 | Activity inference is inaccurate | Medium | High | Explicit fidelity, short expiry, unknown state, no waiting inference from silence |
| 10 | Credential or session metadata leaks | High | Medium | In-memory token leases, allowlisted logging, no raw fixtures or telemetry |
| 11 | Packaging or upgrade damages availability | High | Medium | Stable MSI identity, signed artifacts, running-process handling, clean-VM upgrade tests |
| 12 | Runtime loses support before release | High | High if delayed | Mandatory supported-LTS migration before .NET 8 end of support |
| 13 | Watchers or animations consume excess resources | Medium | Medium | Bounded reconciliation, hidden-state suspension, soak profiling |
| 14 | Avalonia native behavior varies by renderer/version | High | Medium | Pinned versions, exact-version API checks, GPU and fallback-renderer validation |

### Decisions to settle during the early prototype

**Avalonia remains the chosen UI framework.** No inspected evidence demonstrates a limitation requiring WPF. If the prototype fails, isolate a reproducible no-activate, transparency, or region problem against the selected Avalonia version and renderer before considering a fallback.

**Use a per-user MSI for this utility.** This changes modshell-cs's per-machine deployment intentionally because UseNotch needs neither administrative runtime access nor machine-wide configuration.

**Keep updates simple for MVP.** Check releases only on explicit user request, show version and publisher information, and use the signed MSI upgrade path. If the application downloads an installer, verify expected origin and publisher before offering to launch it. Do not add an unsigned self-updater or overwrite installed binaries directly.

**Treat unavailable scope as a valid product outcome.** Supporting OpenAI and Anthropic does not mean inventing every quota their products might expose. Compatibility documentation must name the supported authentication modes, source versions, and quota categories.

**Preserve architectural lessons without copying source weaknesses.** Retain Codenotch's connection generations and stable headlines, while correcting generic immutable-SQLite fallback, ambiguous activity defaults, and capped server backoff. Retain modshell-cs's XAML, MVVM, tray, gauge, and packaging patterns while removing elevation, hardware collection, and constructor-owned polling.

### Recommended first coding task

Create the minimal Avalonia application and `IOverlayPlatform` Windows implementation that displays a static two-provider overlay. Include a tray Quit command, non-elevated manifest, work-area placement, non-activating interaction, and explicit native input regions.

The deliverable is a Windows prototype that proves focus preservation and click-through into another process before any live authentication work begins.

### First vertical slice to validate

Validate this complete path:

**Selected Codex source -> credential discovery -> one quota request -> normalized snapshot -> state store -> overlay ring -> detail panel -> sanitized last-good cache -> offline restart -> tray shutdown.**

Then disable and immediately re-enable the provider while a response is delayed. The old result must not reappear or overwrite the cache.

### Architectural decisions that should not be changed casually

- Avalonia owns presentation; isolated Windows services own native behavior.
- The application runs as the current user without elevation.
- Provider credentials remain owned and refreshed by the originating tools.
- Usage and activity are separate streams.
- Quota scope, fidelity, authentication, and freshness remain explicit.
- Missing data never becomes fabricated availability.
- Results are accepted only for the current connection, credential, and account generation.
- All refresh paths share one scheduler and respect server backoff.
- Provider files and databases are read-only integration sources.
- Tokens and raw provider payloads never enter normal configuration, history, logs, or view models.
- Native overlay behavior is a tested release requirement.
- Exactly the two requested service families define the product boundary.
