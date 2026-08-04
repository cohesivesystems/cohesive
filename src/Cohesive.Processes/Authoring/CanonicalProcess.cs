using System.Globalization;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Processes.Authoring;

/// <summary>
/// Stable identity, revision, entry, recovery, provenance, and descriptive metadata supplied by a C# Process
/// producer.
/// </summary>
public sealed record ProcessAuthoringMetadata
{
    /// <summary>Creates metadata for one canonical Process revision.</summary>
    /// <param name="definitionId">Stable identity shared by every revision of the Process.</param>
    /// <param name="revisionId">Stable identity of the semantic revision being authored.</param>
    /// <param name="entryId">Stable identity of the first Process node.</param>
    /// <param name="recoveryPolicy">Explicit recovery behavior after a recoverable interruption.</param>
    /// <param name="provenance">Producer and root-source attribution for the authored definition.</param>
    /// <param name="displayName">Optional human-facing name excluded from semantic fingerprinting.</param>
    /// <param name="description">Optional human-facing description excluded from semantic fingerprinting.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="definitionId"/>, <paramref name="revisionId"/>, or <paramref name="entryId"/> is default.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="provenance"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="recoveryPolicy"/> is unspecified or is not a supported recovery policy.
    /// </exception>
    public ProcessAuthoringMetadata(
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        ExecutionNodeId entryId,
        ProcessRecoveryPolicy recoveryPolicy,
        ExecutionProvenance provenance,
        string? displayName = null,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(definitionId.Value))
            throw new ArgumentException("Canonical Process authoring requires a definition identity.", nameof(definitionId));
        if (string.IsNullOrWhiteSpace(revisionId.Value))
            throw new ArgumentException("Canonical Process authoring requires a revision identity.", nameof(revisionId));
        if (string.IsNullOrWhiteSpace(entryId.Value))
            throw new ArgumentException("Canonical Process authoring requires an entry-node identity.", nameof(entryId));
        if (!Enum.IsDefined(recoveryPolicy) || recoveryPolicy == ProcessRecoveryPolicy.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoveryPolicy),
                recoveryPolicy,
                "Canonical Process authoring requires an explicit recovery policy.");
        }

        DefinitionId = definitionId;
        RevisionId = revisionId;
        EntryId = entryId;
        RecoveryPolicy = recoveryPolicy;
        Provenance = Guard.RequireNotNull(provenance);
        DisplayName = displayName.TrimmedEmptyOrWhiteSpaceAs();
        Description = description.TrimmedEmptyOrWhiteSpaceAs();
    }

    /// <summary>Stable identity shared by every revision of the Process.</summary>
    public ExecutionDefinitionId DefinitionId { get; }

    /// <summary>Stable identity of this semantic revision.</summary>
    public ExecutionRevisionId RevisionId { get; }

    /// <summary>Stable identity of the first Process node.</summary>
    public ExecutionNodeId EntryId { get; }

    /// <summary>Explicit recovery behavior after a recoverable interruption.</summary>
    public ProcessRecoveryPolicy RecoveryPolicy { get; }

    /// <summary>Producer and root-source attribution.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Optional human-facing name excluded from semantic fingerprinting.</summary>
    public string? DisplayName { get; }

    /// <summary>Optional human-facing description excluded from semantic fingerprinting.</summary>
    public string? Description { get; }
}

/// <summary>Versioned deterministic identities for structural constructs produced by Process authoring frontends.</summary>
/// <remarks>
/// These conventions materialize explicit canonical identities before Process IR is persisted. Structural paths,
/// semantic roles, and ordinals are producer inputs to the convention; source location, builder call order, and
/// runtime state are deliberately excluded. Frontends may always supply explicit identities instead.
/// </remarks>
public static class ProcessAuthoringIdentities
{
    /// <summary>Stable authority and version of the deterministic Process identity convention.</summary>
    public const string ConventionAuthority = "cohesive.processes.authoring.identities/v1";

