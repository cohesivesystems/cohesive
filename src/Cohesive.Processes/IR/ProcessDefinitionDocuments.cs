using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.IR;

/// <summary>Stable diagnostic codes emitted by the canonical Process document facade.</summary>
public static class ProcessDefinitionDocumentDiagnosticCodes
{
    /// <summary>The shared execution document does not contain a Process definition.</summary>
    public const string KindMismatch = "processes.document.kindMismatch";

    /// <summary>The canonical definition payload cannot be projected as typed Process IR.</summary>
    public const string DefinitionProjectionInvalid = "processes.document.definitionProjectionInvalid";

    /// <summary>The projected Process has a different canonical wire representation than the persisted payload.</summary>
    public const string DefinitionWireNonCanonical = "processes.document.definitionWireNonCanonical";
}

/// <summary>Creates and validates canonical Process definitions in the shared execution-definition envelope.</summary>
/// <remarks>
/// <see cref="ExecutionDefinitionDocument"/> remains the sole persisted document, metadata, schema-version, and
/// fingerprint authority. This facade supplies Process-kind dispatch, strict typed projection, canonical-wire
/// validation, and Process-specific semantic validation without introducing a parallel envelope.
/// </remarks>
public static class ProcessDefinitionDocuments
{
    static readonly ExecutionDefinitionDocumentProjection<ProcessDefinition> Projection = new(
        kind: new(ProcessWireNames.DefinitionKind),
        kindMismatchCode: ProcessDefinitionDocumentDiagnosticCodes.KindMismatch,
        projectionInvalidCode: ProcessDefinitionDocumentDiagnosticCodes.DefinitionProjectionInvalid,
        wireNonCanonicalCode: ProcessDefinitionDocumentDiagnosticCodes.DefinitionWireNonCanonical,
        wireNonCanonicalMessage:
            "The persisted definition is not the unique canonical typed Process v1 wire representation.",
        projectionFailurePath: FindProjectionFailurePath);

    /// <summary>Shared execution-definition kind for canonical Process IR.</summary>
    public static ExecutionDefinitionKind Kind => Projection.Kind;

    /// <summary>Creates a fingerprinted shared execution document containing typed Process IR.</summary>
    /// <param name="definitionId">Stable identity shared by every semantic revision of the Process.</param>
    /// <param name="revisionId">Stable identity of this accepted Process revision.</param>
    /// <param name="definition">Canonical typed Process definition payload.</param>
    /// <param name="provenance">Required producer and root-source attribution.</param>
    /// <param name="extensions">Optional exact-versioned semantic extensions.</param>
    /// <param name="displayName">Optional human-facing name excluded from semantic fingerprinting.</param>
    /// <param name="description">Optional human-facing description excluded from semantic fingerprinting.</param>
    /// <param name="sourceMap">Optional normalized per-construct source attribution.</param>
    /// <param name="diagnostics">Optional retained authoring or validation diagnostics.</param>
    /// <returns>A current-version shared execution document whose canonical payload is <paramref name="definition"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity is default, the definition does not serialize as a JSON object or contains duplicate properties,
    /// or an extension or retained metadata value violates its structural contract.
    /// </exception>
    /// <exception cref="JsonException">The typed definition cannot be encoded by the strict wire contract.</exception>
    /// <exception cref="InvalidOperationException">The typed definition has no canonical JSON representation.</exception>
    /// <exception cref="NotSupportedException">The typed definition contains an unsupported runtime type.</exception>
    public static ExecutionDefinitionDocument Create(
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        ProcessDefinition definition,
        ExecutionProvenance provenance,
        ImmutableArray<ExecutionDefinitionExtension> extensions = default,
        string? displayName = null,
        string? description = null,
        ExecutionSourceMap? sourceMap = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default) =>
        ExecutionDefinitionDocument.Create(
            Kind,
            definitionId,
            revisionId,
            definition,
            provenance,
            extensions,
            displayName,
            description,
            sourceMap,
            diagnostics);

    /// <summary>Validates shared envelope integrity, canonical Process projection, and Process IR semantics.</summary>
    /// <param name="document">Shared execution-definition document to validate.</param>
    /// <returns>Deterministically ordered shared and Process-specific diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">Semantic content cannot be encoded by the strict wire contract.</exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static DocumentValidationResult Validate(ExecutionDefinitionDocument document) =>
        ValidateCore(document, context: null);

