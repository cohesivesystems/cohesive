using System.Text.Json;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Storage.Commits;
using Cohesive.Tests.Storage.Conformance;

namespace Cohesive.Tests.Storage;

public sealed class StorageCommitIntentTests
{
    [Fact]
    public void CanonicalDeclarationIsOrderIndependentAndFingerprintsEveryDependency()
    {
        var a = new StorageCommitWrite(StorageCommitConformance.Address("a"), StorageCommitConformance.Value("value"));
        var b = new StorageCommitWrite(StorageCommitConformance.Address("b"), StorageCommitConformance.Value("another"), new("token"));
        var one = StorageCommitConformance.Intent("operation", a, b);
        var two = StorageCommitConformance.Intent("operation", b, a);
        Assert.Equal(one.Fingerprint, two.Fingerprint);
        Assert.Equal(StorageCommitJson.Serialize(one), StorageCommitJson.Serialize(two));
        Assert.Equal(one.Fingerprint, StorageCommitJson.Deserialize(StorageCommitJson.Serialize(one)).Fingerprint);
        Assert.NotEqual(one.Fingerprint, new StorageCommitIntent(one.ReceiptAddress, one.Writes, StorageCommitConformance.Value("changed-result")).Fingerprint);
        Assert.NotEqual(one.Fingerprint, StorageCommitConformance.Intent("operation", a,
            new(b.Address, b.Value, new("other-token"))).Fingerprint);
        Assert.NotEqual(one.Fingerprint, new StorageCommitIntent(one.ReceiptAddress, one.Writes, one.Result,
            [new("query/v1", a.Address)]).Fingerprint);
        Assert.NotEqual(one.Fingerprint, new StorageCommitIntent(StorageCommitConformance.Address("operation", target: "other"), one.Writes, one.Result).Fingerprint);
    }

    [Fact]
    public void RejectsInvalidAndAmbiguousDeclarations()
    {
        var write = new StorageCommitWrite(StorageCommitConformance.Address("a"), StorageCommitConformance.Value("v"));
        var intent = StorageCommitConformance.Intent("op", write);
        Assert.Throws<ArgumentException>(() => StorageCommitConformance.Intent("op", write, write));
        Assert.Throws<ArgumentException>(() => StorageCommitConformance.Intent("op"));
        Assert.Throws<ArgumentException>(() => new StorageCommitWrite(write.Address, PortableValue.Unknown(RunControlFixture.StringContract)));
        Assert.Throws<ArgumentNullException>(() => new StorageCommitWrite(write.Address, write.Value, default(EntityConcurrencyToken)));
        Assert.Throws<ArgumentException>(() => StorageCommitConformance.Address("\ud800"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StorageCommitIntent(intent.ReceiptAddress, intent.Writes, intent.Result, formatVersion: 2));
        var json = StorageCommitJson.Serialize(intent);
        Assert.Throws<JsonException>(() => StorageCommitJson.Deserialize(json.Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => StorageCommitJson.Deserialize(json.Insert(1, "\"unknown\":1,")));
    }

    [Fact]
    public void WireFormatRetainsTaggedBinaryAndDecimalValues()
    {
        var binary = PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.Bytes)), ObservationValue.FromBytes(new byte[] { 0, 1, 254, 255 }));
        var decimalValue = PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.Decimal)), ObservationValue.FromDecimal(123.4567890123456789012345678m));
        var intent = new StorageCommitIntent(StorageCommitConformance.Address("tagged"),
            [new(StorageCommitConformance.Address("blob"), binary)], decimalValue);
        var json = StorageCommitJson.Serialize(intent);
        Assert.Equal(json, StorageCommitJson.Serialize(StorageCommitJson.Deserialize(json)));
        Assert.Equal(intent.Fingerprint, StorageCommitJson.Deserialize(json).Fingerprint);
    }

    [Fact]
    public void GuardEvidenceRequiresAWriteAndAQualifiedProfile()
    {
        var write = new StorageCommitWrite(StorageCommitConformance.Address("state"), StorageCommitConformance.Value("v"));
        var intent = new StorageCommitIntent(StorageCommitConformance.Address("op"), [write], write.Value,
            [new("query/v1", StorageCommitConformance.Address("missing-guard"))]);
        var capable = new StorageCommitCapabilities(null, SupportsMultiplePartitions: true, SupportsQueryGuards: true);
        Assert.Equal(StorageCommitDisposition.Unsupported, capable.Validate(intent)!.Disposition);
        var guarded = new StorageCommitIntent(intent.ReceiptAddress, [write], write.Value, [new("query/v1", write.Address)]);
        Assert.Null(capable.Validate(guarded));
        Assert.NotNull((capable with { SupportsQueryGuards = false }).Validate(guarded));
        Assert.Equal(StorageCommitDisposition.Unsupported, (capable with { MaxAtomicItems = 1 }).Validate(guarded)!.Disposition);
        Assert.Null((capable with { MaxAtomicItems = 2 }).Validate(guarded));
        Assert.Null((capable with { MaxPartitionKeyUtf8Bytes = 4 }).ValidateAddress(StorageCommitConformance.Address("id", partition: "éé")));
        Assert.NotNull((capable with { MaxPartitionKeyUtf8Bytes = 3 }).ValidateAddress(StorageCommitConformance.Address("id", partition: "éé")));
    }
}