    /// <summary>Derives a node identity from its stable structural path in an authoring model.</summary>
    /// <param name="structuralPath">
    /// Producer-neutral structural path whose segments identify the node independently of source location and
    /// construction call order.
    /// </param>
    /// <returns>An explicit canonical node identity containing the convention version and escaped structural path.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="structuralPath"/> is a default, uninitialized path.
    /// </exception>
    public static ExecutionNodeId NodeFor(ExecutionSemanticPath structuralPath) =>
        new($"{ConventionAuthority}{RequirePath(structuralPath, nameof(structuralPath))}/node");

    /// <summary>Derives a node identity from an owning node and semantic role.</summary>
    /// <param name="owner">Stable owning node identity.</param>
    /// <param name="role">Stable semantic role local to <paramref name="owner"/>.</param>
    /// <returns>A node identity independent of source location, call order, and runtime state.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> is default, or <paramref name="role"/> is blank or contains a path separator.
    /// </exception>
    public static ExecutionNodeId NodeFor(ExecutionNodeId owner, string role) =>
        new($"{RequireOwner(owner)}/{RequireRole(role)}/node");

    /// <summary>Derives an ordinal node identity from an owning node and semantic role.</summary>
    /// <param name="owner">Stable owning node identity.</param>
    /// <param name="role">Stable semantic role local to <paramref name="owner"/>.</param>
    /// <param name="ordinal">Zero-based ordinal within the owned semantic role.</param>
    /// <returns>An ordinal node identity independent of source location, call order, culture, and runtime state.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> is default, or <paramref name="role"/> is blank or contains a path separator.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is negative.</exception>
    public static ExecutionNodeId NodeFor(ExecutionNodeId owner, string role, int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        return new(
            $"{RequireOwner(owner)}/{RequireRole(role)}/{ordinal.ToString(CultureInfo.InvariantCulture)}/node");
    }

    /// <summary>Creates portable evidence that a structural path supplied an identity by convention.</summary>
    /// <param name="structuralPath">Stable structural path consumed by the identity convention.</param>
    /// <param name="semanticPath">Path of the corresponding construct in canonical Process IR.</param>
    /// <returns>
    /// Non-semantic source provenance carrying the convention authority, structural input, and canonical IR path.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="structuralPath"/> or <paramref name="semanticPath"/> is a default, uninitialized path.
    /// </exception>
    public static ExecutionSourceProvenance ConventionSourceFor(
        ExecutionSemanticPath structuralPath,
        ExecutionSemanticPath semanticPath) =>
        new(
            $"{ConventionAuthority}#{RequirePath(structuralPath, nameof(structuralPath))}",
            RequirePathValue(semanticPath, nameof(semanticPath)),
            "Identity supplied by the deterministic Process authoring convention.");

    /// <summary>Derives a control-flow edge identity from an owning node and semantic role.</summary>
    /// <param name="owner">Stable owning node identity.</param>
    /// <param name="role">Stable semantic role local to <paramref name="owner"/>.</param>
    /// <returns>An edge identity independent of source location, call order, and runtime state.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> is default, or <paramref name="role"/> is blank or contains a path separator.
    /// </exception>
    public static ProcessEdgeId EdgeFor(ExecutionNodeId owner, string role) =>
        new($"{RequireOwner(owner)}/{RequireRole(role)}/edge");

    /// <summary>Derives a value-binding identity from an owning node and semantic role.</summary>
    /// <param name="owner">Stable owning node identity.</param>
    /// <param name="role">Stable semantic role local to <paramref name="owner"/>.</param>
    /// <returns>A binding identity independent of source location, call order, and runtime state.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> is default, or <paramref name="role"/> is blank or contains a path separator.
    /// </exception>
    public static ValueBindingId BindingFor(ExecutionNodeId owner, string role) =>
        new($"{RequireOwner(owner)}/{RequireRole(role)}/binding");

    /// <summary>Derives a Request-obligation binding identity from an owning node and semantic role.</summary>
    /// <param name="owner">Stable owning node identity.</param>
    /// <param name="role">Stable semantic role local to <paramref name="owner"/>.</param>
    /// <returns>An obligation identity independent of source location, call order, and runtime state.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> is default, or <paramref name="role"/> is blank or contains a path separator.
    /// </exception>
    public static RequestObligationBindingId RequestObligationFor(ExecutionNodeId owner, string role) =>
        new($"{RequireOwner(owner)}/{RequireRole(role)}/request-obligation");

