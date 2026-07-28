namespace Cohesive.Tests.Model;

public sealed class FieldPathTests
{
    [Fact]
    public void Parse_FormatsFieldAndElementSegmentsWithoutLosingInformation()
    {
        var path = FieldPath.Parse("source.Customer.lineItem.[]");

        Assert.Equal("source.Customer.lineItem.[]", path.ToString());
    }
    
    [Fact]
    public void Equals_UsesSegmentSequenceValueSemantics()
    {
        var left = new FieldPath(
        [
            FieldPathSegment.ForField("source"),
            FieldPathSegment.ForField("Status")
        ]);

        var right = new FieldPath(
        [
            FieldPathSegment.ForField("source"),
            FieldPathSegment.ForField("Status")
        ]);

        var different = new FieldPath(
        [
            FieldPathSegment.ForField("source"),
            FieldPathSegment.ForField("Other")
        ]);

        Assert.True(left.Equals(right));
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, different);
    }

    [Fact]
    public void IsPrefixOf_IncludesEqualPathsAndProperAncestors()
    {
        var customer = FieldPath.FromField("Customer");
        var customerCopy = FieldPath.FromField("Customer");
        var customerName = FieldPath.Parse("Customer.Name");

        Assert.True(customer.IsPrefixOf(customerCopy));
        Assert.True(customer.IsPrefixOf(customerName));
        Assert.False(customerName.IsPrefixOf(customer));
    }

    [Fact]
    public void Overlaps_IsSymmetricForAncestorsAndFalseForDisjointPaths()
    {
        var customer = FieldPath.FromField("Customer");
        var customerName = FieldPath.Parse("Customer.Name");
        var customerId = FieldPath.Parse("Customer.Id");
        var supplierName = FieldPath.Parse("Supplier.Name");

        Assert.True(customer.Overlaps(customerName));
        Assert.True(customerName.Overlaps(customer));
        Assert.False(customerName.Overlaps(customerId));
        Assert.False(customerName.Overlaps(supplierName));
    }

    [Fact]
    public void PrefixAndOverlap_CompareElementSegmentsBySemanticValue()
    {
        var orderElement = FieldPath.Parse("Orders.[]");
        var orderElementSku = FieldPath.Parse("Orders.[].Sku");
        var orderFieldSku = FieldPath.Parse("Orders.Sku");

        Assert.True(orderElement.IsPrefixOf(orderElementSku));
        Assert.True(orderElement.Overlaps(orderElementSku));
        Assert.False(orderElement.IsPrefixOf(orderFieldSku));
        Assert.False(orderElement.Overlaps(orderFieldSku));
    }

    [Fact]
    public void EndsWithSegment_MatchesTerminalSegmentOnly()
    {
        var path = FieldPath.Parse("source.OrderId");

        Assert.True(path.EndsWith(FieldPathSegment.ForField("OrderId")));
        Assert.False(path.EndsWith(FieldPathSegment.ForField("source")));
    }

    [Fact]
    public void EndsWithPath_MatchesSuffixBySegmentValue()
    {
        var path = FieldPath.Parse("source.Customer.Name");
        var matchingSuffix = FieldPath.Parse("Customer.Name");
        var nonMatchingSuffix = FieldPath.Parse("Customer.Id");
        var longerSuffix = FieldPath.Parse("root.source.Customer.Name");

        Assert.True(path.EndsWith(matchingSuffix));
        Assert.False(path.EndsWith(nonMatchingSuffix));
        Assert.False(path.EndsWith(longerSuffix));
    }

    [Fact]
    public void SegmentTryGetFieldIdentity_ResolvesFieldSegments()
    {
        var fieldSegment = FieldPathSegment.ForField("OrderId");

        Assert.True(fieldSegment.TryGetFieldIdentity(out var fieldIdentity));
        Assert.Equal("OrderId", fieldIdentity);
    }

    [Fact]
    public void SegmentTryGetFieldIdentity_DoesNotProjectLoopSegments()
    {
        var fieldSegment = FieldPathSegment.ForField("Status");
        var loopSegment = FieldPathSegment.ForField("item");

        Assert.True(fieldSegment.TryGetFieldIdentity(out var fieldIdentity));
        Assert.Equal("Status", fieldIdentity);
        Assert.True(loopSegment.TryGetFieldIdentity(out var loopFieldIdentity));
        Assert.Equal("item", loopFieldIdentity);
    }

    [Fact]
    public void Capture_ResolvesDtoMemberPathsAcrossNestedObjectsAndArrays()
    {
        Assert.Equal(
            FieldPath.Parse("Id"),
            FieldPath.Capture<FieldPathCaptureDto>(static dto => dto.Id)
            );
        Assert.Equal(
            FieldPath.Parse("Status"),
            FieldPath.Capture<FieldPathCaptureDto>(static dto => dto.Status)
            );
        Assert.Equal(
            FieldPath.Parse("Details.Name"),
            FieldPath.Capture<FieldPathCaptureDto>(static dto => dto.Details.Name))
            ;
        Assert.Equal(
            FieldPath.Parse("Details.Tags.[]"),
            FieldPath.Capture<FieldPathCaptureDto>(static dto => dto.Details.Tags[0])
            );
        Assert.Equal(
            FieldPath.Parse("Details.Lines.[].Code"),
            FieldPath.Capture<FieldPathCaptureDto>(static dto => dto.Details.Lines[0].Code)
            );
    }

    [Fact]
    public void Capture_ResolvesDictionaryStringKeysAsFieldSegments()
    {
        Assert.Equal(
            FieldPath.Parse("Storage.Default.ConnectionString"),
            FieldPath.Capture<FieldPathDictionaryCaptureDto>(static dto => dto.Storage!["Default"].ConnectionString)
            );
    }

    sealed record FieldPathCaptureDto(string Id, string Status, FieldPathCaptureDetailsDto Details);

    sealed record FieldPathCaptureDetailsDto(string Name, string[] Tags, FieldPathCaptureLineDto[] Lines);

    sealed record FieldPathCaptureLineDto(string Code, string Description);

    sealed record FieldPathDictionaryCaptureDto(IReadOnlyDictionary<string, FieldPathDictionaryStorageDto>? Storage);

    sealed record FieldPathDictionaryStorageDto(string ConnectionString);
}
