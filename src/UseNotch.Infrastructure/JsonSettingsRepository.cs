using System.Text.Json;
using System.Text.Json.Serialization;
using UseNotch.Application;

namespace UseNotch.Infrastructure;

/// <summary>
/// Stores settings as one small JSON document under the application's own local folder. Writes are
/// atomic, an unreadable document is preserved beside the recovered defaults instead of being deleted,
/// and nothing secret is ever written here.
/// </summary>
public sealed class JsonSettingsRepository : ISettingsRepository
{
    public const string FileName = "settings.json";
    private const long MaximumSettingsBytes = 256 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public JsonSettingsRepository(string? directory = null)
        => _directory = directory ?? ApplicationPaths.Root;

    public string Path => System.IO.Path.Combine(_directory, FileName);

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var path = Path;
        if (!File.Exists(path))
        {
            return new SettingsLoadResult(UseNotchSettings.Default, SettingsLoadOutcome.Defaults, null);
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumSettingsBytes)
            {
                return Recover(path, "size");
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var stored = await JsonSerializer.DeserializeAsync<UseNotchSettings>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return Recover(path, "empty");
            }

            var normalized = SettingsValidator.Normalize(stored);
            var outcome = stored.SchemaVersion == UseNotchSettings.CurrentSchemaVersion
                ? SettingsLoadOutcome.Loaded
                : SettingsLoadOutcome.Migrated;
            return new SettingsLoadResult(normalized, outcome, null);
        }
        catch (JsonException)
        {
            return Recover(path, "unreadable");
        }
        catch (IOException)
        {
            // A transient read failure must not silently replace the user's settings on disk.
            return new SettingsLoadResult(UseNotchSettings.Default, SettingsLoadOutcome.Defaults, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new SettingsLoadResult(UseNotchSettings.Default, SettingsLoadOutcome.Defaults, null);
        }
    }

    public async Task SaveAsync(UseNotchSettings settings, CancellationToken cancellationToken)
    {
        var path = Path;
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_directory);
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, SettingsValidator.Normalize(settings), SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static SettingsLoadResult Recover(string path, string reason)
    {
        var recoveredPath = path + ".invalid";
        try
        {
            File.Move(path, recoveredPath, true);
        }
        catch (IOException)
        {
            recoveredPath = path;
        }
        catch (UnauthorizedAccessException)
        {
            recoveredPath = path;
        }

        return new SettingsLoadResult(UseNotchSettings.Default, SettingsLoadOutcome.Recovered, reason);
    }
}

/// <summary>
/// Every path this application owns, resolved through the known local application data folder. Settings
/// stay local because they contain machine-specific paths.
/// </summary>
public static class ApplicationPaths
{
    public const string ApplicationFolderName = "UseNotch";

    public static string Root => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        ApplicationFolderName);

    public static string Cache => System.IO.Path.Combine(Root, "cache");

    public static string Logs => System.IO.Path.Combine(Root, "logs");

    public static string Secrets => System.IO.Path.Combine(Root, "secrets");

    /// <summary>
    /// Removes only the data this application owns. Provider installations are never touched, so clearing
    /// UseNotch data can never sign another tool out.
    /// </summary>
    public static IReadOnlyList<string> ClearOwnedData()
    {
        var removed = new List<string>();
        foreach (var directory in new[] { Cache, Logs })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, true);
                removed.Add(directory);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }
}
