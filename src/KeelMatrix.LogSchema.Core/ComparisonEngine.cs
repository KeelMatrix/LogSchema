using System.Globalization;

namespace KeelMatrix.LogSchema;

internal enum FindingSeverity
{
    Info,
    Warning,
    Breaking
}

internal sealed record CompatibilityFinding(
    string Code,
    FindingSeverity Severity,
    string ProjectKey,
    string Identity,
    string EventName,
    string Field,
    string? OldValue,
    string? NewValue,
    string Message,
    bool Accepted = false);

internal sealed record ComparisonReport(
    IReadOnlyList<CompatibilityFinding> Findings,
    IReadOnlyList<string> AnalysisErrors)
{
    internal bool HasGatedFindings(SeverityGate gate) => Findings.Any(finding => !finding.Accepted && (int)finding.Severity >= (int)gate);
}

internal enum SeverityGate
{
    Breaking = 2,
    Warning = 1,
    All = 0
}

internal static class ComparisonEngine
{
    internal static ComparisonReport Compare(ManifestDocument oldManifest, ManifestDocument newManifest, SeverityGate gate, IReadOnlySet<string> acceptedCodes, bool rejectEmptyEventSets = false)
    {
        var oldEvents = oldManifest.Events.ToDictionary(EventKey, StringComparer.Ordinal);
        var newEvents = newManifest.Events.ToDictionary(EventKey, StringComparer.Ordinal);
        var findings = new List<CompatibilityFinding>();

        foreach (var key in oldEvents.Keys.Except(newEvents.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var oldEvent = oldEvents[key];
            findings.Add(Find("KMLOG002", FindingSeverity.Breaking, oldEvent, "event", null, null, $"{Display(oldEvent)} was removed."));
        }

        foreach (var key in newEvents.Keys.Except(oldEvents.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var newEvent = newEvents[key];
            findings.Add(Find("KMLOG001", FindingSeverity.Info, newEvent, "event", null, null, $"{Display(newEvent)} was added."));
        }

        foreach (var key in oldEvents.Keys.Intersect(newEvents.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            CompareEvent(oldEvents[key], newEvents[key], findings);
        }

        var normalized = findings
            .Select(finding => finding with { Accepted = acceptedCodes.Contains(finding.Code) })
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.ProjectKey, StringComparer.Ordinal)
            .ThenBy(finding => finding.Identity, StringComparer.Ordinal)
            .ThenBy(finding => finding.Code, StringComparer.Ordinal)
            .ThenBy(finding => finding.Field, StringComparer.Ordinal)
            .ToArray();
        var analysisErrors = newManifest.AnalysisIssues
            .Where(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .Select(issue => issue.Code + ": " + issue.Message)
            .ToList();
        if (rejectEmptyEventSets && oldManifest.Events.Count == 0)
        {
            analysisErrors.Add(LogSchemaExtractor.EmptyEventsIssueCode + ": " + LogSchemaExtractor.EmptyBaselineEventsIssueMessage);
        }

        var distinctAnalysisErrors = analysisErrors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new ComparisonReport(normalized, distinctAnalysisErrors);
    }

    private static void CompareEvent(EventContract oldEvent, EventContract newEvent, List<CompatibilityFinding> findings)
    {
        if (oldEvent.EventId != newEvent.EventId)
        {
            findings.Add(Find("KMLOG003", FindingSeverity.Breaking, newEvent, "eventId", oldEvent.EventId.ToString(CultureInfo.InvariantCulture), newEvent.EventId.ToString(CultureInfo.InvariantCulture), $"{Display(newEvent)} changed EventId {oldEvent.EventId} -> {newEvent.EventId}."));
        }
        if (!string.Equals(oldEvent.EventName, newEvent.EventName, StringComparison.Ordinal))
        {
            findings.Add(Find("KMLOG004", FindingSeverity.Breaking, newEvent, "eventName", oldEvent.EventName, newEvent.EventName, $"{Display(newEvent)} changed EventName {oldEvent.EventName} -> {newEvent.EventName}."));
        }
        if (!string.Equals(oldEvent.Level, newEvent.Level, StringComparison.Ordinal))
        {
            findings.Add(Find("KMLOG201", FindingSeverity.Warning, newEvent, "level", oldEvent.Level, newEvent.Level, $"{Display(newEvent)} changed level {oldEvent.Level} -> {newEvent.Level}."));
        }

        var oldNames = oldEvent.Placeholders.Select(p => p.Name).ToArray();
        var newNames = newEvent.Placeholders.Select(p => p.Name).ToArray();
        var oldSet = oldNames.ToHashSet(StringComparer.Ordinal);
        var newSet = newNames.ToHashSet(StringComparer.Ordinal);
        if (oldNames.Length == newNames.Length && oldSet.SetEquals(newSet) && !oldNames.SequenceEqual(newNames, StringComparer.Ordinal))
        {
            findings.Add(Find("KMLOG103", FindingSeverity.Breaking, newEvent, "placeholders", string.Join(", ", oldNames), string.Join(", ", newNames), $"{Display(newEvent)} changed structured placeholder order."));
        }
        else
        {
            var removed = oldNames.Where(name => !newSet.Contains(name)).ToArray();
            var added = newNames.Where(name => !oldSet.Contains(name)).ToArray();
            if (removed.Length == 1 && added.Length == 1)
            {
                findings.Add(Find("KMLOG102", FindingSeverity.Breaking, newEvent, "placeholder", removed[0], added[0], $"{Display(newEvent)} changed structured property \"{removed[0]}\" to \"{added[0]}\"."));
            }
            else
            {
                foreach (var name in removed.Order(StringComparer.Ordinal))
                {
                    findings.Add(Find("KMLOG101", FindingSeverity.Breaking, newEvent, "placeholder", name, null, $"{Display(newEvent)} removed structured property \"{name}\"."));
                }
                foreach (var name in added.Order(StringComparer.Ordinal))
                {
                    findings.Add(Find("KMLOG104", FindingSeverity.Info, newEvent, "placeholder", null, name, $"{Display(newEvent)} added structured property \"{name}\"."));
                }
            }
        }

        if (!string.Equals(oldEvent.Message, newEvent.Message, StringComparison.Ordinal) && oldNames.SequenceEqual(newNames, StringComparer.Ordinal))
        {
            findings.Add(Find("KMLOG301", FindingSeverity.Info, newEvent, "message", oldEvent.Message, newEvent.Message, $"{Display(newEvent)} changed message template prose."));
        }
    }

    private static CompatibilityFinding Find(string code, FindingSeverity severity, EventContract @event, string field, string? oldValue, string? newValue, string message) => new(code, severity, @event.ProjectKey, @event.Identity, @event.EventName, field, oldValue, newValue, message);
    private static string EventKey(EventContract @event) => @event.ProjectKey + "\u001f" + @event.Identity;
    private static string Display(EventContract @event) => string.IsNullOrWhiteSpace(@event.EventName) ? @event.Method : @event.EventName;
}