    static string RequireOwner(ExecutionNodeId owner)
    {
        if (string.IsNullOrWhiteSpace(owner.Value))
            throw new ArgumentException("A derived Process identity requires an owning node identity.", nameof(owner));
        return owner.Value;
    }

    static string RequireRole(string role)
    {
        role = Guard.RequireNotNullOrWhiteSpace(role).Trim();
        if (role.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A derived Process identity role must be one canonical path segment and cannot contain '/'.",
                nameof(role));
        }
        return role;
    }

    static string RequirePath(ExecutionSemanticPath path, string parameterName)
    {
        if (path.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A derived Process identity requires a structural path.", parameterName);
        return path.ToString();
    }

    static ExecutionSemanticPath RequirePathValue(ExecutionSemanticPath path, string parameterName)
    {
        RequirePath(path, parameterName);
        return path;
    }
}

/// <summary>Typed C# handle for one canonical Process execution-definition document.</summary>
/// <remarks>
/// This handle contains no executable callback, legacy Process definition, runtime service, or suspended
/// host-language frame. <see cref="Document"/> is the sole durable semantic authority; <see cref="Definition"/>
/// and compiled plans are projections interpreted from that document.
/// </remarks>
/// <typeparam name="TInput">CLR type projected into the portable Process invocation-input contract.</typeparam>
/// <typeparam name="TResult">CLR type projected into the portable terminal-result contract.</typeparam>
public sealed class Process<TInput, TResult>
{
    internal Process(ExecutionDefinitionDocument document, DocumentValidationResult validation)
    {
        Document = Guard.RequireNotNull(document);
        Validation = Guard.RequireNotNull(validation);
    }

    /// <summary>Canonical persisted execution-definition document and sole durable semantic authority.</summary>
    public ExecutionDefinitionDocument Document { get; }

    /// <summary>Canonical validation diagnostics enriched with producer source references.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether context-free document and Process validation found no errors.</summary>
    public bool IsValid => Validation.IsValid;

    /// <summary>Typed projection of the canonical Process definition payload.</summary>
    /// <returns>The independently deserialized canonical Process definition.</returns>
    /// <exception cref="System.Text.Json.JsonException">The canonical payload cannot be projected as Process IR.</exception>
    /// <exception cref="NotSupportedException">The strict execution serializer does not support a payload value.</exception>
    public CanonicalProcessDefinition Definition => Document.GetDefinition<CanonicalProcessDefinition>();

    /// <summary>Exact identity, revision, and fingerprint reference to this canonical definition.</summary>
    public ExecutionDefinitionReference Reference => new(
        Document.Metadata.DefinitionId,
        Document.Metadata.RevisionId,
        Document.Metadata.Fingerprint);

    /// <summary>Compiles the canonical document using exact linked-definition and interaction evidence.</summary>
    /// <param name="context">Exact linked definitions, interaction contracts, and optional shape evidence.</param>
    /// <returns>A complete plan when valid and supported, or structured validation and compilation diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Canonical semantic content has no stable JSON representation.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical content cannot be decoded using the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">Canonical semantic content contains an unsupported runtime value.</exception>
    public ProcessCompilationResult Compile(ProcessDefinitionValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ProcessStaticCompiler.Compile(Document, context);
    }
}

/// <summary>Produces canonical Process IR and execution documents from finite typed C# construction.</summary>
public static class ProcessAuthoring
{
    /// <summary>Stable producer identity for the canonical C# Process frontend.</summary>
    public const string Producer = "cohesive.processes.csharp/v1";

