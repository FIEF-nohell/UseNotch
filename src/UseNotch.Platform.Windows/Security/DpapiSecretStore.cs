using System.Runtime.Versioning;
using System.Security.Cryptography;
using UseNotch.Application;

namespace UseNotch.Platform.Windows.Security;

/// <summary>
/// Protects small app-owned secret material with the current user's DPAPI scope, so another account on
/// the same machine cannot read it. Borrowed provider tokens never reach this store; they exist only in
/// memory for the lifetime of a request.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(byte[]? entropy = null) : ISecretStore
{
    private static readonly byte[] DefaultEntropy = "UseNotch.AccountPartition.v1"u8.ToArray();
    private readonly byte[] _entropy = entropy ?? DefaultEntropy;

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);
    }

    public byte[]? TryUnprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        try
        {
            return ProtectedData.Unprotect(protectedData, _entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Written by another user, on another machine, or corrupted. Report it as unreadable rather
            // than throwing at whichever feature happened to ask for it.
            return null;
        }
    }
}
