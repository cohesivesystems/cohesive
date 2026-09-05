using System.Security.Cryptography;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using Cohesive.Simulation;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Relations;
using Cohesive.Simulation.Storage;
using Cohesive.Simulation.Worlds;
using Cohesive.Simulation.Xunit;
using Xunit.Sdk;

const string ExpectedArtifactId =
    "csimartifact1_571e3af7ae916eb7d7b55ab04ded535b9fdc779908e207b75cf5506797bb5575";
const string ExpectedManifestFingerprint =
    "571e3af7ae916eb7d7b55ab04ded535b9fdc779908e207b75cf5506797bb5575";
const string ExpectedWorldFingerprint =
    "acfd6737bd655c8f96c6da5b3b4e52f1dac83c548fdee666448440015658aa8d";
const string ExpectedJsonLinesFingerprint =
    "821688c353089e5809d2a30afec85d6a37efc0ece7f09ea4455fab4a8f0207e3";

if (args is ["emit", string coreWorldPath])
{
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
else
{
    throw new ArgumentException(
        "Expected an emit, emit-relationship, verify, or verify-relationship command.");
}

return 0;

static void Require(string? actual, string expected, string property)
{
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected {property} '{expected}' but found '{actual}'.");
    }
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

sealed record SmokeIdentity(string Name, string Region);

sealed record SmokeCustomer(string Name, string Region, int Age);
