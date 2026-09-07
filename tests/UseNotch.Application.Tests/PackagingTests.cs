using System.Text.RegularExpressions;
using UseNotch.TestSupport;

namespace UseNotch.Application.Tests;

/// <summary>
/// Packaging invariants that must hold in the repository itself, so a drift is caught by the ordinary
/// test run rather than during a release.
/// </summary>
public class PackagingTests
{
    private static string ReadRepositoryFile(params string[] parts) => File.ReadAllText(RepositoryFiles.PathTo(parts));

    private static string ApplicationVersion
    {
        get
        {
            var match = Regex.Match(ReadRepositoryFile("Directory.Build.props"), @"<Version>(?<version>[^<]+)</Version>");
            Assert.True(match.Success, "Directory.Build.props does not define a Version.");
            return match.Groups["version"].Value;
        }
    }

    [Fact]
    public void The_application_version_is_a_valid_installer_product_version()
    {
        var version = ApplicationVersion;

        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        var parts = version.Split('.').Select(int.Parse).ToArray();
        Assert.InRange(parts[0], 0, 255);
        Assert.InRange(parts[1], 0, 255);
        Assert.InRange(parts[2], 0, 65535);
    }

    [Fact]
    public void The_installer_takes_its_version_from_the_build_rather_than_a_copy()
    {
        var wxs = ReadRepositoryFile("installer", "UseNotch.wxs");

        Assert.Contains("Version=\"$(var.ProductVersion)\"", wxs, StringComparison.Ordinal);
        Assert.DoesNotContain($"Version=\"{ApplicationVersion}\"", wxs, StringComparison.Ordinal);
    }

    [Fact]
    public void The_installer_identity_is_stable_and_scoped_to_the_current_user()
    {
        var wxs = ReadRepositoryFile("installer", "UseNotch.wxs");

        // The upgrade code is this product's permanent identity; changing it silently breaks upgrades.
        Assert.Contains("UpgradeCode=\"8f3d6c41-5b27-4a19-9d0e-2c7a41f6b8d5\"", wxs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Scope=\"perUser\"", wxs, StringComparison.Ordinal);
        Assert.Contains("LocalAppDataFolder", wxs, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgramFiles", wxs, StringComparison.Ordinal);
        Assert.DoesNotContain("perMachine", wxs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_downgrade_is_rejected_rather_than_overwriting_newer_files()
    {
        var wxs = ReadRepositoryFile("installer", "UseNotch.wxs");

        Assert.Contains("AllowDowngrades=\"no\"", wxs, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_removes_the_startup_registration_this_application_owns()
    {
        var wxs = ReadRepositoryFile("installer", "UseNotch.wxs");

        Assert.Contains("RemoveRegistryValue", wxs, StringComparison.Ordinal);
        Assert.Contains(@"Software\Microsoft\Windows\CurrentVersion\Run", wxs, StringComparison.Ordinal);
    }

    [Fact]
    public void The_build_tools_are_pinned_to_an_exact_version()
    {
        var manifest = ReadRepositoryFile(".config", "dotnet-tools.json");

        Assert.Contains("\"wix\"", manifest, StringComparison.Ordinal);
        Assert.Matches(@"""version"":\s*""5\.\d+\.\d+""", manifest);
    }

    [Fact]
    public void The_build_script_stops_on_a_failed_step_instead_of_packaging_partial_output()
    {
        var script = ReadRepositoryFile("installer", "build-installer.ps1");

        Assert.Contains("$ErrorActionPreference = 'Stop'", script, StringComparison.Ordinal);
        foreach (var guardedStep in new[] { "Restore failed.", "Tests failed", "Publish failed.", "Building the MSI failed." })
        {
            Assert.Contains(guardedStep, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_build_script_proves_a_cleanup_path_stays_inside_the_workspace()
    {
        var script = ReadRepositoryFile("installer", "build-installer.ps1");

        // A recursive delete must never run against a path that resolved outside the repository.
        Assert.Contains("Assert-InsideWorkspace", script, StringComparison.Ordinal);
        Assert.Contains("is outside the repository", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $safePath -Recurse -Force", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_release_workflow_never_runs_for_a_pull_request_and_asks_for_least_privilege()
    {
        var release = ReadRepositoryFile(".github", "workflows", "release.yml");

        Assert.DoesNotContain("pull_request", release, StringComparison.Ordinal);
        Assert.Contains("permissions:\n  contents: read", release.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        // A release is created as a draft; publication is R01 and needs explicit authorization.
        Assert.Contains("--draft", release, StringComparison.Ordinal);
    }

    [Fact]
    public void The_packaging_workflow_validates_before_uploading_and_bounds_retention()
    {
        var package = ReadRepositoryFile(".github", "workflows", "package.yml");

        Assert.Contains("Test-Installer.ps1", package, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", package, StringComparison.Ordinal);
        Assert.Matches(@"retention-days:\s*\d+", package);
        Assert.DoesNotContain("secrets.", package, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_output_directories_are_not_committed()
    {
        var ignore = ReadRepositoryFile(".gitignore");

        Assert.Contains("artifacts/", ignore, StringComparison.Ordinal);
    }
}
