using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;

namespace Cohesive.Api.Execution.Services;

// One identity for ownership, read selection and runtime lookup, including shapes and relationship inputs.
internal static class ServiceQueryIdentity
{
    // Requests are immutable. Weak ownership bounds retention to the declaration lifetime;
    // shared preparation slots publish successes once and leave failures retryable.
    static readonly WeakPreparationCache<RelationQueryCompilationRequest, RelationQueryPlanComponentFingerprint> identities = new();
    internal static RelationQueryPlanComponentFingerprint For(RelationQueryCompilationRequest request) =>
        identities.Get(request, static request => Prepare(request));

    static RelationQueryPlanComponentFingerprint Prepare(RelationQueryCompilationRequest request)
    {
        var compiled = RelationQueryStaticCompiler.Compile(request);
        if (!compiled.IsSuccessful) throw new RelationQueryPreparationException("storage ownership", compiled);
        return For(compiled, out _);
    }

    internal static RelationQueryPlanComponentFingerprint For(RelationQueryCompilationResult compilation,
        out RelationQueryCompiledPlanReference reference)
    {
        reference = RelationQueryCompiledPlanReference.From(compilation.Plan!);
        return RelationQueryCompiledPlanReferenceFingerprinter.Compute(reference);
    }
}
