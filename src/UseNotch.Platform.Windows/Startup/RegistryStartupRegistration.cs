using Microsoft.Win32;
using UseNotch.Application;

namespace UseNotch.Platform.Windows.Startup;

/// <summary>
/// Opt-in launch at login through the current user's Run key. Nothing machine-wide is written, so no
/// elevation is required, and the registered command is always read back rather than assumed.
/// </summary>
public sealed class RegistryStartupRegistration(string? executablePath = null, string valueName = "UseNotch") : IStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string? _executablePath = executablePath ?? CurrentExecutablePath();

    public StartupRegistrationState Read()
    {
        if (_executablePath is null)
        {
            return StartupRegistrationState.Unavailable;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(valueName) is not string registered || string.IsNullOrWhiteSpace(registered))
            {
                return StartupRegistrationState.NotRegistered;
            }

            // A registration left behind by a different build, or by a development copy, must be reported
            // as such instead of being shown as this application's own launch-at-login state.
            return string.Equals(Unquote(registered), _executablePath, StringComparison.OrdinalIgnoreCase)
                ? StartupRegistrationState.RegisteredForThisApplication
                : StartupRegistrationState.RegisteredElsewhere;
        }
        catch (System.Security.SecurityException)
        {
            return StartupRegistrationState.Unavailable;
        }
        catch (UnauthorizedAccessException)
        {
            return StartupRegistrationState.Unavailable;
        }
        catch (IOException)
        {
            return StartupRegistrationState.Unavailable;
        }
    }

    public bool TryRegister()
    {
        if (_executablePath is null)
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(valueName, "\"" + _executablePath + "\"", RegistryValueKind.String);
            return Read() == StartupRegistrationState.RegisteredForThisApplication;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public bool TryUnregister()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return true;
            }

            key.DeleteValue(valueName, throwOnMissingValue: false);
            return Read() != StartupRegistrationState.RegisteredForThisApplication;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1] : trimmed;
    }

    private static string? CurrentExecutablePath()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }
}
