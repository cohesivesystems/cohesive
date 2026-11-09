using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Configuration;

/// <summary>Selection precedence, independent of permission to publish a contribution.</summary>
public enum SelectionPrecedence
{
    /// <summary>Framework convention.</summary>
    Framework,
    /// <summary>Adapter convention.</summary>
    Adapter,
    /// <summary>Scoped application profile.</summary>
    Profile,
    /// <summary>Explicit local choice, requiring an override grant.</summary>
    Local
}

/// <summary>Exact configuration scope; a user always belongs to a tenant.</summary>
/// <param name="Tenant">Tenant identity; null denotes system scope.</param>
/// <param name="User">User identity within the tenant; null denotes tenant-wide scope.</param>
public sealed record SelectionScope(string? Tenant = null, string? User = null)
{
    internal bool Valid => (Tenant is null || !string.IsNullOrWhiteSpace(Tenant))
        && (User is null || Tenant is not null && !string.IsNullOrWhiteSpace(User));
    internal int Specificity => User is not null ? 2 : Tenant is not null ? 1 : 0;
    internal bool AppliesTo(SelectionScope target) => (Tenant is null || Tenant == target.Tenant)
        && (User is null || User == target.User);
}

/// <summary>Host-authenticated permission to publish at an exact scope. This is not a credential or signature verifier.</summary>
/// <param name="Issuer">Authenticated publisher.</param>
/// <param name="Scope">Exact permitted publication scope.</param>
/// <param name="AllowLocalOverride">Whether explicit local choices are permitted; never permits policy bypass.</param>
public sealed record SelectionGrant(string Issuer, SelectionScope Scope, bool AllowLocalOverride = false);

/// <summary>Canonical contribution; domain values and policies contain data only.</summary>
/// <typeparam name="TChoice">Atomic domain selection.</typeparam>
/// <typeparam name="TPolicy">Domain-owned mandatory constraint.</typeparam>
/// <param name="Issuer">Publisher identity.</param>
/// <param name="Scope">Applicability scope.</param>
/// <param name="Precedence">Default selection priority; does not affect policies.</param>
/// <param name="Choice">Optional complete default or explicit choice.</param>
/// <param name="Policies">Mandatory constraints, all of which must hold.</param>
public sealed record SelectionContribution<TChoice, TPolicy>(string Issuer, SelectionScope Scope,
    SelectionPrecedence Precedence, TChoice? Choice, ImmutableArray<TPolicy> Policies) where TChoice : class where TPolicy : class;

/// <summary>Immutable source for one scoped contribution.</summary>
/// <typeparam name="TChoice">Serializable domain selection.</typeparam>
/// <typeparam name="TPolicy">Serializable domain policy.</typeparam>
public sealed record SelectionLayer<TChoice, TPolicy> where TChoice : class where TPolicy : class
{
    readonly string id;
    readonly string revision;
    SelectionContribution<TChoice, TPolicy> source;
    SelectionLayer(string id, string revision, string issuer, SelectionScope scope, SelectionPrecedence precedence)
    { this.id = id; this.revision = revision; source = new(issuer, scope, precedence, null, []); }

    /// <summary>Declares a layer. Scope and precedence never confer authority.</summary>
    /// <param name="id">Stable identity.</param>
    /// <param name="revision">Exact revision.</param>
    /// <param name="issuer">Publisher.</param>
    /// <param name="scope">Applicable scope.</param>
    /// <param name="precedence">Default priority.</param>
    /// <returns>Immutable refinable source.</returns>
    public static SelectionLayer<TChoice, TPolicy> Define(string id, string revision, string issuer, SelectionScope scope,
        SelectionPrecedence precedence = SelectionPrecedence.Profile) => new(id, revision, issuer, scope, precedence);

    /// <summary>Supplies the atomic choice; local precedence makes it an explicit override.</summary>
    /// <param name="choice">Data-only choice.</param>
    /// <returns>A refined source.</returns>
    public SelectionLayer<TChoice, TPolicy> Prefer(TChoice choice) => this with { source = source with { Choice = Snapshot(choice) } };

    /// <summary>Adds a mandatory constraint, independent of default priority.</summary>
    /// <param name="policy">Data-only constraint interpreted by the consumer.</param>
    /// <returns>A refined source.</returns>
    public SelectionLayer<TChoice, TPolicy> Enforce(TPolicy policy) => this with { source = source with { Policies = source.Policies.Add(Snapshot(policy)) } };

    T Snapshot<T>(T value) where T : class => ScopedSelection<TChoice, TPolicy>
        .Declare(new("configuration.source-snapshot.v1"), id, revision, value).GetDefinition<T>();

    /// <summary>Lowers source into the portable canonical contribution.</summary>
    /// <exception cref="ArgumentException">The contribution is invalid.</exception>
    public ExecutionDefinitionDocument Document
    {
        get
        {
            if (string.IsNullOrWhiteSpace(source.Issuer) || source.Scope is null || !source.Scope.Valid
                || !Enum.IsDefined(source.Precedence) || source.Policies.Any(p => p is null))
                throw new ArgumentException("Invalid scoped selection contribution.");
            return ScopedSelection<TChoice, TPolicy>.Declare(new("configuration.selection-contribution.v1"), id, revision, source);
        }
    }
}

