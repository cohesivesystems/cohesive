using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Immutable offline preparation shared by runtime binding and infrastructure evidence.</summary>
/// <remarks>Proves plan feasibility and bounds, not live access, schema deployment or a cross-source snapshot.</remarks>
public sealed class PostgresComposedQueryPreparation
{
    internal PostgresComposedQueryPreparation(RelationQueryCompilationRequest request, RelationQueryCompilationResult compilation,
        RelationQuerySubplan? cut, PostgresRelationQueryCompiledArtifact? prefix, RelationQueryPhysicalPreparation execution,
        RelationQuerySourcePlacement placement, PostgresRelationQueryStorageBinding? storage,
        RelationQuerySourceInstanceId localSource, PostgresRelationQueryComposedPolicy policy, ImmutableArray<EntityRelationQuerySourceBinding> sources)
    {
        Request = request;
        Compilation = compilation;
        Cut = cut;
        Prefix = prefix;
        Execution = execution;
        Placement = placement;
        Storage = storage;
        LocalSource = localSource;
        Policy = policy;
        Sources = sources;
    }
    /// <summary>Original semantic authority.</summary>
    public RelationQueryCompilationRequest Request { get; }
    /// <summary>Effective semantic compilation.</summary>
    public RelationQueryCompilationResult Compilation { get; }
    /// <summary>Optional closed native prefix and composed remainder.</summary>
    public RelationQuerySubplan? Cut { get; }
    /// <summary>Native prefix artifact, if a closed prefix was selected.</summary>
    public PostgresRelationQueryCompiledArtifact? Prefix { get; }
    /// <summary>Interpreter capability evidence.</summary>
    public RelationQueryRealizationReport Realization => Execution.Realization;
    /// <summary>Exact source placement.</summary>
    public RelationQuerySourcePlacement Placement { get; }
    /// <summary>Validated bounded physical plan.</summary>
    public RelationQueryPhysicalPlanningResult Physical => Execution.Physical;
    /// <summary>Reusable validated physical preparation.</summary>
    public RelationQueryPhysicalPreparation Execution { get; }
    /// <summary>Native acquisition binding when no prefix is selected.</summary>
    public PostgresRelationQueryStorageBinding? Storage { get; }
    /// <summary>Local PostgreSQL or prefix-rowset source.</summary>
    public RelationQuerySourceInstanceId LocalSource { get; }
    /// <summary>Explicit partition and execution bounds.</summary>
    public PostgresRelationQueryComposedPolicy Policy { get; }
    /// <summary>Exactly the remote sources needed by the plan.</summary>
    public ImmutableArray<EntityRelationQuerySourceBinding> Sources { get; }
    /// <summary>Guarantee across the entire result, including every source acquisition.</summary>
    public RelationQueryConsistency Consistency => RelationQueryConsistency.IndependentSources;
}
