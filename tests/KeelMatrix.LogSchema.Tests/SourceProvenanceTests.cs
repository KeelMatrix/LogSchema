using KeelMatrix.LogSchema;

namespace KeelMatrix.LogSchema.Tests;

public sealed class SourceProvenanceTests
{
    [Fact]
    public void LinkedOutsideSourceCannotCollideWithInProjectTailPath()
    {
        var projectDirectory = Path.Combine(Path.GetTempPath(), "logschema-provenance", "service");

        var inProject = SourceProvenance.NormalizeProjectRelativePath(
            projectDirectory,
            Path.Combine(projectDirectory, "Shared", "Logging.cs"));
        var linkedOutside = SourceProvenance.NormalizeProjectRelativePath(
            projectDirectory,
            Path.Combine(projectDirectory, "..", "Shared", "Logging.cs"));

        Assert.Equal("project/Shared/Logging.cs", inProject);
        Assert.Equal("external/up-1/Shared/Logging.cs", linkedOutside);
        Assert.NotEqual(inProject, linkedOutside);
        Assert.DoesNotContain("..", linkedOutside, StringComparison.Ordinal);
    }

    [Fact]
    public void DotPrefixedLogicalNamesArePreserved()
    {
        var projectDirectory = Path.Combine(Path.GetTempPath(), "logschema-provenance", "service");

        var logicalPath = SourceProvenance.NormalizeProjectRelativePath(
            projectDirectory,
            Path.Combine(projectDirectory, ".hidden", ".Logging.cs"));

        Assert.Equal("project/.hidden/.Logging.cs", logicalPath);
    }

    [Fact]
    public void PathSeparatorSpellingsShareOneLogicalIdentity()
    {
        var projectDirectory = Path.Combine(Path.GetTempPath(), "logschema-provenance", "service");

        var slashPath = SourceProvenance.NormalizeProjectRelativePath(projectDirectory, "folder/Logging.cs");
        var backslashPath = SourceProvenance.NormalizeProjectRelativePath(projectDirectory, "folder\\Logging.cs");

        Assert.Equal(slashPath, backslashPath);
    }

    [Fact]
    public void GeneratedIdentityPreservesGeneratorAndHintContextAcrossRoots()
    {
        var firstRoot = SourceProvenance.NormalizeGeneratedPath(
            @"C:\checkout-one\obj\Debug\net8.0\generated\Generator.One\Hint.g.cs");
        var secondRoot = SourceProvenance.NormalizeGeneratedPath(
            @"C:\checkout-two\obj\Debug\net8.0\generated\Generator.One\Hint.g.cs");
        var differentGenerator = SourceProvenance.NormalizeGeneratedPath(
            @"C:\checkout-two\obj\Debug\net8.0\generated\Generator.Two\Hint.g.cs");

        Assert.Equal("generated/Generator.One/Hint.g.cs", firstRoot);
        Assert.Equal(firstRoot, secondRoot);
        Assert.NotEqual(firstRoot, differentGenerator);
        Assert.DoesNotContain("checkout", firstRoot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..", firstRoot, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedIdentityCollisionFailsClosed()
    {
        var registry = new SourceProvenanceRegistry();
        var firstTree = new object();
        var secondTree = new object();

        Assert.True(registry.TryRegister(
            "generated",
            "generated/Generator.One/Hint.g.cs",
            firstTree));
        Assert.False(registry.TryRegister(
            "generated",
            "generated/Generator.One/Hint.g.cs",
            secondTree));
        Assert.True(registry.TryRegister(
            "generated",
            "generated/Generator.Two/Hint.g.cs",
            secondTree));
    }

    [Fact]
    public void GeneratedPathWithoutStableGeneratorContextFailsClosed()
    {
        Assert.Throws<ProjectAnalysisException>(() => SourceProvenance.NormalizeGeneratedPath(
            @"C:\checkout\obj\Debug\net8.0\LoggerMessage.g.cs"));
    }
}