    /// <summary>Authors one canonical Process document.</summary>
    /// <remarks>
    /// CLR nullable-reference annotations are not reified in generic <see cref="Type"/> values. Use the explicit
    /// occurrence-contract overload when top-level input or result reference nullability is semantic.
    /// </remarks>
    /// <typeparam name="TInput">Typed invocation input.</typeparam>
    /// <typeparam name="TResult">Typed terminal result shared by successful and failed outcomes.</typeparam>
    /// <param name="metadata">Stable identity, revision, entry, recovery policy, and provenance.</param>
    /// <param name="configure">Finite builder callback that produces canonical IR data and is not retained.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed handle containing only the canonical document and its context-free validation result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="metadata"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Authored metadata, identity, selector, or canonical construct is invalid, or <paramref name="configure"/>
    /// is asynchronous.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Builder ownership or graph construction is contradictory, or canonical content has no stable representation.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// An authored constant, CLR contract, or canonical payload cannot be represented portably.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Authored canonical content cannot be encoded by the strict execution serializer.
    /// </exception>
    public static Process<TInput, TResult> Create<TInput, TResult>(
        ProcessAuthoringMetadata metadata,
        Action<ProcessBuilder<TInput, TResult>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        CreateCore<TInput, TResult>(
            metadata,
            inputContract: null,
            resultContract: null,
            configure,
            sourceFile,
            sourceLine,
            sourceMember);

    /// <summary>Authors one canonical Process document using explicit top-level occurrence contracts.</summary>
    /// <typeparam name="TInput">Typed invocation input represented by <paramref name="inputContract"/>.</typeparam>
    /// <typeparam name="TResult">Typed terminal result represented by <paramref name="resultContract"/>.</typeparam>
    /// <param name="metadata">Stable identity, revision, entry, recovery policy, and provenance.</param>
    /// <param name="inputContract">
    /// Exact portable input contract, including occurrence presence and nullability.
    /// </param>
    /// <param name="resultContract">
    /// Exact portable result contract, including occurrence presence and nullability.
    /// </param>
    /// <param name="configure">Finite builder callback that produces canonical IR data and is not retained.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed handle containing only the canonical document and its context-free validation result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="metadata"/>, <paramref name="inputContract"/>, <paramref name="resultContract"/>, or
    /// <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Authored metadata, identity, selector, or canonical construct is invalid, or <paramref name="configure"/>
    /// is asynchronous.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Builder ownership or graph construction is contradictory, or canonical content has no stable representation.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// An authored constant, CLR contract, or canonical payload cannot be represented portably.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Authored canonical content cannot be encoded by the strict execution serializer.
    /// </exception>
    public static Process<TInput, TResult> Create<TInput, TResult>(
        ProcessAuthoringMetadata metadata,
        ValueContract inputContract,
        ValueContract resultContract,
        Action<ProcessBuilder<TInput, TResult>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        CreateCore(
            metadata,
            Guard.RequireNotNull(inputContract),
            Guard.RequireNotNull(resultContract),
            configure,
            sourceFile,
            sourceLine,
            sourceMember);

    static Process<TInput, TResult> CreateCore<TInput, TResult>(
        ProcessAuthoringMetadata metadata,
        ValueContract? inputContract,
        ValueContract? resultContract,
        Action<ProcessBuilder<TInput, TResult>> configure,
        string sourceFile,
        int sourceLine,
        string sourceMember)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(configure);
        if (configure.GetInvocationList().Any(static callback =>
                callback.Method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false)))
        {
            throw new ArgumentException(
                "Canonical Process authoring callbacks must complete synchronously and cannot be async.",
                nameof(configure));
        }

        var context = new ProcessAuthoringContext(
            metadata,
            new DefaultClrTypeRefMapper(),
            typeof(TInput),
            typeof(TResult),
            inputContract,
            resultContract);
        var rootSource = context.Source(sourceFile, sourceLine, sourceMember, "Process definition");
        var builder = new ProcessBuilder<TInput, TResult>(context, rootSource);
        configure(builder);
        var definition = builder.Build();
        var sourceMap = context.BuildSourceMap(definition, rootSource);

        var initial = ProcessDefinitionDocuments.Create(
            metadata.DefinitionId,
            metadata.RevisionId,
            definition,
            metadata.Provenance,
            displayName: metadata.DisplayName,
            description: metadata.Description,
            sourceMap: sourceMap);
        var validation = ProcessDefinitionDocuments.Validate(initial);
        var document = ProcessDefinitionDocuments.Create(
            metadata.DefinitionId,
            metadata.RevisionId,
            definition,
            metadata.Provenance,
            displayName: metadata.DisplayName,
            description: metadata.Description,
            sourceMap: sourceMap,
            diagnostics: validation.Diagnostics);
        return new(document, validation);
    }
}
