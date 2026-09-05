using System.Security.Cryptography;
using Cohesive.Simulation;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Worlds;
using Cohesive.Simulation.Xunit;
using Xunit.Sdk;

const string ExpectedArtifactId =
    "csimartifact1_6fe065c4b5cc4bbaae225389937f5626bb607a6da4ddc69420064f0db4c6e084";
const string ExpectedManifestFingerprint =
    "6fe065c4b5cc4bbaae225389937f5626bb607a6da4ddc69420064f0db4c6e084";
const string ExpectedWorldFingerprint =
    "8abd9cc35964a89cd51052778a2d9e45b8edcfe1ebacd193beb0d8b6e636f76f";
const string ExpectedJsonLinesFingerprint =
    "e51c1638cc9445424a4c1552048232ec3cea8823b37007daba8f9f504f4b3d23";

if (args is ["emit", var worldPath])
{
    var customers = Simulation.Define<SmokeCustomer>(customer =>
    {
        var identity = customer.SampleRecord("identity", Gen.Categorical(
            Gen.Weighted(new SmokeIdentity("Ada", "north"), weight: 1d),
            Gen.Weighted(new SmokeIdentity("Grace", "west"), weight: 1d)));
        customer
            .Member(value => value.Name, identity.Project(value => value.Name))
            .Member(value => value.Region, identity.Project(value => value.Region))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90));
    });
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

sealed record SmokeIdentity(string Name, string Region);

sealed record SmokeCustomer(string Name, string Region, int Age);
