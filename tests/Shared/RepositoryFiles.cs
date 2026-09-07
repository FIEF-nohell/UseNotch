namespace UseNotch.TestSupport;

internal static class RepositoryFiles
{
    public static string Root { get; } = FindRoot();

    public static string PathTo(params string[] parts) =>
        Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "UseNotch.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Run repository tests from a UseNotch checkout.");
    }
}
