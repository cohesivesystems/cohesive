using System.Collections.Immutable;
using Cohesive.Adapters.Bogus;
using Cohesive.Model;
using Cohesive.Simulation;
using Cohesive.Simulation.Generation;
using global::Bogus;
using SimulationDsl = Cohesive.Simulation.Simulation;

namespace Cohesive.Adapters.Bogus.Tests;

public sealed class BogusGenerationCatalogTests
{
    [Fact]
    public void Import_RetainsExactProviderProfileAndApplicationSources()
    {
        var catalog = ImportProfiles(seed: 1729, locale: "en");
        var provenance = catalog.Definition.Provenance;

        Assert.Equal(BogusGenerationCatalog.CapabilityProfileIdentity, provenance.Adapter);
        Assert.Equal(BogusGenerationCatalog.AdapterVersion, provenance.AdapterVersion);
        Assert.Equal(BogusGenerationCatalog.ProviderIdentity, provenance.Provider);
        Assert.Equal("35.6.5", provenance.ProviderVersion);
        Assert.Equal("en", provenance.Locale);
        Assert.Equal(BogusGenerationCatalog.RandomAlgorithmIdentity, provenance.RandomAlgorithm);
        Assert.Equal("1729", provenance.Seed);
        Assert.Contains("nuget://Bogus/35.6.5", provenance.SourceReferences.Select(static source => source.Value));
        Assert.Contains(
            $"nuget://Cohesive.Adapters.Bogus/{BogusGenerationCatalog.AdapterVersion}",
            provenance.SourceReferences.Select(static source => source.Value));
        Assert.Contains(
            "repo://src/adapters/Cohesive.Adapters.Bogus/README.md",
            provenance.SourceReferences.Select(static source => source.Value));
        Assert.Contains(
            "repo://src/Cohesive.Adapters.Bogus.Tests/BogusGenerationCatalogTests.cs",
            provenance.SourceReferences.Select(static source => source.Value));
        Assert.Equal(8, catalog.Definition.Entries.Length);
        Assert.Equal(
            Enumerable.Range(0, 8).Select(static index => $"sample/{index:D8}"),
            catalog.Definition.Entries.Select(static entry => entry.Id));
    }

    [Fact]
    public void Import_IsDeterministicAcrossGlobalRandomStateAndRoundTripsWithoutBogus()
    {
        Randomizer.Seed = new Random(1);
        var first = ImportProfiles(seed: 8675309, locale: "en");
        Randomizer.Seed = new Random(2);
        var second = ImportProfiles(seed: 8675309, locale: "en");

        var firstJson = GenerationCatalogJsonSerializer.Serialize(first);
        Assert.Equal(firstJson, GenerationCatalogJsonSerializer.Serialize(second));

        var retained = GenerationCatalogJsonSerializer.Deserialize(firstJson);
        var generation = SimulationDsl.Define<GeneratedCustomer>(customer =>
        {
            var profile = customer.SampleRecord("profile", Gen.Catalog<PersonProfile>(retained));
            customer
                .Member(value => value.GivenName, profile.Project(value => value.GivenName))
                .Member(value => value.FamilyName, profile.Project(value => value.FamilyName))
                .Member(value => value.Email, profile.Project(value => value.Email));
        }).Compile();

        var generated = generation.Generate(seed: 42);
        Assert.Equal(
            $"{generated.Value.GivenName}.{generated.Value.FamilyName}@example.test",
            generated.Value.Email);
    }

    [Fact]
    public void Import_ChangesFingerprintWithProviderInputs()
    {
        var baseline = ImportProfiles(seed: 11, locale: "en");
        var otherSeed = ImportProfiles(seed: 12, locale: "en");
        var otherLocale = ImportProfiles(seed: 11, locale: "fr");

        Assert.NotEqual(baseline.Fingerprint, otherSeed.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, otherLocale.Fingerprint);
    }

    [Fact]
    public void Import_UsesTheProfileFixedUtcDateTimeReference()
    {
        var options = Options(seed: 99, locale: "en", count: 2);

        var first = BogusGenerationCatalog.Import(
            options,
            faker => faker.Date.Recent(days: 2));
        var second = BogusGenerationCatalog.Import(
            options,
            faker => faker.Date.Recent(days: 2));

        Assert.Equal(DateTime.UnixEpoch, BogusGenerationCatalog.DateTimeReference);
        Assert.Equal(
            GenerationCatalogJsonSerializer.Serialize(first),
            GenerationCatalogJsonSerializer.Serialize(second));
    }

    [Fact]
    public void Import_HonorsCancellationBeforeInvokingProviderCode()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        var callbackInvoked = false;

        Assert.Throws<OperationCanceledException>(() => BogusGenerationCatalog.Import(
            Options(seed: 1, locale: "en", count: 1),
            faker =>
            {
                callbackInvoked = true;
                return faker.Name.FirstName();
            },
            cancellation.Token));
        Assert.False(callbackInvoked);
    }

    [Fact]
    public void Options_RejectInvalidBoundsAndMissingApplicationSources()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BogusGenerationCatalogImportOptions(
            id: "catalog/invalid",
            revision: "r1",
            count: 0,
            seed: 1,
            locale: "en",
            sourceReferences: [Source()]));
        Assert.Throws<ArgumentException>(() => new BogusGenerationCatalogImportOptions(
            id: "catalog/invalid",
            revision: "r1",
            count: 1,
            seed: 1,
            locale: "en",
            sourceReferences: []));
    }

    [Fact]
    public void CapabilityProfile_DeclaresEveryImplementedConvention()
    {
        Assert.Equal(
            BogusGenerationCatalogCapability.All,
            BogusGenerationCatalog.Capabilities);
        Assert.True(BogusGenerationCatalog.Capabilities.HasFlag(
            BogusGenerationCatalogCapability.FixedUtcDateTimeReference));
    }

    static GenerationCatalogDocument ImportProfiles(int seed, string locale) =>
        BogusGenerationCatalog.Import(
            Options(seed, locale, count: 8),
            faker =>
            {
                var givenName = faker.Name.FirstName();
                var familyName = faker.Name.LastName();
                return new PersonProfile(
                    givenName,
                    familyName,
                    $"{givenName}.{familyName}@example.test");
            });

    static BogusGenerationCatalogImportOptions Options(int seed, string locale, int count) => new(
        id: "catalog/person-profiles",
        revision: "r1",
        count: count,
        seed: seed,
        sourceReferences: [Source()],
        locale: locale);

    static SourceReference Source() => SourceReference.Repository(
        new("src/Cohesive.Adapters.Bogus.Tests/BogusGenerationCatalogTests.cs"));

    public sealed record PersonProfile(string GivenName, string FamilyName, string Email);

    public sealed record GeneratedCustomer(string GivenName, string FamilyName, string Email);
}
