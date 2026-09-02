using System.Security.Cryptography;
using Cohesive.Simulation;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Worlds;

const string ExpectedArtifactId =
    "csimartifact1_73e9c87d107576fe2a4d4d161829e9ea7ca9d6b8315d91860ea7a62891bc1393";
const string ExpectedManifestFingerprint =
    "73e9c87d107576fe2a4d4d161829e9ea7ca9d6b8315d91860ea7a62891bc1393";
const string ExpectedWorldFingerprint =
    "af30a71937b54f72d10de9c68938110624edb6bd778f829a2f65b53fe64b82eb";
const string ExpectedJsonLinesFingerprint =
    "8f6947d294acb8ea7141caefad3c00a98a78294a31c6976b18f6c047f3410955";

if (args is ["emit", var worldPath])
{
    var customers = Simulation.Define<SmokeCustomer>(customer => customer
        .Member(value => value.Name, Gen.Categorical(
            Gen.Weighted("Ada", weight: 1d),
            Gen.Weighted("Grace", weight: 1d)))
        .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));
    var compiledCustomers = customers.Compile();
    var propertyRun = compiledCustomers.CheckProperty(
        seed: 42,
        property: static customer => customer.Age < 50);
    if (propertyRun.Status != PropertyCaseRunStatus.CounterexampleFound)
    {
        throw new InvalidOperationException(
            $"Expected a property counterexample but found '{propertyRun.Status}'.");
    }

    var replayed = compiledCustomers.ReplayPropertyCase(
        propertyRun.BestCounterexample!.Replay.ToToken());
    Require(replayed.Name, "Ada", "property counterexample name");
    if (replayed.Age != 50)
        throw new InvalidOperationException($"Expected property counterexample age '50' but found '{replayed.Age}'.");

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

    await using (FileStream wireInput = File.OpenRead(jsonLinesPath))
    {
        var wireFingerprint = Convert.ToHexString(await SHA256.HashDataAsync(wireInput)).ToLowerInvariant();
        Require(wireFingerprint, ExpectedJsonLinesFingerprint, "JSON Lines wire fingerprint");
    }

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
