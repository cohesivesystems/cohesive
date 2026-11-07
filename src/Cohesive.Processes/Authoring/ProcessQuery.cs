using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Processes.Authoring;

/// <summary>A bounded native query attached to an observational Process read.</summary>
/// <typeparam name="TInput">Canonical invocation projection.</typeparam>
/// <typeparam name="TResult">Complete result projection.</typeparam>
/// <remarks>The original query owns row semantics. The explicit revision also covers its CLR result projection.
/// Bounds and consistency are persisted in the hosted contract. Runtime readers remain host attachments.</remarks>
public sealed class ProcessQuery<TInput, TResult> where TInput : notnull where TResult : notnull
{
    /// <summary>Declares a bounded read without resolving storage.</summary>
    /// <param name="query">Immutable native query authority.</param>
    /// <param name="revision">Revision of query and result projection.</param>
    /// <param name="maximumRows">Positive complete-result row limit.</param>
    /// <param name="maximumBytes">Positive decoded-result byte limit.</param>
    /// <param name="consistency">Required acquisition guarantee.</param>
    /// <exception cref="ArgumentNullException">Query is null.</exception>
    /// <exception cref="ArgumentException">Query or revision is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive or consistency is undefined.</exception>
    public ProcessQuery(RelationQuery<TInput, TResult> query, ExecutionRevisionId revision,
        int maximumRows, long maximumBytes, RelationQueryConsistency consistency)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.Value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!Enum.IsDefined(consistency)) throw new ArgumentOutOfRangeException(nameof(consistency));
        var document = query.CompilationRequest.DefinitionDocument;
        if (!RelationQueryDocumentSemanticValidator.Validate(document).IsValid || document.Definition is not QueryDefinition definition)
            throw new ArgumentException("A valid native query is required.", nameof(query));
        Query = query;
        Revision = revision;
        MaximumRows = maximumRows;
        MaximumBytes = maximumBytes;
        Consistency = consistency;
        var fingerprint = document.DefinitionFingerprint;
        var native = new ExecutionDefinitionReference(new(definition.Id.Value), revision,
            new(fingerprint.Algorithm, fingerprint.Canonicalization, fingerprint.Value));
        var id = new ExecutionDefinitionId(definition.Id.Value + "/process-read");
        Contract = HostedQuery<TInput, TResult>.Create(id, revision, new("cohesive.processes.native-query", "1"),
            new ProcessQueryConfiguration(maximumRows, maximumBytes, consistency),
            new(new("cohesive.processes.native-query/v1"), new(id.Value), DocumentOrigin.Generated),
            dependencies: [new("query", native)]);
    }

    /// <summary>Canonical row semantics and local typed result projection.</summary>
    public RelationQuery<TInput, TResult> Query { get; }
    /// <summary>Explicit native query and projection revision.</summary>
    public ExecutionRevisionId Revision { get; }
    /// <summary>Positive complete-result row limit.</summary>
    public int MaximumRows { get; }
    /// <summary>Positive decoded-result byte limit.</summary>
    public long MaximumBytes { get; }
    /// <summary>Required acquisition guarantee.</summary>
    public RelationQueryConsistency Consistency { get; }
    /// <summary>Portable observational contract linking the exact native query and policy.</summary>
    public HostedQuery<TInput, TResult> Contract { get; }
    /// <summary>Exact observational contract selected by the Process.</summary>
    public ExecutionDefinitionReference Reference => Contract.Reference;
}

internal sealed record ProcessQueryConfiguration(int MaximumRows, long MaximumBytes, RelationQueryConsistency Consistency);
