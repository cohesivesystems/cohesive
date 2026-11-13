using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Api;
using Cohesive.Host.Services;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionAuthorityMigrationTests
{
    [Fact]
    public void ApiAndAspNetTransitionBoundaries_ConsumeOnlyExactCanonicalAuthority()
    {
        Assert.Null(typeof(ApiOperation).Assembly.GetType("Cohesive.Transitions.Model.TransitionDefinition"));

        Assert.DoesNotContain(typeof(EntityApiOperationBinding).GetMethods(),
            static method => method.Name == "Transition");
        var binding = Assert.Single(typeof(EntityTransitionCommandExtensions).GetMethods(),
            static method => method.Name == nameof(EntityTransitionCommandExtensions.BindTransition));
        Assert.Contains(binding.GetParameters(), static parameter => parameter.ParameterType.IsGenericType
            && parameter.ParameterType.GetGenericTypeDefinition() == typeof(Transition<,,>));
    }

    [Fact]
    public void EntityModel_DoesNotExposeACompetingTransitionCatalog()
    {
        Assert.DoesNotContain(
            typeof(EntityDefinition).GetProperties(),
            static property => string.Equals(property.Name, "Transitions", StringComparison.Ordinal));
    }

    [Fact]
    public void ShippedAssemblies_DoNotContainRetiredFlatTransitionOrEffectAuthorities()
    {
        var transitionAssembly = typeof(TransitionDefinitionDocuments).Assembly;
        string[] retiredTransitionTypes =
        [
            "Cohesive.Transitions.Model.TransitionDefinition",
            "Cohesive.Transitions.Model.EffectDefinition",
            "Cohesive.Transitions.Model.EffectContinuationDefinition",
            "Cohesive.Transitions.Model.FieldUpdateDefinition",
            "Cohesive.Transitions.Model.TransitionParameterDefinition",
            "Cohesive.Transitions.Model.TransitionPreconditionDefinition",
            "Cohesive.Transitions.Authoring.Transition`2",
            "Cohesive.Transitions.Authoring.TransitionBuilder",
            "Cohesive.Transitions.Authoring.TransitionExpressionBuilder`2",
            "Cohesive.Transitions.Authoring.TransitionExpressionCompiler",
            "Cohesive.Transitions.Authoring.TransitionExpressionDsl",
            "Cohesive.Transitions.Authoring.DeclarativeEntityRuntime",
            "Cohesive.Transitions.Authoring.TransitionResult",
            "Cohesive.Transitions.Authoring.EffectRequest",
            "Cohesive.Transitions.Authoring.EffectContinuation",
            "Cohesive.Transitions.Authoring.EffectSnapshot",
            "Cohesive.Transitions.Authoring.IEffectRequest",
            "Cohesive.Transitions.Authoring.IEffectHandler`2",
            "Cohesive.Transitions.Authoring.IEffectHandlerBinding",
            "Cohesive.Transitions.Authoring.EffectHandlerBinding`2",
            "Cohesive.Transitions.Authoring.TransitionPatchProjector",
            "Cohesive.Transitions.Authoring.SnapshotTokenProjector",
            "Cohesive.Transitions.Compilation.TransitionExpressionAnalyzer"
        ];

        Assert.All(retiredTransitionTypes, typeName => Assert.Null(transitionAssembly.GetType(typeName)));
        Assert.Contains(
            transitionAssembly.GetExportedTypes(),
            static type => type == typeof(TransitionDefinitionDocuments));

        var hostAssembly = typeof(HostProcessWorker).Assembly;
        string[] retiredHostTypes =
        [
            "Cohesive.Host.Transitions.TelemetryEffectHandlerRegistration",
            "Cohesive.Host.Transitions.TelemetryEffectHandlerWrapper`2",
            "Cohesive.Host.Transitions.TelemetryEffectHandlerWrapperOptions`2"
        ];
        Assert.All(retiredHostTypes, typeName => Assert.Null(hostAssembly.GetType(typeName)));
    }
}
