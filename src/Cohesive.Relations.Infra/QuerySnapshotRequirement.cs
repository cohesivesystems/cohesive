using Cohesive.Infra;
using Cohesive.Relations.Compilation;

namespace Cohesive.Relations.Infra;

/// <summary>A provider-neutral, explicitly versioned single-snapshot query execution requirement.</summary>
/// <remarks>The author owns the identity and revision. Compiler changes alter retained evidence, never
/// the public capability identity. Change the revision when the semantic query contract changes.</remarks>
public sealed class QuerySnapshotRequirement
{
    /// <summary>Declares and validates a canonical query requirement without provider configuration.</summary>
    /// <param name="id">Stable application-owned query capability identity.</param>
    /// <param name="revision">Explicit semantic contract revision.</param>
    /// <param name="request">Exact query definition and semantic snapshots.</param>
    /// <exception cref="ArgumentException">An identity or revision is empty.</exception>
    /// <exception cref="ArgumentNullException">Request is null.</exception>
    /// <exception cref="RelationQueryPreparationException">Semantic compilation fails.</exception>
    public QuerySnapshotRequirement(string id, string revision, RelationQueryCompilationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentNullException.ThrowIfNull(request);
        var compilation = RelationQueryStaticCompiler.Compile(request);
        if (!compilation.IsSuccessful) throw new RelationQueryPreparationException("query preparation", compilation);
        Capability = new($"relations/query-snapshot/v1/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(revision)}");
        Request = request;
        Plan = RelationQueryCompiledPlanReference.From(compilation.Plan!);
    }
    /// <summary>Stable versioned guarantee identity usable by any implementing adapter.</summary>
    public InfrastructureCapabilityId Capability { get; }
    /// <summary>Canonical compilation input retained for target preparation.</summary>
    public RelationQueryCompilationRequest Request { get; }
    /// <summary>Exact semantic and compiler affinity that each target must validate.</summary>
    public RelationQueryCompiledPlanReference Plan { get; }
}
