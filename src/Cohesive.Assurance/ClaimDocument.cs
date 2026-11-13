using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

/// <summary>Maps a derived semantic location to the exact reviewed authoring source.</summary>
/// <param name="Path">Stable expression or claim path; not a host line-number guess.</param>
/// <param name="Source">Exact source reference containing this declaration.</param>
public sealed record ClaimSourceLocation(string Path, ClaimReference Source);

/// <summary>Versioned, integrity-checked derived claim artifact.</summary>
public sealed class ClaimDocument
{
    /// <summary>Current persisted schema.</summary>
    public const string CurrentSchema = "cohesive-assurance-claim/v1";
    /// <summary>Version of source-to-claim lowering.</summary>
    public const string LoweringVersion = "cohesive.assurance.lowering/v1";
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    [JsonConstructor]
    internal ClaimDocument(string schema, string lowering, string predicateProfile, ClaimDefinition definition, ClaimReference source,
        ClaimReference configuration, string producer, string semanticFingerprint, string fingerprint)
    {
        if (schema != CurrentSchema) throw new ArgumentException("Unsupported claim schema.", nameof(schema));
        if (lowering != LoweringVersion || predicateProfile != AssurancePredicates.ProfileId)
            throw new ArgumentException("Unsupported lowering or predicate profile version.");
        Definition = Guard.RequireNotNull(definition);
        Source = Guard.RequireNotNull(source);
        Configuration = Guard.RequireNotNull(configuration);
        Producer = Guard.RequireNotNullOrWhiteSpace(producer);
        Schema = schema;
        Lowering = lowering;
        PredicateProfile = predicateProfile;
        var analysis = definition.Analyze();
        if (!analysis.IsValid) throw new ClaimValidationException(analysis.Validation);
        var semantic = Hash(new SemanticPayload(schema, predicateProfile, definition));
        var artifact = Hash(new Payload(schema, LoweringVersion, AssurancePredicates.ProfileId,
            definition, source, configuration, producer));
        if (!string.Equals(semantic, semanticFingerprint, StringComparison.Ordinal)
            || !string.Equals(artifact, fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Claim fingerprint does not match canonical content.");
        SemanticFingerprint = semantic;
        Fingerprint = artifact;
        SourceMap = [.. analysis.CapabilityUses.Select(x => x.ExpressionPath)
            .Concat(analysis.FieldUses.Select(x => x.ExpressionPath))
            .Concat(analysis.BindingUses.Select(x => x.ExpressionPath))
            .Append("/predicate").Append("/").Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).Select(path => new ClaimSourceLocation(path, source))];
    }

    /// <summary>Persisted schema identity.</summary>
    public string Schema { get; }
    /// <summary>Exact lowering implementation version.</summary>
    public string Lowering { get; }
    /// <summary>Exact predicate semantics/capability profile.</summary>
    public string PredicateProfile { get; }
    /// <summary>Canonical semantic authority for interpretations.</summary>
    public ClaimDefinition Definition { get; }
    /// <summary>Exact reviewed source from which this artifact was derived.</summary>
    public ClaimReference Source { get; }
    /// <summary>Exact effective configuration and convention profile.</summary>
    public ClaimReference Configuration { get; }
    /// <summary>Versioned expression/structural source producer.</summary>
    public string Producer { get; }
    /// <summary>SHA-256 of canonical claim semantics; excludes authoring provenance.</summary>
    public string SemanticFingerprint { get; }
    /// <summary>SHA-256 of semantics, source, configuration and compiler/profile versions.</summary>
    public string Fingerprint { get; }
    /// <summary>Derived stable-path attribution to reviewed source, regenerated on restore.</summary>
    [JsonIgnore]
    public ImmutableArray<ClaimSourceLocation> SourceMap { get; }

    /// <summary>Checks that an owning workspace can resolve every exact referenced artifact.</summary>
    /// <param name="available">Exact catalog entries, including source/configuration and domain contracts.</param>
    /// <returns>Structured diagnostics for missing or revision/fingerprint-mismatched references.</returns>
    /// <remarks>This checks reference identity, not provider truth or runtime conformance.</remarks>
    /// <exception cref="ArgumentNullException">The catalog is null.</exception>
    /// <exception cref="ArgumentException">A catalog entry is null.</exception>
    public DocumentValidationResult ValidateReferences(IEnumerable<ClaimReference> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        var catalog = available.ToHashSet();
        if (catalog.Contains(null!)) throw new ArgumentException("Catalog entries cannot be null.", nameof(available));
        List<DocumentValidationDiagnostic> diagnostics = [];
        void Check(ClaimReference reference, string path)
        {
            if (!catalog.Contains(reference))
                diagnostics.Add(new("assurance.reference.unresolved", DiagnosticSeverity.Error,
                    $"Exact reference '{reference.Source}' revision '{reference.Revision}' fingerprint '{reference.Fingerprint}' is unavailable.",
                    path));
        }
        Check(Source, "/source");
        Check(Configuration, "/configuration");
        Check(Definition.Observation, "/definition/observation");
        Check(Definition.Selection, "/definition/selection");
        if (Definition.Witness is { } witness) Check(witness, "/definition/witness");
        for (var i = 0; i < Definition.Attachments.Length; i++)
            Check(Definition.Attachments[i].Reference, $"/definition/attachments/{i}/reference");
        for (var i = 0; i < Definition.Assumptions.Length; i++)
            Check(Definition.Assumptions[i], $"/definition/assumptions/{i}");
        for (var i = 0; i < Definition.EvidenceRequirements.Length; i++)
            Check(Definition.EvidenceRequirements[i], $"/definition/evidenceRequirements/{i}");
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    internal static ClaimDocument Create(ClaimDefinition definition, ClaimReference source,
        ClaimReference configuration, string producer) =>
        new(CurrentSchema, LoweringVersion, AssurancePredicates.ProfileId, definition, source, configuration, producer,
            Hash(new SemanticPayload(CurrentSchema, AssurancePredicates.ProfileId, definition)),
            Hash(new Payload(CurrentSchema, LoweringVersion, AssurancePredicates.ProfileId,
                definition, source, configuration, producer)));

    static string Hash<T>(T value) where T : class => Convert.ToHexStringLower(
        StrictDocumentJson.ComputeCanonicalSha256(value, Options));

    sealed record Payload(string Schema, string Lowering, string PredicateProfile, ClaimDefinition Definition,
        ClaimReference Source, ClaimReference Configuration, string Producer);
    sealed record SemanticPayload(string Schema, string PredicateProfile, ClaimDefinition Definition);

    /// <summary>Serializes this derived artifact in deterministic canonical JSON.</summary>
    /// <returns>Canonical JSON including integrity fingerprints.</returns>
    /// <exception cref="JsonException">Content has no valid portable encoding.</exception>
    /// <exception cref="NotSupportedException">Content contains an unsupported runtime representation.</exception>
    public string Serialize() => Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(this, Options));

    /// <summary>Restores and validates a derived artifact, never treating JSON as authoring source.</summary>
    /// <param name="json">Strict persisted JSON to restore.</param>
    /// <returns>The complete validated artifact.</returns>
    /// <exception cref="JsonException">JSON, schema, semantic validation or integrity is invalid.</exception>
    public static ClaimDocument Deserialize(string json)
    {
        if (StrictDocumentJson.TryReadCanonicalObject(json, Options, "assurance claim", out ClaimDocument? result, out var error)
            && result is not null) return result;
        throw new JsonException($"{error.Failure} at {error.Location}: {error.Message}");
    }
}
