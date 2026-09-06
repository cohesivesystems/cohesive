using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Simulation.Generation;

namespace Cohesive.Adapters.Mimesis.Tests;

public sealed class MimesisGenerationCatalogTests
{
    const string ExpectedConfiguration =
        "{\"members\":["
        + "{\"arguments\":{\"end\":80,\"start\":18},\"field\":\"numeric.integer_number\",\"path\":[\"Age\"]},"
        + "{\"arguments\":{\"domains\":[\"example.com\"]},\"field\":\"person.email\",\"path\":[\"Email\"]},"
        + "{\"arguments\":{},\"field\":\"person.full_name\",\"path\":[\"display_name\"]}],"
        + "\"schemaVersion\":\"cohesive-simulation-mimesis-record/v1\"}";

    [Fact]
    public void Define_LowersTypedMembersToCanonicalClosedConfiguration()
    {
        var definition = Definition();

        Assert.Equal(ExpectedConfiguration, definition.Configuration.GetRawText());
        Assert.Equal(["Age", "Email", "Region", "display_name"], definition.ValueType.Fields.Select(static field => field.Name));
        Assert.Equal(FieldPresence.Optional, definition.ValueType.Fields.Single(static field => field.Name == "Region").Presence);
    }

    [Fact]
    public void Define_RejectsMissingRequiredDuplicateAndNestedMembers()
    {
        var missing = Assert.Throws<ArgumentException>(() => MimesisGenerationCatalog.Define<MimesisPerson>(person => person
            .Member(value => value.Name, "person.full_name")
            .Member(value => value.Email, "person.email")));
        Assert.Contains("Age", missing.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => MimesisGenerationCatalog.Define<MimesisPerson>(person => person
            .Member(value => value.Age, "numeric.integer_number")
            .Member(value => value.Age, "numeric.integer_number")
            .Member(value => value.Email, "person.email")
            .Member(value => value.Name, "person.full_name")));

        Assert.Throws<ArgumentException>(() => MimesisGenerationCatalog.Define<NestedPerson>(person => person
            .Member(value => value.Identity.Name, "person.full_name")));
    }

    [Theory]
    [InlineData("full_name")]
    [InlineData("person.__dict__")]
    [InlineData("person.full-name")]
    public void Define_RequiresExplicitSafeProviderFields(string providerField)
    {
        Assert.Throws<ArgumentException>(() => MimesisGenerationCatalog.Define<MimesisPerson>(person => person
            .Member(value => value.Age, "numeric.integer_number")
            .Member(value => value.Email, "person.email")
            .Member(value => value.Name, providerField)));
    }

    [Fact]
    public void Define_RejectsNonObjectAndDuplicateArgumentProperties()
    {
        using var scalar = JsonDocument.Parse("42");
        using var duplicate = JsonDocument.Parse("{\"domains\":[\"a.test\"],\"domains\":[\"b.test\"]}");

        Assert.Throws<ArgumentException>(() => MimesisGenerationCatalog.Define<MimesisPerson>(person => person
            .Member(value => value.Age, "numeric.integer_number")
            .Member(value => value.Email, "person.email", scalar.RootElement)
            .Member(value => value.Name, "person.full_name")));
        Assert.Throws<ArgumentException>(() => MimesisGenerationCatalog.Define<MimesisPerson>(person => person
            .Member(value => value.Age, "numeric.integer_number")
            .Member(value => value.Email, "person.email", duplicate.RootElement)
            .Member(value => value.Name, "person.full_name")));
    }

