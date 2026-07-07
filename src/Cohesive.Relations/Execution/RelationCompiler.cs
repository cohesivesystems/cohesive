using System.Text.Json;
using Cohesive.Model;
using Cohesive.Relations.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Execution;

/// <summary>
/// Compiled relation form with an execution plan.
/// </summary>
public sealed record CompiledRelation(RelationDefinition Definition, RelationPlan Plan);


/// <summary>
/// Compiler that maps JSON or IR relation mappings into executable form.
/// </summary>
public sealed class RelationCompiler
{
    readonly RelationPlanner planner;

    /// <summary>
    /// Creates a compiler with an optional planner override.
    /// </summary>
    public RelationCompiler(RelationPlanner? planner = null)
    {
        this.planner = planner ?? new();
    }

    /// <summary>
    /// Compiles an IR relation definition.
    /// </summary>
    public CompiledRelation Compile(RelationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(definition, planner.Plan(definition));
    }

    /// <summary>
    /// Compiles a UI-authored JSON relation document.
    /// </summary>
    public CompiledRelation CompileJson(string json)
    {
        var definition = RelationJsonMapper.ParseJson(json);
        return Compile(definition);
    }

    /// <summary>
    /// Computes a deterministic hash string for compiled definition + plan.
    /// </summary>
    public static string ComputeFingerprint(CompiledRelation compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        var payload = new
        {
            definition = compiled.Definition,
            plan = compiled.Plan
        };
        return JsonSerializer.Serialize(payload);
    }
}
