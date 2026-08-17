using Cohesive.AI.Training;
using Cohesive.Model;
using Cohesive.Model.Authoring;

namespace Cohesive.AI.Tests.Training;

public sealed class TrainingDatasetArtifactTests
{
    [Fact]
    public void Contract_UsesPortableStructuralLocation()
    {
        var contract = Assert.IsType<ObjectTypeRef>(
            new DefaultClrTypeRefMapper().Map(typeof(TrainingDatasetArtifact), null));

        var location = Assert.Single(contract.Fields, static field => field.Name == nameof(TrainingDatasetArtifact.Location));
        Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(location.Type).Kind);
        Assert.DoesNotContain(contract.Fields, static field => field.Type is OpaqueRuntimeTypeRef);
    }

    [Fact]
    public void TrainingJobStateContract_IsFullyPortableAndStructural()
    {
        var contract = new DefaultClrTypeRefMapper().Map(typeof(TrainingJobState), null);

        AssertNoOpaqueTypes(contract);
    }

    static void AssertNoOpaqueTypes(TypeRef type)
    {
        Assert.IsNotType<OpaqueRuntimeTypeRef>(type);
        switch (type)
        {
            case ObjectTypeRef objectType:
                foreach (var field in objectType.Fields)
                    AssertNoOpaqueTypes(field.Type);
                break;
            case ArrayTypeRef arrayType:
                AssertNoOpaqueTypes(arrayType.ElementType);
                break;
        }
    }
}
