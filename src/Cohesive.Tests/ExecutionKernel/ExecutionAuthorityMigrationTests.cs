using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Api;
using Cohesive.Host.Configuration;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionAuthorityMigrationTests
{
    [Fact]
    public void ApiAndAspNetTransitionBoundaries_ConsumeOnlyExactCanonicalAuthority()
    {
        Assert.Null(typeof(ApiOperation).Assembly.GetType("Cohesive.Transitions.Model.TransitionDefinition"));

        var bindings = typeof(EntityApiOperationBinding)
            .GetMethods()
            .Where(static method => method.Name == nameof(EntityApiOperationBinding.Transition))
            .ToArray();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, static binding =>
        {
            Assert.Contains(
                binding.GetParameters(),
                static parameter => parameter.ParameterType == typeof(CompiledTransitionPlan));
            Assert.DoesNotContain(
                binding.GetParameters(),
                static parameter => string.Equals(parameter.Name, "transitionName", StringComparison.Ordinal));
        });
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

        var hostAssembly = typeof(DomainModelExternalDsl).Assembly;
        string[] retiredHostTypes =
        [
            "Cohesive.Host.Transitions.TelemetryEffectHandlerRegistration",
            "Cohesive.Host.Transitions.TelemetryEffectHandlerWrapper`2",
            "Cohesive.Host.Transitions.TelemetryEffectHandlerWrapperOptions`2"
        ];
        Assert.All(retiredHostTypes, typeName => Assert.Null(hostAssembly.GetType(typeName)));
    }
}
