using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Cohesive.Adapters.Bogus;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using Cohesive.Simulation;
using Cohesive.Simulation.ExternalProcess;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Adapters.Mimesis;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Relations;
using Cohesive.Simulation.Storage;
using Cohesive.Simulation.Worlds;
using Cohesive.Simulation.Xunit;
using Xunit.Sdk;

const string ExpectedArtifactId =
    "csimartifact1_7f28277f04c1f1c4b5a104e7c4fe2c8e561a0eff36048749c6a6ca2bc86ccf1d";
const string ExpectedManifestFingerprint =
    "7f28277f04c1f1c4b5a104e7c4fe2c8e561a0eff36048749c6a6ca2bc86ccf1d";
const string ExpectedWorldFingerprint =
    "53adc1b48a3171c566ca32c2293d6d76bb81e577cb86367b64a7af0d205bbe75";
const string ExpectedJsonLinesFingerprint =
    "1e4954f2d2170ee2b060b6e6e0a9117993be7b22ac8f6a0c7b628e48a823c1e2";

if (args is ["emit", string coreWorldPath])
{
    VerifyBogusAdapterPackage();
    VerifyExternalProcessAdapterPackage();
    await VerifyMimesisPackage();
    var customers = CreateCustomers();
    var compiledCustomers = customers.Compile();
    var propertyRun = compiledCustomers.CheckProperty(
        seed: 42,
        property: static customer => customer.Age < 50);
    if (propertyRun.Status != PropertyCaseRunStatus.CounterexampleFound)
    {
        throw new InvalidOperationException(
            $"Expected a property counterexample but found '{propertyRun.Status}'.");
    }

    try
    {
        PropertyCaseAssert.Passed(propertyRun);
        throw new InvalidOperationException("Expected the xUnit adapter to reject the property counterexample.");
    }
    catch (XunitException exception)
    {
        if (!exception.Message.Contains(
            propertyRun.BestCounterexample!.Replay.ToToken(),
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The xUnit failure omitted the exact replay token.");
        }
    }

    var replayed = compiledCustomers.ReplayPropertyCase(
        propertyRun.BestCounterexample!.Replay.ToToken());
    Require(replayed.Name, "Ada", "property counterexample name");
    Require(replayed.Region, "north", "property counterexample region");
    if (replayed.Age != 50)
    {
        throw new InvalidOperationException($"Expected property counterexample age '50' but found '{replayed.Age}'.");
    }

    await File.WriteAllTextAsync(coreWorldPath, WorldDefinitionJsonSerializer.Serialize(CreateWorld(customers)));
}
else if (args is ["emit-relationship", string relationshipWorldPath])
{
    var world = new RelationshipWorldDefinition(
        CreateWorld(CreateCustomers()),
        RelationshipCatalogDocument.FromCatalog(RelationshipCatalog.Empty),
        []);
    await File.WriteAllTextAsync(
        relationshipWorldPath,
        RelationshipWorldDefinitionJsonSerializer.Serialize(world));
}
else if (args is ["emit-catalog", string emittedCatalogPath])
{
    await File.WriteAllTextAsync(
        emittedCatalogPath,
        GenerationCatalogJsonSerializer.Serialize(CreateIdentityCatalog()));
}
else if (args is ["emit-external-import", string externalImportPath])
{
    await File.WriteAllTextAsync(
        externalImportPath,
        ExternalGenerationCatalogImportJsonSerializer.Serialize(CreateExternalImportDefinition()));
}
else if (args is ["provide-external-catalog"])
{
    await ProvideExternalCatalog();
}
else if (args is ["verify", string coreJsonLinesPath, string coreManifestPath, string coreReportPath])
{
    var manifest = WorldArtifactManifestJsonSerializer.Deserialize(await File.ReadAllTextAsync(coreManifestPath));
    Require(manifest.ArtifactId.Value, ExpectedArtifactId, "artifactId");
    Require(manifest.Fingerprint.Value, ExpectedManifestFingerprint, "manifest fingerprint");
    Require(manifest.World.Fingerprint.Value, ExpectedWorldFingerprint, "world fingerprint");

    var exemplar = manifest.GetExemplar("customer-for-ui");
    Require(exemplar.PopulationId, "customers", "exemplar populationId");
    if (exemplar.SequenceIndex != 1)
    {
        throw new InvalidOperationException("Manifest exemplar has an invalid sequence index.");
    }

    await using (FileStream wireInput = File.OpenRead(coreJsonLinesPath))
    {
        var wireFingerprint = Convert.ToHexString(await SHA256.HashDataAsync(wireInput)).ToLowerInvariant();
        Require(wireFingerprint, ExpectedJsonLinesFingerprint, "JSON Lines wire fingerprint");
    }

    await using FileStream jsonLines = File.OpenRead(coreJsonLinesPath);
    var verification = await WorldJsonLinesVerifier.VerifyAsync(manifest, jsonLines);
    Require(verification.ArtifactId.Value, ExpectedArtifactId, "verified artifactId");
    Require(verification.TargetId, "package-smoke/cli", "targetId");
    if (verification.BatchSize != 1 || verification.ItemCount != 2)
    {
        throw new InvalidOperationException(
            $"Expected batch size 1 and two items but found '{verification.BatchSize}' and "
            + $"'{verification.ItemCount}'.");
    }

    await VerifyCliReport(coreReportPath, manifest.ArtifactId.Value, expectedItemCount: 2);
    Require(
        RepositoryWorldProvisioningTargetConvention.Identity,
        "cohesive-simulation-storage-target/v2",
        "storage target convention");
}
else if (args is [
    "verify-relationship",
    string relationshipJsonLinesPath,
    string relationshipManifestPath,
    string relationshipReportPath])
{
    var manifest = WorldArtifactManifestJsonSerializer.Deserialize(
        await File.ReadAllTextAsync(relationshipManifestPath));
    _ = RelationshipWorldArtifact.GetWorld(manifest);
    await using FileStream jsonLines = File.OpenRead(relationshipJsonLinesPath);
    var verification = await RelationshipWorldJsonLinesVerifier.VerifyAsync(manifest, jsonLines);
    if (verification.ItemCount != 2)
    {
        throw new InvalidOperationException(
            $"Expected two relationship-world items but found '{verification.ItemCount}'.");
    }

    await VerifyCliReport(relationshipReportPath, manifest.ArtifactId.Value, expectedItemCount: 2);
}
else if (args is ["verify-catalog", string retainedCatalogPath, string catalogReportPath])
{
    var catalog = GenerationCatalogJsonSerializer.Deserialize(await File.ReadAllTextAsync(retainedCatalogPath));
    await VerifyCatalogCliReport(catalogReportPath, catalog);
}
else if (args is [
    "verify-external-catalog",
    string importedCatalogPath,
    string importedCatalogReportPath])
{
    var catalog = GenerationCatalogJsonSerializer.Deserialize(await File.ReadAllTextAsync(importedCatalogPath));
    Require(catalog.Definition.Id, "catalog/package-smoke-external-cli", "external CLI catalog id");
    Require(catalog.Definition.Provenance.Provider, "package-smoke-provider", "external CLI provider");
    Require(catalog.Definition.Provenance.ProviderVersion, "1", "external CLI provider version");
    Require(
        catalog.Definition.Entries[0].Value.String,
        "package-smoke-42-0",
        "external CLI first value");
    if (!catalog.Definition.Provenance.SourceReferences.Any(static source =>
            source.Value.StartsWith("csimcatalogrequest://csimcatalogrequest1_", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("The imported catalog omitted its external request identity.");
    }
    await VerifyCatalogCliReport(importedCatalogReportPath, catalog);
}
else
{
    throw new ArgumentException(
        "Expected an emit, emit-catalog, emit-external-import, emit-relationship, provide-external-catalog, verify, "
        + "verify-catalog, verify-external-catalog, or verify-relationship command.");
}

return 0;

static void Require(string? actual, string expected, string property)
{
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected {property} '{expected}' but found '{actual}'.");
    }
}

static void VerifyBogusAdapterPackage()
{
    var catalog = BogusGenerationCatalog.Import(
        new(
            id: "catalog/package-smoke-bogus",
            revision: "r1",
            count: 2,
            seed: 1729,
            locale: "en",
            sourceReferences:
            [
                SourceReference.Repository(new("eng/package-smoke/Cohesive.Simulation.Consumer/Program.cs"))
            ]),
        faker => faker.Name.FullName());

    Require(catalog.Definition.Provenance.Provider, "Bogus", "Bogus provider identity");
    if (catalog.Definition.Entries.Length != 2)
        throw new InvalidOperationException("Expected the installed Bogus adapter to retain two catalog entries.");
}

static async Task VerifyMimesisPackage()
{
    var python = Environment.GetEnvironmentVariable("COHESIVE_MIMESIS_PYTHON");
    if (string.IsNullOrWhiteSpace(python))
    {
        Console.Error.WriteLine(
            "SKIP: Mimesis package smoke requires COHESIVE_MIMESIS_PYTHON with the pinned provider environment.");
        return;
    }

    var definition = MimesisGenerationCatalog.Define<MimesisSmokePerson>(person => person
        .Member(value => value.Name, "person.full_name")
        .Member(value => value.Age, "numeric.integer_number", new { Start = 18, End = 80 }));
    var catalog = await MimesisGenerationCatalog.ImportAsync(
        definition,
        new(
            id: "catalog/package-smoke-mimesis",
            revision: "r1",
            count: 2,
            seed: 1729,
            locale: "en",
            sourceReferences:
            [
                SourceReference.Repository(new("eng/package-smoke/Cohesive.Simulation.Consumer/Program.cs"))
            ]),
        new(pythonExecutable: python));

    Require(catalog.Definition.Provenance.Provider, "Mimesis", "Mimesis provider identity");
    if (catalog.Definition.Entries.Length != 2)
        throw new InvalidOperationException("Expected the installed Mimesis package to retain two catalog entries.");
}

static void VerifyExternalProcessAdapterPackage()
{
    DefaultClrTypeRefMapper typeMapper = new();
    var request = ExternalGenerationCatalogProtocol.CreateRequest(
        catalogId: "catalog/package-smoke-external",
        catalogRevision: "r1",
        count: 2,
        seed: long.MaxValue,
        valueType: typeMapper.Map(typeof(string), nullability: null),
        configuration: JsonSerializer.SerializeToElement(new SmokeExternalProviderConfiguration("name")),
        locale: "en");
    var restored = ExternalGenerationCatalogProtocol.DeserializeRequest(
        ExternalGenerationCatalogProtocol.SerializeRequest(request));

    Require(restored.RequestId, request.RequestId, "external provider requestId");
    Require(
        restored.SchemaVersion,
        "cohesive-simulation-generation-catalog-provider/v1",
        "external provider schema");
}

static ExternalGenerationCatalogImportDefinition CreateExternalImportDefinition() =>
    ExternalGenerationCatalogImportDefinition.Create(
        catalogId: "catalog/package-smoke-external-cli",
        catalogRevision: "r1",
        count: 2,
        seed: 42,
        valueType: new ScalarTypeRef(ScalarTypeKind.String),
        configuration: JsonSerializer.SerializeToElement(new { prefix = "package-smoke" }),
        provider: "package-smoke-provider",
        providerVersion: "1",
        randomAlgorithm: "package-smoke-provider/local-seed/v1",
        capabilityProfile: new(
            id: "package-smoke-provider/finite-snapshot/v1",
            capabilities:
            [
                GenerationCatalogProducerCapability.FiniteSnapshot,
                GenerationCatalogProducerCapability.LocalSeed
            ],
            sourceReferences:
            [
                SourceReference.Repository(new("eng/package-smoke/Cohesive.Simulation.Consumer/Program.cs"))
            ]),
        sourceReferences:
        [
            SourceReference.Repository(new("eng/package-smoke/Cohesive.Simulation.Consumer/Program.cs"))
        ]);

static async Task ProvideExternalCatalog()
{
    var request = ExternalGenerationCatalogProtocol.DeserializeRequest(await Console.In.ReadToEndAsync());
    var prefix = request.Configuration.GetProperty("prefix").GetString()
                 ?? throw new InvalidOperationException("The package-smoke provider requires a prefix.");
    var values = ImmutableArray.CreateBuilder<ObservationValue>(request.Count);
    for (var index = 0; index < request.Count; index++)
    {
        values.Add(ObservationValue.FromString(
            $"{prefix}-{request.Seed.ToString(CultureInfo.InvariantCulture)}-"
            + index.ToString(CultureInfo.InvariantCulture)));
    }

    var response = new ExternalGenerationCatalogResponse(
        schemaVersion: ExternalGenerationCatalogProtocol.CurrentSchemaVersion,
        requestId: request.RequestId,
        provider: "package-smoke-provider",
        providerVersion: "1",
        values: values.MoveToImmutable());
    await Console.Out.WriteAsync(ExternalGenerationCatalogProtocol.SerializeResponse(response));
}

static PocoGenerationDefinition<SmokeCustomer> CreateCustomers() =>
    Simulation.Define<SmokeCustomer>(customer =>
    {
        var identity = customer.SampleRecord(
            "identity",
            Gen.Catalog<SmokeIdentity>(CreateIdentityCatalog()));
        customer
            .Member(value => value.Name, identity.Project(value => value.Name))
            .Member(value => value.Region, identity.Project(value => value.Region))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90));
    });