/// <summary>Admitted immutable choice and the complete applicable policy/default lineage.</summary>
/// <typeparam name="TChoice">Domain choice.</typeparam>
/// <param name="Scope">Admission scope.</param>
/// <param name="Winner">Exact selected contribution.</param>
/// <param name="Contributions">Applicable exact revisions; non-winning defaults remain explainable.</param>
/// <param name="Choice">Admitted complete value.</param>
public sealed record ScopedSelectionSnapshot<TChoice>(SelectionScope Scope, ExecutionDefinitionReference Winner,
    ImmutableArray<ExecutionDefinitionReference> Contributions, TChoice Choice) where TChoice : class;

/// <summary>Resolution with a snapshot only when every constraint holds.</summary>
/// <param name="Snapshot">Canonical admission snapshot, or null on failure.</param>
/// <param name="Contributions">Applicable canonical contributions in stable identity order.</param>
/// <param name="Validation">Structured diagnostics.</param>
public sealed record ScopedSelectionResult(ExecutionDefinitionDocument? Snapshot,
    ImmutableArray<ExecutionDefinitionDocument> Contributions, DocumentValidationResult Validation);

/// <summary>Deterministic selection mechanism. Domain policy interpretation executes during resolution and never enters IR.</summary>
/// <typeparam name="TChoice">Serializable atomic choice.</typeparam>
/// <typeparam name="TPolicy">Serializable domain policy.</typeparam>
public static class ScopedSelection<TChoice, TPolicy> where TChoice : class where TPolicy : class
{
    internal static ExecutionDefinitionDocument Declare<T>(ExecutionDefinitionKind kind, string id, string revision, T value) where T : class =>
        ExecutionDefinitionDocument.Create(kind, new(id), new(revision), value,
            new(new("cohesive.configuration.scoped-selection", "1"), new(id), DocumentOrigin.Generated),
            sourceMap: new([new(id + "#/scope", new(["scope"]))]));

