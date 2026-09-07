using System.Xml.Linq;

namespace UseNotch.UI.Tests;

public class TrayShellTests
{
    [Fact]
    public void Tray_menu_keeps_unimplemented_actions_disabled()
    {
        var projectDirectory = FindProjectDirectory();
        var document = XDocument.Load(Path.Combine(projectDirectory, "App.axaml"));
        var menuItems = document
            .Descendants()
            .Where(element => element.Name.LocalName == "NativeMenuItem")
            .ToDictionary(
                element => (string?)element.Attribute("Header") ?? string.Empty,
                element => (string?)element.Attribute("IsEnabled"));

        Assert.Null(menuItems["Status and settings"]);
        Assert.Null(menuItems["Quit"]);
        Assert.Null(menuItems["Show overlay"]);
        Assert.Equal("False", menuItems["Pin overlay"]);
        Assert.Equal("False", menuItems["Refresh usage"]);
        Assert.Equal("False", menuItems["Pause monitoring"]);
    }

    private static string FindProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UseNotch.App");
            if (File.Exists(Path.Combine(candidate, "App.axaml")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the UseNotch.App project directory.");
    }
}
