dotnet restore UseNotch.sln --locked-mode           # locked dependency restore
dotnet build UseNotch.sln -c Release --no-restore   # zero-warning Release build
dotnet test UseNotch.sln -c Release --no-build --no-restore --logger trx --results-directory TestResults   # full solution test suite
dotnet format UseNotch.sln --verify-no-changes --no-restore   # formatting verification
git diff --check                                    # whitespace/conflict-marker check

The implementer runs these after every code-changing task; the reviewer runs them before approving. If a command here stops matching reality, fix this file in the same change.

Interactive/native checks (scripts/Test-*.ps1, installer/Test-Installer.ps1) need a real non-elevated Windows desktop session and are not part of this default sequence; see the README's Verify and Package sections and .docs/plans/DEVELOPMENT_BUILD_PLAN.md for when each applies. Hosted CI success is not evidence that any interactive check passed.
