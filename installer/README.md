# Installer

A per-user WiX v5 MSI. UseNotch runs non-elevated and owns only the current user's data, so the package
installs per user and never requires administrator rights.

## Build

```powershell
dotnet tool restore
pwsh -NoProfile -File installer/build-installer.ps1
pwsh -NoProfile -File installer/Test-Installer.ps1 -MsiPath (Get-Item artifacts/installer/*.msi).FullName
```

The build script restores locked dependencies, runs the full test suite, publishes a self-contained
application, and packages it. It stops at the first failure, so partial output is never presented as a
successful package. Output goes to the ignored `artifacts/` directory, and every directory it clears is
checked to be inside the repository before any recursive delete.

`Test-Installer.ps1` inspects the built MSI without installing it. It checks product identity, the
stable upgrade code, version agreement with the application, per-user scope, a self-contained payload,
downgrade rejection, and startup cleanup.

## What the package does

| Area | Behaviour |
|---|---|
| Scope | Per user. Files install under `%LOCALAPPDATA%\UseNotch`; nothing is written per machine |
| Runtime | Self-contained, so a standard user needs no separately installed .NET runtime |
| Shortcut | A per-user Start menu shortcut under `UseNotch` |
| Identity | `UpgradeCode` `{8F3D6C41-5B27-4A19-9D0E-2C7A41F6B8D5}`, which must never change |
| Version | Taken from `Directory.Build.props`, so the application and installer cannot drift apart |
| Upgrade | An N-1 upgrade replaces program files and leaves settings and cache untouched |
| Downgrade | Rejected with a message rather than overwriting newer files |
| Running app | Restart Manager is asked to close a running UseNotch instead of requiring a reboot |
| Uninstall | Removes installed files, the shortcut, and the launch-at-login registration UseNotch owns |
| User data | `settings.json` and the cache under `%LOCALAPPDATA%\UseNotch` are the user's own data and are deliberately kept. Clear them from Settings, under Privacy |

## Versions and tags

An MSI `ProductVersion` is numeric only, so a prerelease label lives in the release tag rather than in
the installer version. `scripts/Test-VersionAgreement.ps1` checks the application, the installer, and an
optional tag agree before anything is packaged.

## Still to validate

Clean install, N-1 upgrade, downgrade rejection, uninstall, reinstall, and interrupted upgrade must be
exercised in a disposable Windows environment. They are deliberately not run in a developer's daily
environment, so that gate stays open until a suitable environment is available.