    /// <summary>Resolves authorized applicable layers without fallback from a disallowed choice.</summary>
    /// <param name="id">Snapshot identity.</param>
    /// <param name="revision">Snapshot revision.</param>
    /// <param name="scope">Exact requesting scope.</param>
    /// <param name="documents">Published current contribution revisions; registration order is irrelevant.</param>
    /// <param name="grants">Host-authenticated exact-scope publication grants.</param>
    /// <param name="validate">Domain interpreter checking the choice against every accumulated policy.</param>
    /// <returns>A pinned snapshot or structured diagnostics; no services are retained.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static ScopedSelectionResult Resolve(string id, string revision, SelectionScope scope,
        IEnumerable<ExecutionDefinitionDocument> documents, IReadOnlyCollection<SelectionGrant> grants,
        Func<TChoice, ImmutableArray<TPolicy>, DocumentValidationResult> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        var read = Read(scope, documents, grants, out var layers);
        if (!read.IsValid) return new(null, [], read);
        var choices = layers.Where(x => x.Value.Choice is not null)
            .OrderByDescending(x => x.Value.Precedence).ThenByDescending(x => x.Value.Scope.Specificity).ToArray();
        if (choices.Length == 0) return new(null, [.. layers.Select(x => x.Document)], Error("missing", "No applicable choice was supplied."));
        var winner = choices[0];
        if (choices.Skip(1).Any(x => x.Value.Precedence == winner.Value.Precedence && x.Value.Scope.Specificity == winner.Value.Scope.Specificity))
            return new(null, [.. layers.Select(x => x.Document)], Error("ambiguous", "Multiple choices share the winning precedence and scope."));
        var validation = validate(winner.Value.Choice!, [.. layers.SelectMany(x => x.Value.Policies)]);
        var closure = layers.Select(x => x.Document).ToImmutableArray();
        return new(validation.IsValid ? Declare(new("configuration.selection-snapshot.v1"), id, revision,
            new ScopedSelectionSnapshot<TChoice>(scope, winner.Document.Reference, [.. closure.Select(d => d.Reference)], winner.Value.Choice!)) : null,
            closure, validation);
    }

    /// <summary>Checks new effects against current restrictions without reselecting a retained choice.</summary>
    /// <param name="snapshot">Original validated admission snapshot.</param>
    /// <param name="current">Complete current contribution set supplied by the trusted host.</param>
    /// <param name="grants">Current host-authenticated publication permissions.</param>
    /// <param name="validate">Domain constraint interpreter.</param>
    /// <returns>Permission diagnostics for new dispatch. Reconciliation uses the original effect identity and is not a new admission.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static DocumentValidationResult CheckDispatch(ExecutionDefinitionDocument snapshot, IEnumerable<ExecutionDefinitionDocument> current,
        IReadOnlyCollection<SelectionGrant> grants, Func<TChoice, ImmutableArray<TPolicy>, DocumentValidationResult> validate)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(validate);
        var projection = Project<ScopedSelectionSnapshot<TChoice>>(snapshot, "configuration.selection-snapshot.v1", out var admitted);
        if (!projection.IsValid) return projection;
        if (admitted!.Scope is null || admitted.Choice is null) return Error("snapshot", "Invalid admission scope or choice.");
        var read = Read(admitted.Scope, current, grants, out var layers);
        return read.IsValid ? validate(admitted.Choice, [.. layers.SelectMany(x => x.Value.Policies)]) : read;
    }

    /// <summary>Replays an admitted snapshot from exact retained dependencies; this checks integrity, not publisher authentication.</summary>
    /// <param name="snapshot">Pinned snapshot.</param>
    /// <param name="catalog">Retained canonical dependency closure.</param>
    /// <param name="validate">Domain policy interpreter.</param>
    /// <returns>Structural and semantic diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static DocumentValidationResult Inspect(ExecutionDefinitionDocument snapshot, ExecutionDefinitionDocumentCatalog catalog,
        Func<TChoice, ImmutableArray<TPolicy>, DocumentValidationResult> validate)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(validate);
        var projection = Project<ScopedSelectionSnapshot<TChoice>>(snapshot, "configuration.selection-snapshot.v1", out var admitted);
        if (!projection.IsValid) return projection;
        if (admitted!.Choice is null || admitted.Scope is null || admitted.Contributions.IsDefaultOrEmpty)
            return Error("snapshot", "Incomplete admission snapshot.");
        List<ExecutionDefinitionDocument> documents = [];
        List<SelectionGrant> grants = [];
        foreach (var reference in admitted.Contributions)
        {
            var linked = catalog.ValidateReference(reference, "/configuration/contributions", out var document);
            if (!linked.IsValid) return linked;
            projection = Project<SelectionContribution<TChoice, TPolicy>>(document!, "configuration.selection-contribution.v1", out var layer);
            if (!projection.IsValid) return projection;
            documents.Add(document!); grants.Add(new(layer!.Issuer, layer.Scope, AllowLocalOverride: true));
        }
        var replay = Resolve(snapshot.Metadata.DefinitionId.Value, snapshot.Metadata.RevisionId.Value, admitted.Scope, documents, grants, validate);
        return !replay.Validation.IsValid ? replay.Validation : replay.Snapshot!.Reference == snapshot.Reference
            ? DocumentValidationResult.Valid : Error("snapshot", "Retained lineage does not reproduce the admitted snapshot.");
    }

    static DocumentValidationResult Project<T>(ExecutionDefinitionDocument document, string kind, out T? value) where T : class
    {
        value = null;
        ArgumentNullException.ThrowIfNull(document);
        if (!document.Extensions.IsDefaultOrEmpty) return Error("extension", "Unsupported configuration extension.");
        var projection = new ExecutionDefinitionDocumentProjection<T>(new(kind), "configuration.selection.kind",
            "configuration.selection.projection", "configuration.selection.wire", "Use canonical scoped-selection source.");
        return projection.ValidateAndProject(ExecutionDefinitionDocumentValidator.Validate(document), document,
            _ => DocumentValidationResult.Valid, out value);
    }

    static DocumentValidationResult Read(SelectionScope scope, IEnumerable<ExecutionDefinitionDocument> documents,
        IReadOnlyCollection<SelectionGrant> grants,
        out List<(ExecutionDefinitionDocument Document, SelectionContribution<TChoice, TPolicy> Value)> layers)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(documents); ArgumentNullException.ThrowIfNull(grants);
        layers = [];
        if (!scope.Valid) return Error("scope", "Invalid tenant/user scope.");
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (var document in documents.OrderBy(d => d.Metadata.DefinitionId.Value, StringComparer.Ordinal))
        {
            if (!identities.Add(document.Metadata.DefinitionId.Value)) return Error("revision", "Supply one current revision per contribution identity.");
            if (document.Kind.Value != "configuration.selection-contribution.v1" || !document.Extensions.IsDefaultOrEmpty
                || !ExecutionDefinitionDocumentValidator.Validate(document).IsValid) return Error("document", "Invalid contribution document.");
            var projection = Project<SelectionContribution<TChoice, TPolicy>>(document, "configuration.selection-contribution.v1", out var value);
            if (!projection.IsValid) return projection;
            if (string.IsNullOrWhiteSpace(value!.Issuer) || value.Scope is null || !value.Scope.Valid || !Enum.IsDefined(value.Precedence) || value.Policies.IsDefault || value.Policies.Any(p => p is null))
                return Error("document", "Invalid contribution semantics.");
            if (!value.Scope.AppliesTo(scope)) continue;
            if (!grants.Any(g => g.Issuer == value.Issuer && g.Scope == value.Scope && (value.Precedence != SelectionPrecedence.Local || g.AllowLocalOverride)))
                return Error("unauthorized", "Publisher lacks the required exact-scope publication or override grant.");
            layers.Add((document, value));
        }
        return DocumentValidationResult.Valid;
    }

    static DocumentValidationResult Error(string code, string message) => new([new("configuration.selection." + code,
        DiagnosticSeverity.Error, message, "/configuration")]);
}