static GenerationCatalogDocument CreateIdentityCatalog()
{
    DefaultClrTypeRefMapper typeMapper = new();
    return GenerationCatalogDocument.FromDefinition(new(
        id: "catalog/package-smoke-identities",
        revision: "r1",
        valueType: typeMapper.Map(typeof(SmokeIdentity), nullability: null),
        entries:
        [
            new("identity/ada", ObservationValue.FromObject(new SmokeIdentity("Ada", "north"))),
            new("identity/grace", ObservationValue.FromObject(new SmokeIdentity("Grace", "west")))
        ],
        provenance: new(
            adapter: "cohesive-package-smoke",
            adapterVersion: "1",
            provider: "embedded-fixture",
            providerVersion: "1",
            capabilityProfile: new(
                id: "cohesive-package-smoke/embedded-catalog/v1",
                capabilities:
                [
                    GenerationCatalogProducerCapability.FiniteSnapshot,
                    GenerationCatalogProducerCapability.StructuredValues
                ],
                sourceReferences:
                [
                    SourceReference.Repository(new("eng/package-smoke/Cohesive.Simulation.Consumer/Program.cs"))
                ]),
            sourceReferences: [SourceReference.Repository(new("eng/package-smoke/Cohesive.Simulation.Consumer/Program.cs"))])));
}

