using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using UseNotch.Application;
using UseNotch.Platform.Windows.Security;
using UseNotch.Platform.Windows.Startup;

namespace UseNotch.Windows.Tests;

[SupportedOSPlatform("windows")]
public class DpapiSecretStoreTests
{
    [Fact]
    public void Protected_material_round_trips_for_the_current_user()
    {
        var store = new DpapiSecretStore();
        var plaintext = Encoding.UTF8.GetBytes("account-partition-key");

        var protectedData = store.Protect(plaintext);

        Assert.NotEqual(plaintext, protectedData);
        Assert.Equal(plaintext, store.TryUnprotect(protectedData));
    }

    [Fact]
    public void The_protected_form_never_contains_the_plaintext()
    {
        var store = new DpapiSecretStore();
        var plaintext = Encoding.UTF8.GetBytes("account-partition-key");

        var protectedData = store.Protect(plaintext);

        Assert.DoesNotContain("account-partition-key", Encoding.UTF8.GetString(protectedData), StringComparison.Ordinal);
    }

    [Fact]
    public void Material_protected_with_different_entropy_is_reported_as_unreadable_rather_than_throwing()
    {
        // Different entropy stands in for another application's, or another user's, protected blob: the
        // store must report it as unreadable instead of failing whichever feature asked for it.
        var written = new DpapiSecretStore(Encoding.UTF8.GetBytes("other-entropy"));
        var reader = new DpapiSecretStore();

        var protectedData = written.Protect(Encoding.UTF8.GetBytes("secret"));

        Assert.Null(reader.TryUnprotect(protectedData));
    }

    [Fact]
    public void Corrupted_material_is_reported_as_unreadable()
    {
        var store = new DpapiSecretStore();
        var protectedData = store.Protect(Encoding.UTF8.GetBytes("secret"));
        protectedData[^1] ^= 0xFF;

        Assert.Null(store.TryUnprotect(protectedData));
    }

    [Fact]
    public void Protection_uses_the_current_user_scope_so_another_account_cannot_read_it()
    {
        var store = new DpapiSecretStore();
        var protectedData = store.Protect(Encoding.UTF8.GetBytes("secret"));

        // A machine-scope reader would succeed on a machine-scoped blob. Failing here is the evidence
        // that the blob is bound to this user rather than to the machine.
        Assert.Throws<CryptographicException>(
            () => ProtectedData.Unprotect(protectedData, null, DataProtectionScope.LocalMachine));
    }
}

[SupportedOSPlatform("windows")]
public sealed class RegistryStartupRegistrationTests : IDisposable
{
    private readonly string _valueName = "UseNotch.Test." + Guid.NewGuid().ToString("N");
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private RegistryStartupRegistration CreateRegistration(string executablePath = @"C:\fixture\UseNotch.exe")
        => new(executablePath, _valueName);

    [Fact]
    public void An_unregistered_application_reports_that_it_is_not_registered()
        => Assert.Equal(StartupRegistrationState.NotRegistered, CreateRegistration().Read());

    [Fact]
    public void Registering_and_unregistering_are_both_read_back_from_the_registry()
    {
        var registration = CreateRegistration();

        Assert.True(registration.TryRegister());
        Assert.Equal(StartupRegistrationState.RegisteredForThisApplication, registration.Read());

        Assert.True(registration.TryUnregister());
        Assert.Equal(StartupRegistrationState.NotRegistered, registration.Read());
    }

    [Fact]
    public void Registration_is_written_only_under_the_current_user()
    {
        var registration = CreateRegistration();

        registration.TryRegister();

        using var userKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        Assert.NotNull(userKey?.GetValue(_valueName));

        using var machineKey = Registry.LocalMachine.OpenSubKey(RunKeyPath);
        Assert.Null(machineKey?.GetValue(_valueName));
    }

    [Fact]
    public void A_registration_pointing_at_another_location_is_reported_as_registered_elsewhere()
    {
        CreateRegistration(@"C:\somewhere-else\UseNotch.exe").TryRegister();

        // The same value name now holds a different executable, which is exactly the development-path
        // mistake this state exists to surface.
        Assert.Equal(StartupRegistrationState.RegisteredElsewhere, CreateRegistration().Read());
    }

    [Fact]
    public void Unregistering_a_missing_value_succeeds_instead_of_failing()
        => Assert.True(CreateRegistration().TryUnregister());

    public void Dispose()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}

[SupportedOSPlatform("windows")]
public sealed class OwnedDataDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UseNotch.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_restricted_directory_grants_access_only_to_the_current_user()
    {
        OwnedDataDirectory.CreateRestricted(_root);

        var granted = OwnedDataDirectory.ReadGrantedIdentities(_root);
        var current = WindowsIdentity.GetCurrent().User!.Value;

        Assert.Equal([current], granted);
    }

    [Fact]
    public void Creating_a_restricted_directory_twice_is_safe()
    {
        OwnedDataDirectory.CreateRestricted(_root);
        var second = OwnedDataDirectory.CreateRestricted(_root);

        Assert.True(second.Exists);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
