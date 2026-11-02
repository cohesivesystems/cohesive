using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;

namespace Cohesive.Api.Execution.Services;

// One identity for ownership, read selection and runtime lookup, including shapes and relationship inputs.
internal static class ServiceQueryIdentity
{
    internal static RelationQueryPlanComponentFingerprint For(RelationQueryCompilationRequest request)
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
