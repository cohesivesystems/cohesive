using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Stable identity, revision, provenance, and descriptive metadata supplied by a C# Transition producer.
/// </summary>
public sealed record TransitionAuthoringMetadata
{
    /// <summary>Creates metadata for one canonical Transition revision.</summary>
    /// <param name="definitionId">Stable identity shared by every revision of the Transition.</param>
    /// <param name="revisionId">Stable identity of the semantic revision being authored.</param>
    /// <param name="bodyId">Stable identity of the root structured body.</param>
    /// <param name="provenance">Producer and root-source attribution for the authored definition.</param>
    /// <param name="displayName">Optional human-facing name excluded from semantic fingerprinting.</param>
    /// <param name="description">Optional human-facing description excluded from semantic fingerprinting.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="definitionId"/>, <paramref name="revisionId"/>, or <paramref name="bodyId"/> is default.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="provenance"/> is <see langword="null"/>.</exception>
    public TransitionAuthoringMetadata(
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        ExecutionNodeId bodyId,
        ExecutionProvenance provenance,
        string? displayName = null,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(definitionId.Value))
            throw new ArgumentException("Canonical Transition authoring requires a definition identity.", nameof(definitionId));
        if (string.IsNullOrWhiteSpace(revisionId.Value))
            throw new ArgumentException("Canonical Transition authoring requires a revision identity.", nameof(revisionId));
        if (string.IsNullOrWhiteSpace(bodyId.Value))
            throw new ArgumentException("Canonical Transition authoring requires a root-body identity.", nameof(bodyId));

        DefinitionId = definitionId;
        RevisionId = revisionId;
        BodyId = bodyId;
        Provenance = Guard.RequireNotNull(provenance);
        DisplayName = displayName.TrimmedEmptyOrWhiteSpaceAs();
        Description = description.TrimmedEmptyOrWhiteSpaceAs();
    }

    /// <summary>Stable identity shared by every revision of the Transition.</summary>
    public ExecutionDefinitionId DefinitionId { get; }

    /// <summary>Stable identity of this semantic revision.</summary>
    public ExecutionRevisionId RevisionId { get; }

    /// <summary>Stable identity of the root structured body.</summary>
    public ExecutionNodeId BodyId { get; }

    /// <summary>Producer and root-source attribution.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Optional human-facing name excluded from semantic fingerprinting.</summary>
    public string? DisplayName { get; }

    /// <summary>Optional human-facing description excluded from semantic fingerprinting.</summary>
    public string? Description { get; }
}

/// <summary>Deterministic identities derived for structural bodies introduced by C# authoring.</summary>
public static class TransitionAuthoringIdentities
{
    /// <summary>Derives the body-sequence identity owned by a branch, case, or fallback identity.</summary>
    /// <param name="owner">Stable identity of the construct that owns the nested body.</param>
    /// <returns>A deterministic body identity independent of source location and call order.</returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is default.</exception>
    public static ExecutionNodeId BodyFor(ExecutionNodeId owner)
    {
        if (string.IsNullOrWhiteSpace(owner.Value))
            throw new ArgumentException("A nested Transition body requires an owning identity.", nameof(owner));
        return new($"{owner.Value}/body");
    }
}

/// <summary>
/// Typed C# handle for one canonical Transition execution-definition document.
/// </summary>
/// <remarks>
/// This handle contains no executable callback, legacy flat definition, runtime service, or entity state. The
/// persisted <see cref="Document"/> is the semantic authority; <see cref="Definition"/> and compiled plans are
/// projections interpreted from that document.
/// </remarks>
/// <typeparam name="TEntity">CLR entity authoring type whose fields supplied the observation selectors.</typeparam>
/// <typeparam name="TInput">CLR type projected into the portable invocation-input contract.</typeparam>
/// <typeparam name="TOutcome">CLR type projected into the portable outcome contract.</typeparam>
public sealed class Transition<TEntity, TInput, TOutcome>
    where TEntity : notnull
{
    internal Transition(
        ExecutionDefinitionDocument document,
        DocumentValidationResult validation)
    {
        Document = Guard.RequireNotNull(document);
        Validation = Guard.RequireNotNull(validation);
    }

    /// <summary>Canonical persisted execution-definition document and sole durable semantic authority.</summary>
    public ExecutionDefinitionDocument Document { get; }

    /// <summary>Canonical validation diagnostics enriched with producer source references.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether canonical document and Transition-specific validation found no errors.</summary>
    public bool IsValid => Validation.IsValid;

    /// <summary>Typed projection of the canonical definition payload.</summary>
    /// <returns>The independently deserialized canonical Transition definition.</returns>
    /// <exception cref="System.Text.Json.JsonException">The canonical payload cannot be projected as Transition IR.</exception>
    /// <exception cref="NotSupportedException">The strict execution serializer does not support a payload value.</exception>
    public IR.TransitionDefinition Definition => Document.GetDefinition<IR.TransitionDefinition>();

    /// <summary>Exact identity, revision, and fingerprint reference to this canonical definition.</summary>
    public ExecutionDefinitionReference Reference => new(
        Document.Metadata.DefinitionId,
        Document.Metadata.RevisionId,
        Document.Metadata.Fingerprint);

    /// <summary>Compiles the canonical document without an external shape graph.</summary>
    /// <returns>A complete plan when valid and supported, or structured validation and compilation diagnostics.</returns>
    /// <exception cref="InvalidOperationException">Canonical semantic content has no stable JSON representation.</exception>
    /// <exception cref="NotSupportedException">Canonical semantic content contains an unsupported runtime value.</exception>
    public TransitionCompilationResult Compile() => TransitionStaticCompiler.Compile(Document);

    /// <summary>Compiles the canonical document using an exact shape graph.</summary>
    /// <param name="graph">Shape graph resolving qualified input, observation, and outcome contracts.</param>
    /// <returns>A complete plan when valid and supported, or structured validation and compilation diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Canonical semantic content has no stable JSON representation.</exception>
    /// <exception cref="NotSupportedException">Canonical semantic content contains an unsupported runtime value.</exception>
    public TransitionCompilationResult Compile(ShapeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return TransitionStaticCompiler.Compile(Document, graph);
    }
}

