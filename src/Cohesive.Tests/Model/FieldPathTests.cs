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
    public void TryGetDirectFieldName_RejectsNestedAndCollectionNavigation()
    {
        Assert.True(FieldPath.FromField("Status").TryGetDirectFieldName(out var fieldName));
        Assert.Equal("Status", fieldName);

        Assert.False(FieldPath.Parse("Customer.Name").TryGetDirectFieldName(out var nestedFieldName));
        Assert.Empty(nestedFieldName);
        Assert.False(FieldPath.Parse("Orders.[]").TryGetDirectFieldName(out var collectionFieldName));
        Assert.Empty(collectionFieldName);
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

    [Fact]
    public void Capture_WithResolver_DelegatesTheCompleteReadablePropertyChain()
    {
        Type? capturedRoot = null;
        string[] capturedMembers = [];

        var path = FieldPath.Capture(
            static (FieldPathCaptureDto dto) => dto.Details.Name,
            (rootType, members) =>
            {
                capturedRoot = rootType;
                capturedMembers = [.. members.Select(static member => member.Name)];
                return FieldPath.Parse("payload.legal_name");
            });

        Assert.Equal(typeof(FieldPathCaptureDto), capturedRoot);
        Assert.Equal(["Details", "Name"], capturedMembers);
        Assert.Equal(FieldPath.Parse("payload.legal_name"), path);
    }

    [Fact]
    public void Capture_WithResolver_RejectsNonPropertyPathComponentsBeforeResolution()
    {
        var called = false;

        Assert.Throws<ArgumentException>(() => FieldPath.Capture(
            static (FieldPathCaptureDto dto) => dto.Details.Tags[0],
            (_, _) =>
            {
                called = true;
                return FieldPath.Parse("ignored");
            }));
        Assert.False(called);
    }

    [Fact]
    public void Capture_WithResolver_RejectsAnEmptyResolvedPath()
    {
        Assert.Throws<InvalidOperationException>(() => FieldPath.Capture(
            static (FieldPathCaptureDto dto) => dto.Id,
            static (_, _) => default));
    }

    sealed record FieldPathCaptureDto(string Id, string Status, FieldPathCaptureDetailsDto Details);

    sealed record FieldPathCaptureDetailsDto(string Name, string[] Tags, FieldPathCaptureLineDto[] Lines);

    sealed record FieldPathCaptureLineDto(string Code, string Description);

    sealed record FieldPathDictionaryCaptureDto(IReadOnlyDictionary<string, FieldPathDictionaryStorageDto>? Storage);

    sealed record FieldPathDictionaryStorageDto(string ConnectionString);
}
