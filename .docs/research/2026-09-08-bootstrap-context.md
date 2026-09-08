# Bootstrap context: what is UseNotch and how is it built

**Question:** What is this project and how is it built?

**Short answer:** UseNotch is a non-elevated Windows 11 x64 tray utility (.NET 8, Avalonia 11) that shows OpenAI Codex and Anthropic Claude Code usage quotas in a screen-edge overlay ("the notch"), read-only against each tool's own local credentials. It is a strict seven-project layered solution (Domain -> Application -> Providers/Infrastructure -> Platform.Windows/App) with layer boundaries enforced by tests, built and verified via `dotnet` with a pinned SDK, and packaged as a per-user WiX v5 MSI. Development runs against a milestone ledger (`DEVELOPMENT_BUILD_PLAN.md`), not ad-hoc commits.

**Evidence:**
- Stack: .NET 8 SDK pinned to `8.0.424` in `global.json`; Avalonia 11.3.20 + MVVM 8.4.2 pinned centrally in `Directory.Packages.props`; solution file `UseNotch.sln`.
- Layout (`README.md` "Structure" table, confirmed against `src/`):
  - `src/UseNotch.Domain` - immutable usage models, no dependencies.
  - `src/UseNotch.Application` - use cases, polling, activity, state, cache, settings contracts; depends only on Domain.
  - `src/UseNotch.Providers.OpenAI`, `src/UseNotch.Providers.Anthropic` - discovery, credentials, usage/activity adapters per provider.
  - `src/UseNotch.Infrastructure` - JSON settings, application paths, sanitized diagnostics.
  - `src/UseNotch.Platform.Windows` - Win32 overlay, monitors, single instance, startup, DPAPI (`Overlay/`, `Security/`, `Session/`, `SingleInstance/`, `Startup/`).
  - `src/UseNotch.App` - Avalonia composition root, views, view models (`Controls/`, `Lifecycle/`, `Overlay/`, `ViewModels/`, `Views/`).
  - Tests mirror this: `tests/UseNotch.Domain.Tests`, `.Application.Tests`, `.Provider.Tests`, `.UI.Tests`, `.Windows.Tests`, plus `tests/OverlayInputHarness` (independent-process input probe) and `tests/Shared`/`tests/Fixtures`.
- Commands (README "Build and run" / "Verify"):
  - `dotnet restore UseNotch.sln --locked-mode`
  - `dotnet build UseNotch.sln -c Release --no-restore`
  - `dotnet test UseNotch.sln -c Release --no-build --no-restore --logger trx --results-directory TestResults`
  - `dotnet format UseNotch.sln --verify-no-changes --no-restore`
  - `git diff --check`
  - An isolated SDK under the ignored `.tmp/dotnet` is a documented fallback when the pinned SDK is not installed system-wide (`.\.tmp\dotnet\dotnet.exe`).
- Interactive/native checks are separate and cannot be assumed passed from CI: `scripts/Test-DesktopLaunch.ps1`, `Test-OverlayBehavior.ps1`, `Test-OverlayPlacement.ps1`, `Test-LifecycleBehavior.ps1`, `Test-LiveProviderSlice.ps1`, `Measure-Resources.ps1`, `Test-VersionAgreement.ps1`; packaging via `installer/build-installer.ps1` and `installer/Test-Installer.ps1`.
- Governance: root `AGENTS.md` (this bootstrap's routing index now folds in the project's pre-existing instructions in its Project-specific section) plus `.docs/plans/DEVELOPMENT_BUILD_PLAN.md`, an extremely detailed, dated, evidence-logged milestone ledger (M00-M14) with its own resume protocol, session-state block, and checkpoint commit convention. `.docs/plans/IMPLEMENTATION_PLAN.md` holds the original design. Both predate and are independent of this bootstrap's dated-file `.docs/plans/` mechanism; see `.docs/rules/plan-execution.md`'s note on the relationship. Both were moved from the repo root into `.docs/plans/` during this bootstrap, per the user's explicit instruction to relocate pre-existing non-bootstrap docs; all internal links were updated to match.
- Status at bootstrap time (`.docs/plans/DEVELOPMENT_BUILD_PLAN.md` Session state table): M00-M11 complete; M12 packaging implemented and structurally validated but its disposable-environment install matrix is open; M13-M14 not started; `v0.1.6` is the published latest release (unsigned).
- Hard project rules already in force (pre-existing root `AGENTS.md`, folded into this file's Project-specific section): no em dashes/AI attribution/co-author trailers anywhere; monitor only OpenAI/Codex and Anthropic/Claude Code; never commit credentials or raw account data; build with the pinned SDK; centralized package versions in `Directory.Packages.props`.

**Open questions:**
- Whether `CODENOTCH_WINDOWS_PORT_BRIEF.md` and `IMPLEMENTATION_PLAN.md` are still live references or historical design notes only - left in place, not summarized further here.
- No CI workflow file was inspected in this pass beyond what `.docs/plans/DEVELOPMENT_BUILD_PLAN.md` describes (packaging runs per PR, release runs per tag); read `.github/workflows/` directly before changing CI behavior.
