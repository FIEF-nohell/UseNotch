using System.Xml.Linq;
using UseNotch.TestSupport;

namespace UseNotch.Application.Tests;

public class ArchitectureTests
{
    [Theory]
    [InlineData("UseNotch.Application", "UseNotch.Domain")]
    [InlineData("UseNotch.Infrastructure", "UseNotch.Application")]
    [InlineData("UseNotch.Platform.Windows", "UseNotch.Application")]
    [InlineData("UseNotch.Providers.OpenAI", "UseNotch.Application")]
    [InlineData("UseNotch.Providers.Anthropic", "UseNotch.Application")]
    public void Outer_layers_only_reference_their_declared_inner_layer(string name, string dependency)
    {
        var project = XDocument.Load(RepositoryFiles.PathTo("src", name, name + ".csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension((string?)reference.Attribute("Include")))
            .ToArray();

        Assert.Equal(dependency, Assert.Single(references));
    }

    [Fact]
    public void Package_versions_are_central_and_Avalonia_versions_match()
    {
        foreach (var path in Directory.EnumerateFiles(RepositoryFiles.PathTo("src"), "*.csproj", SearchOption.AllDirectories))
        {
            var project = XDocument.Load(path);
            Assert.All(project.Descendants("PackageReference"),
                reference => Assert.Null(reference.Attribute("Version")));
        }

        var packages = XDocument.Load(RepositoryFiles.PathTo("Directory.Packages.props"));
        var avalonia = packages.Descendants("PackageVersion")
            .Where(package => ((string?)package.Attribute("Include"))?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
        Assert.All(avalonia, package => Assert.Equal("$(AvaloniaVersion)", (string?)package.Attribute("Version")));
        Assert.StartsWith("11.", packages.Descendants("AvaloniaVersion").Single().Value);
    }
}