    /// <summary>Validates a Process document using exact linked-definition, interaction, and shape evidence.</summary>
    /// <param name="document">Shared execution-definition document to validate.</param>
    /// <param name="context">External semantic evidence available while linking the Process.</param>
    /// <returns>Deterministically ordered shared, projection, and Process semantic diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> or <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="JsonException">Semantic content cannot be encoded by the strict wire contract.</exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static DocumentValidationResult Validate(
        ExecutionDefinitionDocument document,
        ProcessDefinitionValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValidateCore(document, context);
    }

    /// <summary>Attempts to read and validate a canonical Process document.</summary>
    /// <param name="json">Persisted shared execution-definition JSON.</param>
    /// <param name="document">Receives the parsed shared document when structural deserialization succeeds.</param>
    /// <param name="definition">Receives typed Process IR only when every validation layer succeeds.</param>
    /// <returns>Deterministically ordered read, integrity, projection, wire, and Process diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out ExecutionDefinitionDocument? document,
        out ProcessDefinition? definition)
    {
        var shared = ExecutionDefinitionJsonSerializer.TryDeserialize(json, out document);
        return Projection.ValidateAndProject(
            shared,
            document,
            static candidate => ProcessDefinitionValidator.Validate(candidate),
            out definition);
    }

    /// <summary>Attempts to read and validate a Process document using exact external semantic evidence.</summary>
    /// <param name="json">Persisted shared execution-definition JSON.</param>
    /// <param name="context">External semantic evidence available while linking the Process.</param>
    /// <param name="document">Receives the parsed shared document when structural deserialization succeeds.</param>
    /// <param name="definition">Receives typed Process IR only when every validation layer succeeds.</param>
    /// <returns>Deterministically ordered read, integrity, projection, wire, and Process diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult TryDeserialize(
        string json,
        ProcessDefinitionValidationContext context,
        out ExecutionDefinitionDocument? document,
        out ProcessDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(context);
        var shared = context.ShapeGraph is null
            ? ExecutionDefinitionJsonSerializer.TryDeserialize(json, out document)
            : ExecutionDefinitionJsonSerializer.TryDeserialize(json, context.ShapeGraph, out document);
        return Projection.ValidateAndProject(
            shared,
            document,
            candidate => ProcessDefinitionValidator.Validate(candidate, context),
            out definition);
    }

    static DocumentValidationResult ValidateCore(
        ExecutionDefinitionDocument document,
        ProcessDefinitionValidationContext? context)
    {
        ArgumentNullException.ThrowIfNull(document);
        var graph = context?.ShapeGraph;
        return Projection.ValidateAndProject(
            ExecutionDefinitionDocumentValidator.Validate(document, graph),
            document,
            candidate => context is null
                ? ProcessDefinitionValidator.Validate(candidate)
                : ProcessDefinitionValidator.Validate(candidate, context),
            out _);
    }

    static string? FindProjectionFailurePath(JsonElement definition, Exception exception)
    {
        var serializerPath = (exception as JsonException)?.Path;
        if (!string.IsNullOrEmpty(serializerPath) && serializerPath != "$")
            return serializerPath;
        if (!definition.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return serializerPath;
        }

        var nodeIndex = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            var nodePath = $"$.nodes[{nodeIndex}]";
            if (node.ValueKind != JsonValueKind.Object
                || !node.TryGetProperty(ProcessWireNames.NodeDiscriminator, out var discriminator)
                || discriminator.ValueKind != JsonValueKind.String
                || !IsKnownNode(discriminator.GetString()))
            {
                return nodePath;
            }

            if (string.Equals(
                    discriminator.GetString(),
                    ProcessWireNames.AwaitMatchNode,
                    StringComparison.Ordinal)
                && node.TryGetProperty("clauses", out var clauses)
                && clauses.ValueKind == JsonValueKind.Array)
            {
                var clauseIndex = 0;
                foreach (var clause in clauses.EnumerateArray())
                {
                    if (clause.ValueKind != JsonValueKind.Object
                        || !clause.TryGetProperty(ProcessWireNames.AwaitClauseDiscriminator, out var clauseDiscriminator)
                        || clauseDiscriminator.ValueKind != JsonValueKind.String
                        || !IsKnownClause(clauseDiscriminator.GetString()))
                    {
                        return $"{nodePath}.clauses[{clauseIndex}]";
                    }
                    clauseIndex++;
                }
            }
            nodeIndex++;
        }
        return serializerPath;
    }

    static bool IsKnownNode(string? discriminator) => discriminator is
        ProcessWireNames.InvokeTransitionNode
        or ProcessWireNames.EvaluateRelationNode
        or ProcessWireNames.RequestNode
        or ProcessWireNames.EmitEventNode
        or ProcessWireNames.SendSignalNode
        or ProcessWireNames.ChoiceNode
        or ProcessWireNames.MatchNode
        or ProcessWireNames.ForkNode
        or ProcessWireNames.JoinNode
        or ProcessWireNames.AwaitMatchNode
        or ProcessWireNames.TimerNode
        or ProcessWireNames.ReplyNode
        or ProcessWireNames.DurableCutNode
        or ProcessWireNames.ReturnNode
        or ProcessWireNames.FailNode;

    static bool IsKnownClause(string? discriminator) => discriminator is
        ProcessWireNames.InteractionAwaitClause
        or ProcessWireNames.TimerAwaitClause;
}
