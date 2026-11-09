using System.Collections.Immutable;
using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Integrations.Authoring;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Integrations;

/// <summary>Qualification of an exact requirement at admission, distinct from invocation evidence.</summary>
public enum IntegrationQualificationStatus
{
    /// <summary>The interpreted requirement is established by the admitted evidence.</summary>
    Supported,
    /// <summary>The operation may acquire evidence, but this requirement remains pending at its declared runtime boundary.</summary>
    RequiresRuntimeEvidence,
    /// <summary>No permitted realization satisfies the requirement.</summary>
    Unsupported
}

/// <summary>One domain-interpreted decision about an existing Contract property.</summary>
/// <param name="Property">Exact required assertion; its scope, parameters and assumptions remain authoritative.</param>
/// <param name="Status">Admission decision.</param>
/// <param name="Realization">Shared capability-realization classification.</param>
/// <param name="Code">Stable domain reason code.</param>
/// <param name="Explanation">Human-readable reason, including pending evidence where applicable.</param>
public sealed record IntegrationRequirementDecision(ExecutionDefinitionReference Property, IntegrationQualificationStatus Status,
    CapabilityRealizationKind Realization, string Code, string Explanation);

/// <summary>Resource-specific capability offer supported by bounded, attributable evidence.</summary>
/// <param name="Profile">Exact operation profile.</param>
/// <param name="Connection">Exact configured access; never a provider-wide wildcard.</param>
/// <param name="Schema">Portable capability interpretation identity.</param>
/// <param name="Capabilities">Exact domain-owned capability declaration.</param>
/// <param name="Evidence">Exact evidence supporting this declaration; not invocation coverage evidence.</param>
/// <param name="ValidFromUtc">Inclusive beginning of evidence validity.</param>
/// <param name="ValidUntilUtc">Exclusive end of evidence validity.</param>
public sealed record IntegrationCapabilityOfferDefinition(ExecutionDefinitionReference Profile, ExecutionDefinitionReference Connection,
    string Schema, ExecutionDefinitionReference Capabilities, ExecutionDefinitionReference Evidence,
    DateTimeOffset ValidFromUtc, DateTimeOffset ValidUntilUtc);

/// <summary>Immutable authoring projection and lowered dependencies of a resource-specific offer.</summary>
public sealed class IntegrationCapabilityOffer
{
    /// <summary>Canonical offer.</summary>
    public ExecutionDefinitionDocument Document { get; }
    /// <summary>Canonical capability data and supporting evidence.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> Dependencies { get; }
    IntegrationCapabilityOffer(ExecutionDefinitionDocument document, ImmutableArray<ExecutionDefinitionDocument> dependencies)
    { Document = document; Dependencies = dependencies; }

    /// <summary>Lowers a data-only capability declaration, pinning the actual profile/connection and evidence.</summary>
    /// <typeparam name="T">Domain-owned capability data.</typeparam>
    /// <param name="id">Stable offer identity.</param>
    /// <param name="revision">Offer revision.</param>
    /// <param name="profile">Operation source.</param>
    /// <param name="connection">Configured resource access.</param>
    /// <param name="schema">Portable capability schema understood by the evaluator.</param>
    /// <param name="capabilities">Domain data snapshotted during lowering.</param>
    /// <param name="evidence">Already lowered evidence from its owning source.</param>
    /// <param name="validFromUtc">Inclusive UTC validity start.</param>
    /// <param name="validUntilUtc">Exclusive UTC expiry.</param>
    /// <returns>Canonical offer and dependencies; no live clients or secrets.</returns>
    /// <exception cref="ArgumentException">The validity window, schema or source declarations are invalid.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static IntegrationCapabilityOffer Declare<T>(string id, string revision, ProviderOperationProfile profile,
        IntegrationConnection connection, string schema, T capabilities, ExecutionDefinitionDocument evidence,
        DateTimeOffset validFromUtc, DateTimeOffset validUntilUtc) where T : class
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(capabilities); ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (validFromUtc.Offset != TimeSpan.Zero || validUntilUtc.Offset != TimeSpan.Zero || validUntilUtc <= validFromUtc)
            throw new ArgumentException("Declare a positive UTC evidence-validity interval.");
        var data = IntegrationQualification.Declare("integration.capability-data.v1", id + "/capabilities", revision, capabilities);
        var definition = new IntegrationCapabilityOfferDefinition(profile.Document.Reference, connection.Document.Reference, schema,
            data.Reference, evidence.Reference, validFromUtc, validUntilUtc);
        return new(IntegrationQualification.Declare("integration.capability-offer.v1", id, revision, definition), [data, evidence]);
    }
}

