using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace UseNotch.Platform.Windows.Security;

/// <summary>
/// Creates a directory that only the current user can read. Local application data is already per-user,
/// but app-owned secret material gets its inherited access removed as well, so a machine administrator
/// account cannot pick it up through inheritance alone.
/// </summary>
[SupportedOSPlatform("windows")]
public static class OwnedDataDirectory
{
    public static DirectoryInfo CreateRestricted(string path)
    {
        var directory = Directory.CreateDirectory(path);
        var identity = WindowsIdentity.GetCurrent().User;
        if (identity is null)
        {
            return directory;
        }

        var security = new DirectorySecurity();
        security.SetOwner(identity);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);
        return directory;
    }

    /// <summary>
    /// Reports the identities that have been granted access, so a test can prove the restriction rather
    /// than assume it.
    /// </summary>
    public static IReadOnlyList<string> ReadGrantedIdentities(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        return [.. security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => rule.IdentityReference.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