static WorldDefinition CreateWorld(PocoGenerationDefinition<SmokeCustomer> customers) =>
    Simulation.DefineWorld("world/package-smoke", "r1", builder => builder
        .Population("customers", count: 2, customers)
        .Exemplar("customer-for-ui", "customers", sequenceIndex: 1));

static async Task VerifyCliReport(string path, string expectedArtifactId, long expectedItemCount)
{
    using var report = JsonDocument.Parse(await File.ReadAllTextAsync(path));
    var root = report.RootElement;
    if (!root.GetProperty("isValid").GetBoolean())
    {
        throw new InvalidOperationException("The CLI verification report is not valid.");
    }

    Require(
        root.GetProperty("schemaVersion").GetString(),
        "cohesive-simulation-cli-verification/v1",
        "CLI verification schema");
    var verification = root.GetProperty("verification");
    Require(
        verification.GetProperty("artifactId").GetString(),
        expectedArtifactId,
        "CLI verified artifactId");
    if (verification.GetProperty("itemCount").GetInt64() != expectedItemCount)
    {
        throw new InvalidOperationException("The CLI verification report has an invalid item count.");
    }
}

static async Task VerifyCatalogCliReport(string path, GenerationCatalogDocument expectedCatalog)
{
    using var report = JsonDocument.Parse(await File.ReadAllTextAsync(path));
    var root = report.RootElement;
    if (!root.GetProperty("isValid").GetBoolean())
    {
        throw new InvalidOperationException("The CLI catalog-verification report is not valid.");
    }

    Require(
        root.GetProperty("schemaVersion").GetString(),
        "cohesive-simulation-cli-catalog-verification/v1",
        "CLI catalog-verification schema");
    var verification = root.GetProperty("verification");
    Require(
        verification.GetProperty("catalogSchemaVersion").GetString(),
        expectedCatalog.SchemaVersion,
        "CLI verified catalog schema");
    Require(
        verification.GetProperty("catalogId").GetString(),
        expectedCatalog.Definition.Id,
        "CLI verified catalog id");
    Require(
        verification.GetProperty("catalogRevision").GetString(),
        expectedCatalog.Definition.Revision,
        "CLI verified catalog revision");
    Require(
        verification.GetProperty("catalogFingerprint").GetString(),
        expectedCatalog.Fingerprint.Value,
        "CLI verified catalog fingerprint");
    if (verification.GetProperty("entryCount").GetInt32() != expectedCatalog.Definition.Entries.Length)
    {
        throw new InvalidOperationException("The CLI catalog-verification report has an invalid entry count.");
    }
    Require(
        verification.GetProperty("provenance").GetProperty("provider").GetString(),
        expectedCatalog.Definition.Provenance.Provider,
        "CLI verified catalog provider");
}

sealed record SmokeIdentity(string Name, string Region);

sealed record SmokeCustomer(string Name, string Region, int Age);

sealed record MimesisSmokePerson(string Name, int Age);

sealed record SmokeExternalProviderConfiguration(string Generator);