    [MimesisFact]
    public async Task ImportAsync_RetainsPinnedDeterministicMimesisOutput()
    {
        var python = RequireMimesisPython();

        var options = new MimesisGenerationCatalogImportOptions(
            id: "catalog/mimesis-people",
            revision: "r1",
            count: 2,
            seed: 42,
            sourceReferences:
            [
                SourceReference.Repository(new(
                    "src/Cohesive.Adapters.Mimesis.Tests/MimesisGenerationCatalogTests.cs"))
            ],
            locale: "en");

        var first = await MimesisGenerationCatalog.ImportAsync(
            Definition(),
            options,
            new(pythonExecutable: python));
        var second = await MimesisGenerationCatalog.ImportAsync(
            Definition(),
            options,
            new(pythonExecutable: python));

        Assert.Equal(
            GenerationCatalogJsonSerializer.Serialize(first),
            GenerationCatalogJsonSerializer.Serialize(second));
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(
            [
                new MimesisPerson("Demarcus Raymond", "any1925@example.com", 58, null),
                new MimesisPerson("Arden Brady", "usage2016@example.com", 25, null)
            ],
            first.Definition.Entries.Select(static entry => entry.Value.Deserialize<MimesisPerson>()));
        Assert.Equal("Mimesis", first.Definition.Provenance.Provider);
        Assert.Equal("21.0.0", first.Definition.Provenance.ProviderVersion);
        Assert.Equal("Mimesis.Field/local-seed/v1", first.Definition.Provenance.RandomAlgorithm);
        Assert.Equal("42", first.Definition.Provenance.Seed);
        Assert.Null(first.Definition.Provenance.DateTimeReferenceUtc);
        Assert.Equal(MimesisGenerationCatalog.CapabilityProfile, first.Definition.Provenance.CapabilityProfile);
        Assert.Equal(
            "cohesive.adapters.mimesis/field-record-snapshot/v1",
            first.Definition.Provenance.CapabilityProfile.Id);
        Assert.Equal(
            [
                "csimcatalogrequest://csimcatalogrequest1_859f01c548ba1cd7b11144d4b3aea6b86acb66f64ffdc7a6efe3e406fc448d9f",
                "repo://src/Cohesive.Adapters.Mimesis.Tests/MimesisGenerationCatalogTests.cs"
            ],
            first.Definition.Provenance.SourceReferences.Select(static source => source.Value));
    }

    [MimesisFact]
    public async Task ImportAsync_NormalizesFiniteMimesisFloatsAsPortableNumbers()
    {
        var python = RequireMimesisPython();

        var definition = MimesisGenerationCatalog.Define<MimesisNumeric>(numeric => numeric
            .Member(
                value => value.Score,
                "numeric.float_number",
                new { Start = -1, End = 1, Precision = 4 }));
        var catalog = await MimesisGenerationCatalog.ImportAsync(
            definition,
            new(
                id: "catalog/mimesis-numeric",
                revision: "r1",
                count: 1,
                seed: 42,
                sourceReferences:
                [
                    SourceReference.Repository(new(
                        "src/Cohesive.Adapters.Mimesis.Tests/MimesisGenerationCatalogTests.cs"))
                ]),
            new(pythonExecutable: python));

        var score = catalog.Definition.Entries[0].Value.GetProperty("Score");
        Assert.Equal(ObservationValueKind.Decimal, score.Kind);
        Assert.Equal(0.2789m, score.GetDecimal());
        Assert.Equal(new MimesisNumeric(0.2789), catalog.Definition.Entries[0].Value.Deserialize<MimesisNumeric>());
    }

    static MimesisRecordDefinition<MimesisPerson> Definition() =>
        MimesisGenerationCatalog.Define<MimesisPerson>(person => person
            .Member(value => value.Email, "person.email", new { Domains = new[] { "example.com" } })
            .Member(value => value.Name, "person.full_name")
            .Member(value => value.Age, "numeric.integer_number", new { Start = 18, End = 80 }));

    static string RequireMimesisPython()
    {
        var python = Environment.GetEnvironmentVariable("COHESIVE_MIMESIS_PYTHON");
        return !string.IsNullOrWhiteSpace(python)
            ? python
            : throw new InvalidOperationException(
                "MimesisFact must skip this test when COHESIVE_MIMESIS_PYTHON is unset.");
    }

    public sealed record MimesisPerson(
        [property: JsonPropertyName("display_name")] string Name,
        string Email,
        int Age,
        string? Region);

    public sealed record NestedPerson(Identity Identity);

    public sealed record Identity(string Name);

    public sealed record MimesisNumeric(double Score);
}

sealed class MimesisFactAttribute : FactAttribute
{
    public MimesisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COHESIVE_MIMESIS_PYTHON")))
        {
            Skip =
                "Set COHESIVE_MIMESIS_PYTHON to a Python executable containing the pinned Mimesis environment.";
        }
    }
}