/// <summary>Portable identity of a trusted domain interpretation. Implementation remains outside IR.</summary>
/// <param name="Schema">Portable capability schema understood by this evaluator revision.</param>
public sealed record IntegrationEvaluatorDefinition(string Schema);

/// <summary>Host-side domain interpreter paired with its exact portable semantic revision.</summary>
/// <typeparam name="T">Domain capability data.</typeparam>
public sealed class IntegrationCapabilityEvaluator<T> where T : class
{
    /// <summary>Exact evaluator identity and schema declaration.</summary>
    public ExecutionDefinitionDocument Definition { get; }
    internal Func<IngestionProviderBindingInspection, T, IntegrationCapabilityOfferDefinition, DateTimeOffset, ExecutionDefinitionDocumentCatalog, ImmutableArray<IntegrationRequirementDecision>> Evaluate { get; }
    /// <summary>Registers the domain interpretation used during admission.</summary>
    /// <param name="id">Stable interpreter identity.</param>
    /// <param name="revision">Semantic implementation revision; change it when evaluation meaning changes.</param>
    /// <param name="schema">Portable supported capability schema.</param>
    /// <param name="evaluate">Trusted interpreter; must return exactly one decision per required property.</param>
    /// <exception cref="ArgumentNullException">Interpreter is null.</exception>
    /// <exception cref="ArgumentException">An identity or schema is blank.</exception>
    public IntegrationCapabilityEvaluator(string id, string revision, string schema,
        Func<IngestionProviderBindingInspection, T, IntegrationCapabilityOfferDefinition, DateTimeOffset, ExecutionDefinitionDocumentCatalog, ImmutableArray<IntegrationRequirementDecision>> evaluate)
    {
        ArgumentNullException.ThrowIfNull(evaluate); ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        Definition = IntegrationQualification.Declare("integration.capability-evaluator.v1", id, revision, new IntegrationEvaluatorDefinition(schema));
        Evaluate = evaluate;
    }
}

/// <summary>Canonical admission plan. Pending properties still require evidence at their original Contract boundaries.</summary>
/// <param name="Binding">Exact original binding including configuration and independent consumer.</param>
/// <param name="Process">Exact existing Process realization.</param>
/// <param name="Offer">Resource-specific offer and its evidence window.</param>
/// <param name="Evaluator">Exact domain interpretation revision.</param>
/// <param name="AssessedAtUtc">Explicit assessment time, never an ambient clock.</param>
/// <param name="Decisions">One decision per required Contract property in contract order.</param>
public sealed record IntegrationQualificationPlan(ExecutionDefinitionReference Binding, ExecutionDefinitionReference Process,
    ExecutionDefinitionReference Offer, ExecutionDefinitionReference Evaluator, DateTimeOffset AssessedAtUtc,
    ImmutableArray<IntegrationRequirementDecision> Decisions);

/// <summary>Per-candidate assessment, retaining rejected candidates for explanation.</summary>
/// <param name="Candidate">Original compiled candidate.</param>
/// <param name="Offer">Assessed offer.</param>
/// <param name="Compilation">Compilation carrying a qualified admission plan, or null on failure.</param>
/// <param name="Validation">Structured qualification diagnostics.</param>
/// <param name="Decisions">Domain decisions, including rejected requirements; empty when structural checks fail first.</param>
public sealed record IntegrationQualificationResult(IntegrationCompilation Candidate, ExecutionDefinitionDocument Offer,
    IntegrationCompilation? Compilation, DocumentValidationResult Validation, ImmutableArray<IntegrationRequirementDecision> Decisions)
{
    /// <summary>Whether admission is permitted; pending runtime evidence is not satisfied evidence.</summary>
    public bool IsAdmissible => Compilation is not null && Validation.IsValid;
}

