<div align="center">

<img src="docs/brand/usenotch-128.png" width="96" height="96" alt="UseNotch">

# UseNotch

**Know how much Codex and Claude Code you have left, without leaving your screen.**

A non-elevated Windows tray utility that shows OpenAI Codex and Anthropic Claude Code usage in a small
screen-edge overlay. Invisible until you reach for it.

[![CI](https://github.com/FIEF-nohell/UseNotch/actions/workflows/ci.yml/badge.svg)](https://github.com/FIEF-nohell/UseNotch/actions/workflows/ci.yml)
[![Package](https://github.com/FIEF-nohell/UseNotch/actions/workflows/package.yml/badge.svg)](https://github.com/FIEF-nohell/UseNotch/actions/workflows/package.yml)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](global.json)
[![Windows 11](https://img.shields.io/badge/Windows-11%20x64-0078D4)](#requirements)

</div>

---

## Why

Both tools tell you that you have run out. Neither tells you that you are about to. UseNotch puts the
answer at the edge of the screen: a ring per provider, the window each number belongs to, and when it
resets.

It reads the credentials those tools already store on this machine, read-only. It never refreshes a
token, never writes to a provider's installation, never signs anything out, and never reads conversation
content.

## What it does

| | |
|---|---|
| **Invisible at rest** | Nothing is drawn until the pointer reaches the screen edge. An idle desktop shows no trace of the window, and clicks pass straight through to whatever is underneath |
| **Every quota window** | Each window a provider exposes, labelled with what it measures, when it resets, and how old the reading is |
| **Honest about gaps** | A missing value is shown as unavailable, never as zero. Estimates are labelled as estimates |
| **Survives a restart** | The last good reading is cached, so a cold start offline still shows something true, marked with its age |
| **Stays out of the way** | Non-elevated, no taskbar entry, never steals focus, and reachable entirely by keyboard from the settings window |

Hover the edge to expand the notch. Click a provider to open its detail panel.

## Requirements

Windows 11 x64. Nothing else: the installer ships a self-contained build, so no separately installed
.NET runtime is needed.

## Install

Download the MSI from [Releases](https://github.com/FIEF-nohell/UseNotch/releases) and run it. It
installs per user under `%LOCALAPPDATA%`, needs no administrator rights, and adds a Start menu entry.

> **Not yet signed.** Releases are currently drafts and the MSI carries no code signature, so
> SmartScreen will warn. Signing and a clean-machine install matrix are open release gates, tracked in
> [DEVELOPMENT_BUILD_PLAN.md](DEVELOPMENT_BUILD_PLAN.md). Install it only if you are comfortable with
> that.

Uninstall removes the application and its launch-at-login entry. Settings and cache under
`%LOCALAPPDATA%\UseNotch` are your own data and are deliberately left in place.

## Privacy and data

| Location | Contents |
|---|---|
| In memory only | Borrowed access tokens, for the lifetime of one request |
| `%LOCALAPPDATA%\UseNotch\settings.json` | Enabled providers, placement, scale, privacy, startup preference |
| `%LOCALAPPDATA%\UseNotch\cache` | Sanitized last-good quota readings and opaque account partitions |
| `%LOCALAPPDATA%\UseNotch\logs` | Only when diagnostics are enabled; sanitized and size-capped |

No refresh token is ever parsed or stored, no response body is ever written to disk, and no history
database exists. Clearing UseNotch data from Settings removes only what this application owns.

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

Add `--software-render` to use the supported software-rendering fallback instead of the GPU path.

.NET 8 support ends on 2026-11-10. Migration to a supported LTS is a release gate, recorded in the build
plan. [Microsoft lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core)

## Verify

```powershell
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

## Package

```powershell
dotnet tool restore
pwsh -NoProfile -File installer/build-installer.ps1
pwsh -NoProfile -File installer/Test-Installer.ps1 -MsiPath (Get-Item artifacts/installer/*.msi).FullName
```

See [installer/README.md](installer/README.md) for install scope, upgrade and downgrade behaviour, and
what uninstall does and does not remove.

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

## Brand

The mark is a quota ring with a notch cut through it, in the two provider accents the overlay already
uses. It is generated, not drawn by hand, so the vector and the icon cannot drift apart:

```powershell
python scripts/New-AppIcon.py
```

That writes [`src/UseNotch.App/Assets/usenotch.svg`](src/UseNotch.App/Assets/usenotch.svg), the
multi-size `usenotch.ico`, and the PNGs under `docs/brand`.

## Status

M00 to M11 are complete. Packaging works and is validated structurally; the disposable-environment
install matrix, the remaining hardware-dependent checks, and release hardening are open. Read
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) for the design and
[DEVELOPMENT_BUILD_PLAN.md](DEVELOPMENT_BUILD_PLAN.md) for milestones, evidence, and what is still
unproven.

## Notices

OpenAI and Codex are trademarks of OpenAI. Anthropic and Claude are trademarks of Anthropic. UseNotch is
an independent tool, not affiliated with or endorsed by either. Provider marks used in the interface are
covered by [Assets/Marks/NOTICE.md](src/UseNotch.App/Assets/Marks/NOTICE.md).

The architecture is informed by the inspected modshell-cs and Codenotch repositories. No upstream source
files or visual assets were copied. No project license has been selected; dependency licenses remain
their respective owners' licenses.
