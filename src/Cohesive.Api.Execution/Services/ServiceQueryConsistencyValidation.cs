using Cohesive.Relations.Execution;

namespace Cohesive.Api.Execution.Services;

/// <summary>Checks a prepared reader against a declared requirement before any evaluation.</summary>
public static class ServiceQueryConsistencyValidation
{
    /// <summary>Admits stronger guarantees but never silently weakens a requirement.</summary>
    /// <typeparam name="TInput">Query input.</typeparam><typeparam name="TResult">Query result.</typeparam>
    /// <param name="reader">Prepared reader whose guarantee covers the entire evaluation.</param>
    /// <param name="required">Explicit required consistency.</param>
    /// <returns>The same reader, without IO or evaluation.</returns>
    /// <exception cref="ServiceBindingValidationException">The requirement or guarantee is unknown, or the guarantee is insufficient.</exception>
    public static IRelationQueryReader<TInput, TResult> Require<TInput, TResult>(
        IRelationQueryReader<TInput, TResult> reader, RelationQueryConsistency required)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Require(reader.Consistency, required);
        return reader;
    }

    /// <summary>Validates a declared guarantee against endpoint demand without preparing native readers.</summary>
    /// <param name="provided">Minimum reader guarantee.</param>
    /// <param name="required">Endpoint demand.</param>
    /// <exception cref="ServiceBindingValidationException">The guarantee is unknown or insufficient.</exception>
    public static void Require(RelationQueryConsistency provided, RelationQueryConsistency required)
    {
        if (!Enum.IsDefined(required) || !Enum.IsDefined(provided)
            || (required == RelationQueryConsistency.SingleSnapshot && provided != required)
            || (provided == RelationQueryConsistency.Eventual && required != RelationQueryConsistency.Eventual))
            throw ServiceBindingValidationException.Error(ServiceBindingDiagnosticCodes.QueryPlacementUnsupported,
                $"Query requires {required}, but its prepared reader guarantees {provided}.", "/bindings/queries/consistency");
    }
}
