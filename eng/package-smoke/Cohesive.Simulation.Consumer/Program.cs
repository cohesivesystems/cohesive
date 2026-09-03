using System.Security.Cryptography;
using Cohesive.Simulation;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Worlds;
using Cohesive.Simulation.Xunit;
using Xunit.Sdk;

const string ExpectedArtifactId =
    "csimartifact1_8ca335a80bba74b7c448e78027f408b429b29fbcb15065093e44ec5756a4a8b1";
const string ExpectedManifestFingerprint =
    "8ca335a80bba74b7c448e78027f408b429b29fbcb15065093e44ec5756a4a8b1";
const string ExpectedWorldFingerprint =
    "f89dcfb60abda64c3e857aa64709c3e74b0772f33d9c1982213d7a2b2f1dabf2";
const string ExpectedJsonLinesFingerprint =
    "1416245396c3ed8e175433e5d75fb1b99ee13f0ea89f3230ee2292aaf89616c8";

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
