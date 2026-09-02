using Cohesive.Simulation;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Worlds;

const string ExpectedArtifactId =
    "csimartifact1_73e9c87d107576fe2a4d4d161829e9ea7ca9d6b8315d91860ea7a62891bc1393";
const string ExpectedManifestFingerprint =
    "73e9c87d107576fe2a4d4d161829e9ea7ca9d6b8315d91860ea7a62891bc1393";
const string ExpectedWorldFingerprint =
    "af30a71937b54f72d10de9c68938110624edb6bd778f829a2f65b53fe64b82eb";

if (args is ["emit", var worldPath])
{
    var customers = Simulation.Define<SmokeCustomer>(customer => customer
        .Member(value => value.Name, Gen.Categorical(
            Gen.Weighted("Ada", weight: 1d),
            Gen.Weighted("Grace", weight: 1d)))
        .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));
    var world = Simulation.DefineWorld("world/package-smoke", "r1", builder => builder
        .Population("customers", count: 2, customers)
        .Exemplar("customer-for-ui", "customers", sequenceIndex: 1));
    await File.WriteAllTextAsync(worldPath, WorldDefinitionJsonSerializer.Serialize(world));
}
else if (args is ["verify", var jsonLinesPath, var manifestPath])
{
    var manifest = WorldArtifactManifestJsonSerializer.Deserialize(await File.ReadAllTextAsync(manifestPath));
    Require(manifest.ArtifactId.Value, ExpectedArtifactId, "artifactId");
    Require(manifest.Fingerprint.Value, ExpectedManifestFingerprint, "manifest fingerprint");
    Require(manifest.World.Fingerprint.Value, ExpectedWorldFingerprint, "world fingerprint");

    var exemplar = manifest.GetExemplar("customer-for-ui");
    Require(exemplar.PopulationId, "customers", "exemplar populationId");
    if (exemplar.SequenceIndex != 1)
        throw new InvalidOperationException("Manifest exemplar has an invalid sequence index.");

    await using FileStream jsonLines = File.OpenRead(jsonLinesPath);
    var verification = await WorldJsonLinesVerifier.VerifyAsync(manifest, jsonLines);
    Require(verification.ArtifactId.Value, ExpectedArtifactId, "verified artifactId");
    Require(verification.TargetId, "package-smoke/cli", "targetId");
    if (verification.BatchSize != 1 || verification.ItemCount != 2)
    {
        throw new InvalidOperationException(
            $"Expected batch size 1 and two items but found '{verification.BatchSize}' and "
            + $"'{verification.ItemCount}'.");
    }
}
else
{
    throw new ArgumentException(
        "Expected 'emit <world-path>' or 'verify <json-lines-path> <manifest-path>'.");
}

return 0;

static void Require(string? actual, string expected, string property)
{
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"Expected {property} '{expected}' but found '{actual}'.");
}

sealed record SmokeCustomer(string Name, int Age);
