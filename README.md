# UseNotch

A Windows desktop utility for OpenAI / Codex and Anthropic / Claude Code usage status.

M01 provides the project foundation and a small normal Avalonia window. It does not read credentials, contact providers, poll usage, or implement the tray/overlay yet. Closing the foundation window exits the app; M02 will introduce tray-owned lifetime.

## Build and run

Use Windows 11 x64 and the .NET 8 SDK specified in [global.json](global.json), currently 8.0.424. Patch updates within that SDK feature band are allowed. The SDK is separate from the runtime: an installed .NET 9 SDK alone does not satisfy this pin.

From the repository root in a non-elevated shell:

```powershell
dotnet restore UseNotch.sln --locked-mode
dotnet build UseNotch.sln -c Release --no-restore
dotnet run --project src/UseNotch.App -c Release --no-build --no-restore
```

For a machine without the pinned SDK, install it using the [official .NET installation instructions](https://learn.microsoft.com/en-us/dotnet/core/install/windows). An isolated SDK under the ignored `.tmp/dotnet` directory also works: use `.\.tmp\dotnet\dotnet.exe` instead of `dotnet` in the commands above. This checkout was validated that way without changing the system SDK.

.NET 8 support ends on 2026-11-10. Migration to a supported LTS is a release gate, recorded in the build plan. [Microsoft lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core)

## Verify

```powershell
dotnet test UseNotch.sln -c Release --no-build --no-restore --list-tests
dotnet test UseNotch.sln -c Release --no-build --no-restore --logger trx --results-directory TestResults
dotnet format UseNotch.sln --verify-no-changes --no-restore
pwsh -NoProfile -File scripts/Test-DesktopLaunch.ps1
```

The last command requires an interactive, non-elevated Windows session. It launches and closes only its own app process, checking a real HWND, the bound title, the process token, and PerMonitorV2 awareness. It does not validate the future overlay's focus, input regions, tray, or multi-monitor behavior. Those are separate milestone gates.

Foundation tests enforce project boundaries, centralized Avalonia versions, manifest safety, and compiled XAML updates through CommunityToolkit.Mvvm. `UseNotch.Provider.Tests` is deliberately an empty scaffold until M06-M08. No provider compatibility or quota tests are claimed in M01.

CI runs restore, build, test discovery, tests, and formatting on `windows-2022`. Dependencies are centrally pinned and lock files are checked in. CI uses locked restore and pinned GitHub Action commits with read-only repository permissions. Interactive desktop testing is a local gate; GitHub-hosted CI is not proof of desktop input behavior.

## Structure

| Project | Responsibility |
|---|---|
| `UseNotch.Domain` | Future immutable models; no package or project dependencies |
| `UseNotch.Application` | Future use cases and interfaces; depends only on Domain |
| `UseNotch.Providers.OpenAI` | Future Codex adapter; depends on Application |
| `UseNotch.Providers.Anthropic` | Future Claude adapter; depends on Application |
| `UseNotch.Infrastructure` | Future JSON/cache/logging adapters; logging abstractions only for now |
| `UseNotch.Platform.Windows` | Future isolated native services; Windows target |
| `UseNotch.App` | Avalonia composition root, views, and view models; Windows target |

The empty layer projects are intentional boundaries. Domain models, repository implementations, credentials, timers, native overlay hooks, and external database access arrive in their assigned milestones. System.Text.Json is supplied by the .NET 8 framework; there is no additional JSON package or speculative persistence implementation.

The application uses Avalonia 11.3.20, FluentTheme, and CommunityToolkit.Mvvm 8.4.2. Avalonia packages share one central version. This stays on Avalonia 11 while updating the inspected modshell-cs 11.2.5 baseline. [Pinned Avalonia package](https://www.nuget.org/packages/Avalonia/11.3.20)

The manifest sets `asInvoker`, disables UIAccess, and establishes DPI awareness before windows exist. Avalonia owns UI scaling; M03 must coordinate native placement through the isolated platform layer without adding a competing process-wide DPI setter.

## Continue implementation

Read [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) for the design and [DEVELOPMENT_BUILD_PLAN.md](DEVELOPMENT_BUILD_PLAN.md) for the current milestone, checkboxes, evidence, commit checkpoints, and handoff instructions.

The architecture is informed by the inspected modshell-cs and Codenotch repositories. M01 does not copy their source files or visual assets. No project license has been selected; dependency licenses remain their respective owners' licenses. Preserve required upstream notices if source is adapted in later milestones.

Keep credentials, live account payloads, local captures, and reference clones out of this public repository. No provider access is needed to build or run the foundation tests.