/// <summary>Resource-offer qualification over existing Requests, Processes and Contract properties.</summary>
public static class IntegrationQualification
{
    /// <summary>Canonical kind identifying a retained capability qualification plan.</summary>
    public const string PlanKind = "integration.qualification-plan.v1";
    internal static ExecutionDefinitionDocument Declare<T>(string kind, string id, string revision, T source) where T : class
    {
        var document = ExecutionDefinitionDocument.Create(new(kind), new(id), new(revision), source,
            new(new("cohesive.integrations.qualification", "1"), new(id), DocumentOrigin.Generated));
        var m = document.Metadata;
        var map = new ExecutionSourceMap([.. document.Definition.EnumerateObject().Where(p => p.Name != "$types")
            .Select(p => new ExecutionSourceProvenance(id + "#/" + p.Name, new([p.Name])))]);
        return new(document.Kind, new ExecutionDefinitionMetadata(m.DefinitionId, m.RevisionId, m.SchemaVersion, m.Fingerprint,
            m.Provenance, m.DisplayName, m.Description, map, m.Diagnostics), document.Definition, document.Extensions);
    }

    /// <summary>Qualifies an already configured candidate without silently choosing another provider.</summary>
    /// <typeparam name="T">Domain capability data.</typeparam>
    /// <param name="candidate">Existing compiled binding and Process.</param>
    /// <param name="offer">Exact resource capability source.</param>
    /// <param name="evaluator">Trusted domain interpretation.</param>
    /// <param name="atUtc">Explicit admission time within the offer evidence window.</param>
    /// <returns>Admitted compilation or rejection diagnostics; physical compiler warnings are preserved.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static IntegrationQualificationResult Qualify<T>(IntegrationCompilation candidate, IntegrationCapabilityOffer offer,
        IntegrationCapabilityEvaluator<T> evaluator, DateTimeOffset atUtc) where T : class
    {
        ArgumentNullException.ThrowIfNull(candidate); ArgumentNullException.ThrowIfNull(offer); ArgumentNullException.ThrowIfNull(evaluator);
        IntegrationQualificationResult Fail(DocumentValidationResult failure, ImmutableArray<IntegrationRequirementDecision> decisions = default) =>
            new(candidate, offer.Document, null, failure, decisions.IsDefault ? [] : decisions);
        if (!candidate.IsSuccessful || candidate.Qualification is not null) return Fail(Error("candidate", "Supply an unqualified, successful compilation."));
        // An evidence document can already belong to the compilation; identical exact dependencies are retained once.
        var inputs = candidate.Documents.Concat(offer.Dependencies).Append(offer.Document).Append(evaluator.Definition).ToArray();
        foreach (var input in inputs)
        {
            var integrity = ExecutionDefinitionDocumentValidator.Validate(input);
            if (!integrity.IsValid) return Fail(integrity);
        }
        if (inputs.GroupBy(d => d.Reference).Any(g => g.Select(d => ExecutionDefinitionJsonSerializer.Serialize(d)).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            return Fail(Error("provenance", "Duplicate exact dependencies have conflicting provenance or metadata."));
        var documents = inputs.DistinctBy(d => d.Reference).ToImmutableArray();
        var result = ExecutionDefinitionDocumentCatalog.TryCreate(documents, out var catalog);
        if (!result.IsValid) return Fail(result);
        result = Binding(candidate, catalog!, out var binding, out var connection);
        if (!result.IsValid) return Fail(result);
        result = Read<IntegrationCapabilityOfferDefinition>(offer.Document, "integration.capability-offer.v1", out var offered);
        if (!result.IsValid) return Fail(result);
        result = ValidateOffer(offered!, connection!, catalog!, atUtc);
        if (!result.IsValid) return Fail(result);
        if (offered!.Schema != evaluator.Definition.GetDefinition<IntegrationEvaluatorDefinition>().Schema)
            return Fail(Error("schema", "The evaluator does not interpret this capability schema."));
        catalog!.TryResolve(offered.Capabilities, out var data);
        result = Read<T>(data!, "integration.capability-data.v1", out var capabilities);
        if (!result.IsValid) return Fail(result);
        ImmutableArray<IntegrationRequirementDecision> decisions;
        try { decisions = evaluator.Evaluate(binding!, capabilities!, offered, atUtc, catalog); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        { return Fail(Error("interpretation", "The evaluator cannot interpret the supplied domain evidence or requirement context.")); }
        result = ValidateDecisions(binding!, decisions);
        if (!result.IsValid) return Fail(result, decisions);
        var ordered = binding!.Properties.Where(p => p.Use.Role == ContractRole.Requires)
            .Select(p => decisions.Single(d => d.Property == p.Use.Property)).ToImmutableArray();
        var plan = Declare(PlanKind, candidate.Binding!.Metadata.DefinitionId.Value + "/qualification", "v1",
            new IntegrationQualificationPlan(candidate.Binding.Reference, candidate.Process!.Reference, offer.Document.Reference,
                evaluator.Definition.Reference, atUtc, ordered));
        var compilation = candidate with { Documents = documents.Add(plan), Qualification = plan };
        return new(candidate, offer.Document, compilation, result, ordered);
    }

    /// <summary>Validates retained qualification lineage and, when supplied, current evidence freshness for new dispatch.</summary>
    /// <param name="compilation">Compilation containing the exact qualification plan and its dependencies.</param>
    /// <param name="dispatchAtUtc">Null for original-effect reconciliation; otherwise the current UTC dispatch boundary.</param>
    /// <returns>Structured integrity, completeness and freshness diagnostics. This does not rerun unavailable evaluator code.</returns>
    /// <exception cref="ArgumentNullException">Compilation is null.</exception>
    public static DocumentValidationResult Inspect(IntegrationCompilation compilation, DateTimeOffset? dispatchAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        if (!compilation.IsSuccessful || compilation.Qualification is null) return Error("missing", "A qualified admission plan is required.");
        var result = ExecutionDefinitionDocumentCatalog.TryCreate(compilation.Documents, out var catalog);
        if (!result.IsValid) return result;
        result = catalog!.ValidateReference(compilation.Qualification.Reference, "/qualification", out var planDocument);
        if (!result.IsValid) return result;
        result = Read<IntegrationQualificationPlan>(planDocument!, PlanKind, out var plan);
        if (!result.IsValid) return result;
        if (plan!.Binding is null || plan.Process is null || plan.Offer is null || plan.Evaluator is null)
            return Error("references", "Qualification requires exact binding, Process, offer and evaluator references.");
        if (plan.Binding != compilation.Binding!.Reference || plan.Process != compilation.Process!.Reference)
            return Error("binding", "The qualification belongs to another binding or Process.");
        result = catalog.ValidateReference(plan.Process, "/process", out _);
        if (!result.IsValid) return result;
        result = Binding(compilation, catalog, out var binding, out var connection);
        if (!result.IsValid) return result;
        result = catalog.ValidateReference(plan.Offer, "/offer", out var offer);
        if (!result.IsValid) return result;
        result = Read<IntegrationCapabilityOfferDefinition>(offer!, "integration.capability-offer.v1", out var offered);
        if (!result.IsValid) return result;
        result = ValidateOffer(offered!, connection!, catalog, plan.AssessedAtUtc);
        if (!result.IsValid) return result;
        if (dispatchAtUtc is { } now)
        {
            if (now < plan.AssessedAtUtc) return Error("time", "Dispatch cannot precede admission.");
            result = ValidateOffer(offered!, connection!, catalog, now);
            if (!result.IsValid) return result;
        }
        result = catalog.ValidateReference(plan.Evaluator, "/evaluator", out var evaluator);
        if (!result.IsValid) return result;
        result = Read<IntegrationEvaluatorDefinition>(evaluator!, "integration.capability-evaluator.v1", out var interpretation);
        if (!result.IsValid) return result;
        if (interpretation!.Schema != offered!.Schema) return Error("schema", "Evaluator and capability schemas differ.");
        return ValidateDecisions(binding!, plan.Decisions);
    }

    static DocumentValidationResult Binding(IntegrationCompilation candidate, ExecutionDefinitionDocumentCatalog catalog,
        out IngestionProviderBindingInspection? binding, out IntegrationConnectionResolution? connection)
    {
        binding = null; connection = null;
        var result = catalog.ValidateReference(candidate.Binding!.Reference, "/binding", out var document);
        if (!result.IsValid) return result;
        result = InteractionContractCatalog.TryCreate(candidate.Documents.Where(d => d.Kind == InteractionContractDocuments.Kind), out var requests);
        if (!result.IsValid) return result;
        result = IngestionProviderBindingDocuments.Inspect(document!, catalog, requests!, out binding);
        if (!result.IsValid) return result;
        catalog.TryResolve(binding!.Binding.Contract.Ingestion, out var ingestion);
        result = IngestionDefinitionDocuments.TryLower(ingestion!, requests!, out var expectedProcess);
        if (!result.IsValid) return result;
        if (candidate.Process?.Reference != expectedProcess!.Reference)
            return Error("process", "The candidate Process does not realize the exact ingestion declaration.");
        if (binding.Binding.Connection is null) return Error("connection", "Qualification requires an exact connection-bound resource.");
        catalog.TryResolve(binding.Binding.Connection, out var resolved);
        return IntegrationConnectionDocuments.Inspect(resolved!, catalog, out connection);
    }

    static DocumentValidationResult ValidateOffer(IntegrationCapabilityOfferDefinition offer, IntegrationConnectionResolution connection,
        ExecutionDefinitionDocumentCatalog catalog, DateTimeOffset atUtc)
    {
        if (offer.Profile != connection.Profile || offer.Connection != connection.Connection)
            return Error("resource", "Capability evidence belongs to another profile or connection revision.");
        if (string.IsNullOrWhiteSpace(offer.Schema) || offer.ValidFromUtc.Offset != TimeSpan.Zero || offer.ValidUntilUtc.Offset != TimeSpan.Zero
            || offer.ValidUntilUtc <= offer.ValidFromUtc || atUtc.Offset != TimeSpan.Zero || atUtc < offer.ValidFromUtc || atUtc >= offer.ValidUntilUtc)
            return Error("evidence.window", "Capability evidence is expired, not yet valid, or has an invalid UTC window.");
        if (offer.Capabilities is null || offer.Evidence is null) return Error("evidence.missing", "Exact capabilities and evidence are required.");
        var result = catalog.ValidateReference(offer.Evidence, "/offer/evidence", out _);
        return result.IsValid ? catalog.ValidateReference(offer.Capabilities, "/offer/capabilities", out _) : result;
    }

    static DocumentValidationResult ValidateDecisions(IngestionProviderBindingInspection binding, ImmutableArray<IntegrationRequirementDecision> decisions)
    {
        var requirements = binding.Properties.Where(p => p.Use.Role == ContractRole.Requires).Select(p => p.Use.Property).ToHashSet();
        if (decisions.IsDefault || decisions.Length != requirements.Count || decisions.Any(d => d is null || !requirements.Remove(d.Property)))
            return Error("requirements", "Return exactly one decision for every required property; omissions and unrelated decisions are invalid.");
        foreach (var decision in decisions)
        {
            if (!Enum.IsDefined(decision.Status) || string.IsNullOrWhiteSpace(decision.Code) || string.IsNullOrWhiteSpace(decision.Explanation)
                || (decision.Status == IntegrationQualificationStatus.Unsupported ? decision.Realization != CapabilityRealizationKind.Unavailable
                    : decision.Realization is not (CapabilityRealizationKind.Native or CapabilityRealizationKind.Composed or CapabilityRealizationKind.Constrained)))
                return Error("decision", "Declare a valid status, realization and reason for each requirement.");
        }
        var rejected = decisions.Where(d => d.Status == IntegrationQualificationStatus.Unsupported).ToArray();
        return rejected.Length == 0 ? DocumentValidationResult.Valid : new([.. rejected.Select(d =>
            new DocumentValidationDiagnostic("integrations.qualification." + d.Code, DiagnosticSeverity.Error, d.Explanation, "/properties/" + d.Property.DefinitionId.Value))]);
    }

    static DocumentValidationResult Read<T>(ExecutionDefinitionDocument document, string kind, out T? value) where T : class
    {
        value = null;
        if (!document.Extensions.IsDefaultOrEmpty) return Error("extension", "Unsupported qualification extension.");
        var projection = new ExecutionDefinitionDocumentProjection<T>(new(kind), "integrations.qualification.kind", "integrations.qualification.projection",
            "integrations.qualification.wire", "Use canonical qualification source.");
        return projection.ValidateAndProject(ExecutionDefinitionDocumentValidator.Validate(document), document, _ => DocumentValidationResult.Valid, out value);
    }
    internal static DocumentValidationResult Error(string code, string message) => new([new("integrations.qualification." + code,
        DiagnosticSeverity.Error, message, "/qualification")]);
}
