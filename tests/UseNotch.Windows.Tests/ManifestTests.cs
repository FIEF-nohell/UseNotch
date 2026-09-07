using System.Xml.Linq;
using UseNotch.TestSupport;

namespace UseNotch.Windows.Tests;

public class ManifestTests
{
    [Fact]
    public void Application_never_requests_elevation_or_UI_access()
    {
        var manifest = XDocument.Load(RepositoryFiles.PathTo("src", "UseNotch.App", "app.manifest"));
        var level = manifest.Descendants().Single(element => element.Name.LocalName == "requestedExecutionLevel");

        Assert.Equal("asInvoker", (string?)level.Attribute("level"));
        Assert.Equal("false", (string?)level.Attribute("uiAccess"));
    }

    [Fact]
    public void DPI_awareness_is_declared_before_window_creation()
    {
        var manifest = XDocument.Load(RepositoryFiles.PathTo("src", "UseNotch.App", "app.manifest"));
        Assert.Equal("PerMonitorV2",
            manifest.Descendants().Single(element => element.Name.LocalName == "dpiAwareness").Value);
    }
}
