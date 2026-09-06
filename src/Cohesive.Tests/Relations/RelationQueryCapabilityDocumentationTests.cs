using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Elastic;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.SQLite;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Explain;
using Cohesive.Relations.Realization;

namespace Cohesive.Tests.Relations;

public sealed class RelationQueryCapabilityDocumentationTests
{
    const string StartMarker = "<!-- generated-capability-profiles:start -->";
    const string EndMarker = "<!-- generated-capability-profiles:end -->";
    const string UpdateEnvironmentVariable = "UPDATE_RELATIONS_CAPABILITY_DOCS";

    [Fact]
    public void CheckedInCapabilityReference_IsCurrentWithCanonicalProfiles()
    {
        var path = FindRepositoryFile("src", "Cohesive.Relations", "docs", "CAPABILITIES.md");
        var document = File.ReadAllText(path);
        var generated = GenerateProfileInventory();

        if (string.Equals(
                Environment.GetEnvironmentVariable(UpdateEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            document = ReplaceGeneratedBlock(document, generated);
            File.WriteAllText(path, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Equal(generated, ReadGeneratedBlock(document));
    }

    [Fact]
    public void CanonicalDocumentationEntryPoints_HaveValidLocalLinksAndAnchors()
    {
        string[][] documentationFiles =
        [
            ["src", "Cohesive.Relations", "README.md"],
            ["src", "Cohesive.Relations", "INTERNALS.md"],
            ["src", "Cohesive.Relations", "docs", "GETTING_STARTED.md"],
            ["src", "Cohesive.Relations", "docs", "EXECUTION_AND_ADAPTERS.md"],
            ["src", "Cohesive.Relations", "docs", "DIAGNOSTICS.md"],
            ["src", "Cohesive.Relations", "docs", "CAPABILITIES.md"],
            ["src", "Cohesive.Relations", "docs", "MIGRATION.md"],
            ["src", "Cohesive.Relations", "docs", "internals", "SEMANTIC_MODEL.md"],
            ["src", "Cohesive.Relations", "docs", "internals", "RELATIONS_AND_QUERIES.md"],
            ["src", "Cohesive.Relations", "docs", "internals", "DTO_MAPPING.md"],
            ["src", "Cohesive.Relations", "docs", "internals", "DIAGNOSTICS_AND_EXECUTION.md"],
            ["src", "Cohesive.Relations", "docs", "internals", "INTERPRETATIONS_AND_USE_CASES.md"],
            ["src", "Cohesive.Relations", "docs", "internals", "COMPILATION_AND_REALIZATION.md"],
            ["src", "Cohesive.Relations", "docs", "internals", "PORTABILITY_AND_STATUS.md"],
            ["src", "adapters", "Cohesive.Adapters.Cosmos", "README.md"],
            ["src", "adapters", "Cohesive.Adapters.Cosmos", "INTERNALS.md"],
            ["src", "adapters", "Cohesive.Adapters.Elastic", "README.md"],
            ["src", "adapters", "Cohesive.Adapters.Elastic", "INTERNALS.md"],
            ["src", "adapters", "Cohesive.Adapters.Postgres", "README.md"],
            ["src", "adapters", "Cohesive.Adapters.Postgres", "INTERNALS.md"],
            ["src", "adapters", "Cohesive.Adapters.SQLite", "RELATIONS.md"]
        ];

        foreach (var segments in documentationFiles)
        {
            var source = FindRepositoryFile(segments);
            var document = File.ReadAllText(source);
            foreach (Match match in Regex.Matches(document, @"(?<!!)\[[^\]]+\]\((?<target>[^)]+)\)"))
            {
                var target = match.Groups["target"].Value;
                string linkedFile;
                string? fragment;
                if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
                {
                    const string repositoryPrefix = "/cohesivesystems/cohesive/blob/main/";
                    if (!string.Equals(absolute.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                        || !absolute.AbsolutePath.StartsWith(repositoryPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    linkedFile = FindRepositoryFile(
                        Uri.UnescapeDataString(absolute.AbsolutePath[repositoryPrefix.Length..])
                            .Split('/', StringSplitOptions.RemoveEmptyEntries));
                    fragment = absolute.Fragment.TrimStart('#');
                }
                else
                {
                    var parts = target.Split('#', count: 2);
                    linkedFile = parts[0].Length == 0
                        ? source
                        : Path.GetFullPath(Path.Combine(
                            Path.GetDirectoryName(source)!,
                            Uri.UnescapeDataString(parts[0])));
                    fragment = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : null;
                }
                Assert.True(
                    File.Exists(linkedFile),
                    $"'{target}' in '{source}' resolves to missing file '{linkedFile}'.");

                if (!string.IsNullOrEmpty(fragment))
                {
                    var anchors = File.ReadLines(linkedFile)
                        .Where(static line => line.StartsWith('#'))
                        .Select(static line => MarkdownAnchor(line.TrimStart('#').Trim()))
                        .ToHashSet(StringComparer.Ordinal);
                    Assert.Contains(fragment, anchors);
                }
            }
        }
    }

    static string GenerateProfileInventory()
    {
        Profile[] profiles =
        [
            new("In-memory reference", RelationQueryInMemoryInterpreter.DefaultTargetProfile),
            new("Cosmos SQL", CosmosRelationQueryTargetProfile.Default),
            new("Cosmos entity source", CosmosRelationQuerySourceReader.TargetProfile),
            new("Elasticsearch", ElasticRelationQueryTargetProfile.Default),
            new("PostgreSQL", PostgresRelationQueryTargetProfile.Default),
            new("PostgreSQL source", PostgresRelationQuerySourceTargetProfile.Default),
            new("SQLite native rows", SqliteRelationQueryTargetProfile.Default)
        ];

        StringBuilder markdown = new();
        foreach (var profile in profiles)
        {
            var summary = RelationQueryCapabilitySummaryProjector.Project(profile.Value);
            markdown.Append("### ").AppendLine(profile.Label);
            markdown.AppendLine();
            markdown.Append("- Target: `").Append(summary.Target.Value).AppendLine("`");
            markdown.Append("- Profile: `").Append(summary.TargetProfile.Value).AppendLine("`");
            markdown.Append("- Capability evidence: ")
                .Append(profile.Value.Capabilities.Length.ToString(CultureInfo.InvariantCulture))
                .AppendLine();
            markdown.Append("- Definition schemas: ")
                .AppendLine(string.Join(", ", profile.Value.SupportedDefinitionSchemaVersions.Select(
                    static version => $"`{version}`")));
            markdown.Append("- Compiler profiles: ")
                .AppendLine(string.Join(", ", profile.Value.SupportedCompilerProfiles.Select(
                    static version => $"`{version}`")));
            markdown.Append("- Full-profile SHA-256: `")
                .Append(ProfileChecksum(profile.Value))
                .AppendLine("`");
            markdown.Append("- Families: ").AppendLine(FamilyCounts(summary));
            markdown.AppendLine();
            AppendCapabilities<LogicalRelationQueryCapability>(
                markdown,
                summary,
                "Logical semantics",
                static capability => capability.Kind.ToString());
            AppendCapabilities<ExpressionRelationQueryCapability>(
                markdown,
                summary,
                "Expression semantics",
                static capability => $"{capability.RequirementKind}:{capability.Capability.Value}");
            AppendCapabilities<StructuralRelationQueryCapability>(
                markdown,
                summary,
                "Structural paths",
                static capability => $"{capability.Role}:{capability.PathKind}");
            AppendCapabilities<GuaranteeRelationQueryCapability>(
                markdown,
                summary,
                "Preserved guarantees",
                static capability => capability.Kind.ToString());
            AppendCapabilities<OperatingBoundaryValidationRelationQueryCapability>(
                markdown,
                summary,
                "Enforced boundaries",
                static capability => capability.Boundary.Value);
            AppendCapabilities<PrimitiveRelationQueryCapability>(
                markdown,
                summary,
                "Primitive facilities",
                static capability => capability.Kind.ToString());
            AppendCapabilities<TemporalRelationQueryCapability>(
                markdown,
                summary,
                "Temporal semantics",
                static capability => capability.Capability.ToString());
            AppendBoundaries(markdown, profile.Value);
        }

        return markdown.ToString().TrimEnd();
    }

    static string FamilyCounts(RelationQueryCapabilitySummary summary)
    {
        var counts = summary.Entries
            .GroupBy(static entry => FamilyName(entry.Capability), StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count().ToString(CultureInfo.InvariantCulture)}");
        return string.Join(", ", counts);
    }

    static string ProfileChecksum(RelationQueryTargetCapabilityProfile profile)
    {
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    static string FamilyName(RelationQueryCapability capability) => capability switch
    {
        LogicalRelationQueryCapability => "logical",
        ExpressionRelationQueryCapability => "expression",
        TemporalRelationQueryCapability => "temporal",
        StructuralRelationQueryCapability => "structural",
        GuaranteeRelationQueryCapability => "guarantee",
        OperatingBoundaryValidationRelationQueryCapability => "boundary validation",
        PrimitiveRelationQueryCapability => "primitive",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unsupported capability family.")
    };

    static void AppendCapabilities<TCapability>(
        StringBuilder markdown,
        RelationQueryCapabilitySummary summary,
        string label,
        Func<TCapability, string> format)
        where TCapability : RelationQueryCapability
    {
        var values = summary.Entries
            .Select(static entry => entry.Capability)
            .OfType<TCapability>()
            .Select(format)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (values.Length == 0)
            return;

        markdown.Append("- ").Append(label).Append(": ")
            .AppendLine(string.Join(", ", values.Select(static value => $"`{value}`")));
    }

    static void AppendBoundaries(StringBuilder markdown, RelationQueryTargetCapabilityProfile profile)
    {
        if (profile.OperatingBoundaries.IsDefaultOrEmpty)
        {
            markdown.AppendLine("- Operating boundaries: none declared by this profile.");
            markdown.AppendLine();
            return;
        }

        markdown.AppendLine("- Operating boundaries:");
        foreach (var boundary in profile.OperatingBoundaries)
        {
            markdown.Append("  - `").Append(boundary.Id.Value).Append("`: ").Append(boundary.Kind);
            if (boundary.Limit is { } limit)
            {
                markdown.Append(" = ")
                    .Append(limit.ToString(CultureInfo.InvariantCulture));
            }
            markdown.AppendLine();
        }
        markdown.AppendLine();
    }

    static string ReadGeneratedBlock(string document)
    {
        var start = document.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = document.IndexOf(EndMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker '{StartMarker}'.");
        Assert.True(end > start, $"Missing end marker '{EndMarker}'.");
        start += StartMarker.Length;
        return document[start..end].Trim();
    }

    static string ReplaceGeneratedBlock(string document, string generated)
    {
        var start = document.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = document.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start < 0 || end <= start)
            throw new InvalidOperationException("The capability reference does not contain one valid generated block.");
        start += StartMarker.Length;
        return document[..start] + "\n" + generated + "\n" + document[end..];
    }

    static string MarkdownAnchor(string heading)
    {
        StringBuilder anchor = new(heading.Length);
        foreach (var character in heading)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
                anchor.Append(char.ToLowerInvariant(character));
            else if (char.IsWhiteSpace(character))
                anchor.Append('-');
        }
        return anchor.ToString();
    }

    static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cohesive.sln")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("Could not locate the Cohesive repository root.");
        return Path.Combine([directory.FullName, .. segments]);
    }

    sealed record Profile(string Label, RelationQueryTargetCapabilityProfile Value);
}