/// <summary>Produces canonical Transition IR and execution documents from restricted typed C# syntax.</summary>
public static class TransitionAuthoring
{
    /// <summary>Stable producer identity for the canonical C# Transition frontend.</summary>
    public const string Producer = "cohesive.transitions.csharp/v1";

    /// <summary>Authors one canonical Transition from an existing entity observation shape.</summary>
    /// <typeparam name="TEntity">Entity type used by typed observation-field selectors.</typeparam>
    /// <typeparam name="TInput">Typed invocation input.</typeparam>
    /// <typeparam name="TOutcome">Typed Transition outcome.</typeparam>
    /// <param name="entityShape">Canonical aggregate observation shape.</param>
    /// <param name="metadata">Stable identity, revision, root-body identity, and provenance.</param>
    /// <param name="configure">Finite builder callback that produces canonical IR data and is not retained.</param>
    /// <param name="sourceFile">Compiler-supplied source file for non-semantic source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line for non-semantic source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member for non-semantic source attribution.</param>
    /// <param name="typeRefMapper">Optional resolved CLR value contracts used consistently for input, locals, and outcomes.</param>
    /// <param name="memberPathResolver">Optional state-member mapping into the supplied inline observation contract. Nested named types require an explicit whole-value update.</param>
    /// <returns>A typed handle containing only the canonical document and its validation result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entityShape"/>, <paramref name="metadata"/>, or <paramref name="configure"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="entityShape"/> is not an entity shape or has no fields, or authored metadata is invalid.
    /// </exception>
    /// <exception cref="TransitionExpressionTranslationException">
    /// A supplied C# expression contains captured state, an unsupported node, arbitrary method call, or another
    /// construct outside the portable Transition expression language.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Builder structure is contradictory or canonical content has no stable representation.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// An authored constant or canonical payload value cannot be represented portably.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Authored canonical content cannot be encoded by the strict execution serializer.
    /// </exception>
    public static Transition<TEntity, TInput, TOutcome> Create<TEntity, TInput, TOutcome>(
        Shape entityShape,
        TransitionAuthoringMetadata metadata,
        Action<TransitionBuilder<TEntity, TInput, TOutcome>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "",
        IClrTypeRefMapper? typeRefMapper = null,
        ClrMemberPathResolver? memberPathResolver = null)
        where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(entityShape);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(configure);
        if (!entityShape.HasRole(ShapeRoles.Entity))
            throw new ArgumentException("Canonical Transition authoring requires an entity observation shape.", nameof(entityShape));
        if (entityShape.Fields.IsDefaultOrEmpty)
            throw new ArgumentException("Canonical Transition authoring requires at least one observed entity field.", nameof(entityShape));

        var context = new TransitionAuthoringContext<TEntity, TInput, TOutcome>(
            entityShape,
            metadata,
            typeRefMapper ?? new DefaultClrTypeRefMapper(),
            memberPathResolver);
        var rootSource = context.Source(sourceFile, sourceLine, sourceMember, "Transition root body");
        var builder = new TransitionBuilder<TEntity, TInput, TOutcome>(context);
        configure(builder);
        var definition = builder.Build(metadata.BodyId, rootSource);
        var sourceMap = context.BuildSourceMap(definition);

        var initial = TransitionDefinitionDocuments.Create(
            metadata.DefinitionId,
            metadata.RevisionId,
            definition,
            metadata.Provenance,
            displayName: metadata.DisplayName,
            description: metadata.Description,
            sourceMap: sourceMap);
        var validation = TransitionDefinitionDocuments.ValidateAuthored(initial, definition);
        var document = initial.WithRetainedDiagnostics(validation.Diagnostics);
        return new(document, validation);
    }
}
