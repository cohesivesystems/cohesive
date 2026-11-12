using System.Xml.Linq;

internal static class ExerciseAssertions
{
    internal static void RequireCompatibility(string? verdict, bool expectedAdditionBreak, string subject)
    {
        var accepted = expectedAdditionBreak ? verdict == "Breaking" : verdict is "Compatible" or "Unknown";
        if (!accepted) throw new InvalidOperationException($"Unexpected compatibility verdict for {subject}: {verdict}.");
    }

    internal static void RequireTests(XDocument document, IReadOnlyList<string> expected, string suite)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var results = document.Descendants(ns + "UnitTestResult").ToArray();
        var actual = results.Select(test => (string?)test.Attribute("testName")).ToHashSet(StringComparer.Ordinal);
        var missing = expected.Where(name => !actual.Contains(name)).ToArray();
        if (expected.Count == 0 || results.Length == 0 || missing.Length != 0
            || results.Any(test => (string?)test.Attribute("outcome") != "Passed"))
            throw new InvalidOperationException($"Conformance {suite} requires every expected case and no failures or skips. Missing: {string.Join(", ", missing)}");
    }
}
