namespace Cohesive.Relations.Queries;

/// <summary>
/// Compiles aggregation plans to a backend-specific representation.
/// </summary>
/// <typeparam name="TCompiledAggregation">Compiled backend request type.</typeparam>
public interface IAggregationCompiler<out TCompiledAggregation>
{
    /// <summary>
    /// Capabilities supported by the compiler.
    /// </summary>
    AggregationBackendCapability Capabilities { get; }

    /// <summary>
    /// Compiles an aggregation plan.
    /// </summary>
    TCompiledAggregation Compile(AggregationPlan plan);
}