using System.Xml.Linq;
using UseNotch.TestSupport;

namespace UseNotch.Domain.Tests;

public class BoundaryTests
{
    [Fact]
    public void Domain_has_no_external_or_project_dependencies()
    {
        var project = XDocument.Load(RepositoryFiles.PathTo("src", "UseNotch.Domain", "UseNotch.Domain.csproj"));

        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.DoesNotContain(project.Descendants("TargetFramework"),
            framework => framework.Value.Contains("windows", StringComparison.OrdinalIgnoreCase));
    }
}
