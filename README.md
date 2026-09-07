# UseNotch

A non-elevated Windows tray utility that shows OpenAI Codex and Anthropic Claude Code usage in a small
screen-edge overlay.

UseNotch borrows the credentials those tools already store on this machine, read-only. It never refreshes
a token, never writes to a provider's installation, never signs anything out, and never reads
conversation content.

## What it shows

- The supported quota windows each provider exposes, with the window each number belongs to, its reset
  time, and how old the reading is.
- Local activity where it can be observed, with estimates labelled as estimates.
- An honest unavailable state whenever a value is missing. A missing reading is never shown as zero.

Hovering the overlay expands it; clicking a provider opens a detail panel. Everything the overlay shows
is also reachable by keyboard in the Settings window.

## Build and run

Use Windows 11 x64 and the .NET 8 SDK pinned in [global.json](global.json), currently 8.0.424. Patch
updates within that SDK feature band are allowed. The SDK is separate from the runtime: an installed
.NET 9 SDK alone does not satisfy this pin.

From the repository root in a non-elevated shell:

```powershell
dotnet restore UseNotch.sln --locked-mode
dotnet build UseNotch.sln -c Release --no-restore
dotnet run --project src/UseNotch.App -c Release --no-build --no-restore
```

For a machine without the pinned SDK, install it using the
[official .NET installation instructions](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
An isolated SDK under the ignored `.tmp/dotnet` directory also works: use `.\.tmp\dotnet\dotnet.exe`
instead of `dotnet`.

.NET 8 support ends on 2026-11-10. Migration to a supported LTS is a release gate, recorded in the build
plan. [Microsoft lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core)

## Verify

```powershell
dotnet test UseNotch.sln -c Release --no-build --no-restore --list-tests
dotnet test UseNotch.sln -c Release --no-build --no-restore --logger trx --results-directory TestResults
dotnet format UseNotch.sln --verify-no-changes --no-restore
```

Interactive checks need a real, non-elevated desktop session. Hosted CI success is not evidence that any
of them passed.

| Check | What it proves |
|---|---|
| `scripts/Test-DesktopLaunch.ps1` | A real window, the bound title, a non-elevated token, and PerMonitorV2 |
| `scripts/Test-OverlayBehavior.ps1` | Input in transparent space reaches another process, hovering expands without stealing focus, and a cell opens details without activating the overlay |
| `scripts/Test-OverlayPlacement.ps1` | Every edge places the overlay inside the monitor work area, respecting taskbar offsets |
| `scripts/Test-LifecycleBehavior.ps1` | A duplicate launch hands over to the running instance, and quit leaves no orphan process |
| `scripts/Test-LiveProviderSlice.ps1` | The full live path for both providers, including offline restart from cache. It contacts the real endpoints with the signed-in accounts |
| `scripts/Measure-Resources.ps1` | Idle CPU, working set, handles, threads, and cache growth over a bounded run |
| `scripts/Test-VersionAgreement.ps1` | The application, the installer, and an optional release tag agree |

Add `--software-render` to use the supported software-rendering fallback instead of the GPU path.

## Package

```powershell
dotnet tool restore
pwsh -NoProfile -File installer/build-installer.ps1
pwsh -NoProfile -File installer/Test-Installer.ps1 -MsiPath (Get-Item artifacts/installer/*.msi).FullName
```

This produces a per-user, self-contained MSI. See [installer/README.md](installer/README.md) for install
scope, upgrade and downgrade behaviour, and what uninstall does and does not remove.

## Structure

| Project | Responsibility |
|---|---|
| `UseNotch.Domain` | Immutable usage models; no package or project dependencies |
| `UseNotch.Application` | Use cases, polling, activity, state, cache, settings contracts; depends only on Domain |
| `UseNotch.Providers.OpenAI` | Codex discovery, credentials, usage and activity adapters |
| `UseNotch.Providers.Anthropic` | Claude Code discovery, credentials, usage and activity adapters |
| `UseNotch.Infrastructure` | JSON settings, application paths, sanitized diagnostics |
| `UseNotch.Platform.Windows` | Isolated Win32 services: overlay, monitors, single instance, startup, DPAPI |
| `UseNotch.App` | Avalonia composition root, views, and view models |

The layer boundaries are enforced by tests. Provider formats, HTTP details, native handles, and
credential parsing stay inside their adapters.

## Privacy and data

| Location | Contents |
|---|---|
| In memory only | Borrowed access tokens, for the lifetime of one request |
| `%LOCALAPPDATA%\UseNotch\settings.json` | Enabled providers, placement, scale, privacy, startup preference |
| `%LOCALAPPDATA%\UseNotch\cache` | Sanitized last-good quota readings and opaque account partitions |
| `%LOCALAPPDATA%\UseNotch\logs` | Only when diagnostics are enabled; sanitized and size-capped |

No refresh token is ever parsed or stored, no response body is ever written to disk, and no history
database exists. Clearing UseNotch data from Settings removes only what this application owns.

## Continue implementation

Read [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) for the design and
[DEVELOPMENT_BUILD_PLAN.md](DEVELOPMENT_BUILD_PLAN.md) for the current milestone, checkboxes, evidence,
commit checkpoints, and handoff instructions.

The architecture is informed by the inspected modshell-cs and Codenotch repositories. No upstream source
files or visual assets were copied. No project license has been selected; dependency licenses remain
their respective owners' licenses.
