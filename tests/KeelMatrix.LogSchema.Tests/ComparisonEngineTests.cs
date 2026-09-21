using KeelMatrix.LogSchema;

namespace KeelMatrix.LogSchema.Tests;

public sealed class ComparisonEngineTests
{
    [Fact]
    public void AddedEventIsInformationalAndDoesNotGateBreaking()
    {
        var oldManifest = Manifest(Event("Old", 1, "Old", "Information", "old {Value}", "Value"));
        var newManifest = Manifest(Event("Old", 1, "Old", "Information", "old {Value}", "Value"), Event("New", 2, "New", "Information", "new {Value}", "Value"));

        var report = Compare(oldManifest, newManifest, SeverityGate.Breaking);

        Assert.Equal("KMLOG001", Assert.Single(report.Findings).Code);
        Assert.False(report.HasGatedFindings(SeverityGate.Breaking));
        Assert.True(report.HasGatedFindings(SeverityGate.All));
    }

    [Fact]
    public void RemovedEventIsBreaking()
    {
        var report = Compare(Manifest(Event("Old", 1, "Old", "Information", "old {Value}", "Value")), Manifest());
        Assert.Equal("KMLOG002", Assert.Single(report.Findings).Code);
        Assert.True(report.HasGatedFindings(SeverityGate.Breaking));
    }

    [Theory]
    [InlineData(2, 1, "KMLOG003")]
    [InlineData(1, 1, "KMLOG004")]
    public void EventIdentityFieldsAreBreaking(int newId, int expectedId, string expectedCode)
    {
        var oldEvent = Event("Old", 1, "Old", "Information", "old {Value}", "Value");
        var newEvent = expectedCode == "KMLOG003" ? Event("Old", newId, "Old", "Information", "old {Value}", "Value") : Event("Old", expectedId, "Renamed", "Information", "old {Value}", "Value");
        var report = Compare(Manifest(oldEvent), Manifest(newEvent), SeverityGate.Breaking);
        Assert.Equal(expectedCode, Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void PlaceholderRemovalAndRenameAreBreaking()
    {
        var removed = Compare(Manifest(Event("Old", 1, "Old", "Information", "old {Value} {Other}", "Value", "Other")), Manifest(Event("Old", 1, "Old", "Information", "old {Value}", "Value")), SeverityGate.Breaking);
        var renamed = Compare(Manifest(Event("Old", 1, "Old", "Information", "old {Value}", "Value")), Manifest(Event("Old", 1, "Old", "Information", "old {Id}", "Id")), SeverityGate.Breaking);
        Assert.Equal("KMLOG101", Assert.Single(removed.Findings).Code);
        Assert.Equal("KMLOG102", Assert.Single(renamed.Findings).Code);
    }

    [Fact]
    public void PlaceholderAdditionIsInformational()
    {
        var report = Compare(Manifest(Event("Old", 1, "Old", "Information", "old {Value}", "Value")), Manifest(Event("Old", 1, "Old", "Information", "old {Value} {Other}", "Value", "Other")), SeverityGate.Breaking);
        Assert.Equal("KMLOG104", Assert.Single(report.Findings).Code);
        Assert.False(report.HasGatedFindings(SeverityGate.Breaking));
    }

    [Fact]
    public void PlaceholderOrderChangeIsBreaking()
    {
        var report = Compare(Manifest(Event("Old", 1, "Old", "Information", "old {Value} {Other}", "Value", "Other")), Manifest(Event("Old", 1, "Old", "Information", "old {Other} {Value}", "Other", "Value")), SeverityGate.Breaking);
        Assert.Equal("KMLOG103", Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void LevelChangeIsWarningAndTemplateProseChangeIsInfo()
    {
        var warning = Compare(Manifest(Event("Old", 1, "Old", "Information", "old {Value}", "Value")), Manifest(Event("Old", 1, "Old", "Warning", "old {Value}", "Value")), SeverityGate.Breaking);
        var prose = Compare(Manifest(Event("Old", 1, "Old", "Information", "old {Value}", "Value")), Manifest(Event("Old", 1, "Old", "Information", "new prose {Value}", "Value")), SeverityGate.Breaking);
        Assert.Equal("KMLOG201", Assert.Single(warning.Findings).Code);
        Assert.Equal("KMLOG301", Assert.Single(prose.Findings).Code);
        Assert.True(warning.HasGatedFindings(SeverityGate.Warning));
        Assert.False(warning.HasGatedFindings(SeverityGate.Breaking));
        Assert.False(prose.HasGatedFindings(SeverityGate.Breaking));
        Assert.True(prose.HasGatedFindings(SeverityGate.All));
    }

    [Fact]
    public void AcceptedCodeDoesNotGateAndDoesNotRewriteManifest()
    {
        var oldManifest = Manifest(Event("Old", 1, "Old", "Information", "old {Value}", "Value"));
        var newManifest = Manifest(Event("Old", 1, "Old", "Warning", "old {Value}", "Value"));
        var report = ComparisonEngine.Compare(oldManifest, newManifest, SeverityGate.Warning, new HashSet<string>(["KMLOG201"]));
        Assert.True(Assert.Single(report.Findings).Accepted);
        Assert.False(report.HasGatedFindings(SeverityGate.Warning));
        Assert.Equal(1, oldManifest.SchemaVersion);
    }

    private static ComparisonReport Compare(ManifestDocument oldManifest, ManifestDocument newManifest, SeverityGate gate = SeverityGate.All) => ComparisonEngine.Compare(oldManifest, newManifest, gate, new HashSet<string>(StringComparer.Ordinal));

    private static ManifestDocument Manifest(params EventContract[] events) => new(1, [new ProjectIdentity("P|net8.0", "P", "P", "net8.0")], events, [], [], [], []);

    private static EventContract Event(string method, int eventId, string eventName, string level, string message, params string[] placeholders) => new("P|net8.0", "P.Logging." + method + "`0(Microsoft.Extensions.Logging.ILogger)", "P.Logging", method, 0, ["None"], eventId, eventName, level, message, placeholders.Select(name => new Placeholder(name, name)).ToArray(), ["ILogger"], new SourceLocation("Logging.cs", 1, "source"));
}
