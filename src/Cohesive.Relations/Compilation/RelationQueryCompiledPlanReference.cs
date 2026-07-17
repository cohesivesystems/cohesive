using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Compilation;

/// <summary>
/// Versioned cryptographic fingerprint of one compiled relation/query plan component.
/// </summary>
public sealed record RelationQueryPlanComponentFingerprint
{
    /// <summary>Creates a compiled-plan component fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile applied before hashing.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="algorithm"/>, <paramref name="canonicalization"/>, or <paramref name="value"/>
    /// is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="algorithm"/>, <paramref name="canonicalization"/>, or <paramref name="value"/>
    /// is empty or white space.
    /// </exception>
    [JsonConstructor]
    public RelationQueryPlanComponentFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; init; }

    /// <summary>Canonicalization profile applied before hashing.</summary>
    public string Canonicalization { get; init; }

    /// <summary>Lowercase hexadecimal hash value.</summary>
    public string Value { get; init; }
}

/// <summary>
/// Portable attribution to one exact demand-scoped compiled relation/query plan.
/// </summary>
/// <remarks>
/// References created with <see cref="From"/> are computed at most once per compiled plan and weakly
/// cached. Repeated consumers therefore do not serialize or hash shape graphs again and do not extend
/// the compiled plan's lifetime.
/// </remarks>
public sealed class RelationQueryCompiledPlanReference
{
    const string CompilerProfileComponent = "compiler profile";
    const string DefinitionSchemaVersionComponent = "definition schema version";
    const string DefinitionComponent = "definition";
    const string ShapesComponent = "shapes";
    const string CatalogComponent = "catalog";
    const string DemandComponent = "demand";
    const string InputsComponent = "inputs";
    static readonly ConditionalWeakTable<CompiledRelationQueryPlan, Lazy<RelationQueryCompiledPlanReference>>
        References = new();

    /// <summary>Creates portable attribution to an exact compiled input contract.</summary>
    /// <param name="compilerProfile">Stable compiler profile that produced the plan.</param>
    /// <param name="definitionSchemaVersion">Portable schema version of the canonical definition document.</param>
    /// <param name="definitionFingerprint">Fingerprint of the canonical relation/query definition.</param>
    /// <param name="shapeSnapshotsFingerprint">
    /// Semantic fingerprint of the ordered shape-graph snapshots consumed by compilation.
    /// </param>
    /// <param name="relationshipCatalogFingerprint">
    /// Relationship-catalog fingerprint, or <see langword="null"/> when no catalog was supplied.
    /// </param>
    /// <param name="demandFingerprint">Semantic fingerprint of the effective output demand.</param>
    /// <param name="inputs">Canonical semantic input identities belonging to the compiled contract.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compilerProfile"/>, <paramref name="definitionSchemaVersion"/>,
    /// <paramref name="definitionFingerprint"/>, <paramref name="shapeSnapshotsFingerprint"/>, or
    /// <paramref name="demandFingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="compilerProfile"/> or <paramref name="definitionSchemaVersion"/> is empty or
    /// white space; <paramref name="inputs"/> is empty, contains a default identity, or repeats an identity.
    /// </exception>
    [JsonConstructor]
    public RelationQueryCompiledPlanReference(
        string compilerProfile,
        string definitionSchemaVersion,
        RelationQueryDefinitionFingerprint definitionFingerprint,
        RelationQueryPlanComponentFingerprint shapeSnapshotsFingerprint,
        RelationshipCatalogFingerprint? relationshipCatalogFingerprint,
        RelationQueryPlanComponentFingerprint demandFingerprint,
        ImmutableArray<RelationQueryInputId> inputs)
    {
        CompilerProfile = Guard.RequireNotNullOrWhiteSpace(compilerProfile);
        DefinitionSchemaVersion = Guard.RequireNotNullOrWhiteSpace(definitionSchemaVersion);
        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
        ShapeSnapshotsFingerprint = Guard.RequireNotNull(shapeSnapshotsFingerprint);
        RelationshipCatalogFingerprint = relationshipCatalogFingerprint;
        DemandFingerprint = Guard.RequireNotNull(demandFingerprint);

        var normalizedInputs = inputs.IsDefault ? [] : inputs;
        if (normalizedInputs.IsDefaultOrEmpty)
            throw new ArgumentException("A compiled plan reference requires at least one input identity.", nameof(inputs));
        if (normalizedInputs.Any(static input => string.IsNullOrWhiteSpace(input.Value)))
            throw new ArgumentException("Compiled plan input identities cannot be default.", nameof(inputs));
        if (normalizedInputs.GroupBy(static input => input).Any(static group => group.Count() > 1))
            throw new ArgumentException("Compiled plan input identities cannot be repeated.", nameof(inputs));

        Inputs =
        [
            .. normalizedInputs.OrderBy(static input => input.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Stable compiler profile that produced the plan.</summary>
    public string CompilerProfile { get; }

    /// <summary>Portable schema version of the canonical definition document.</summary>
    public string DefinitionSchemaVersion { get; }

    /// <summary>Fingerprint of the canonical relation/query definition.</summary>
    public RelationQueryDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Semantic fingerprint of the ordered shape-graph snapshots consumed by compilation.</summary>
    public RelationQueryPlanComponentFingerprint ShapeSnapshotsFingerprint { get; }

    /// <summary>Relationship-catalog fingerprint, or <see langword="null"/> when no catalog was supplied.</summary>
    public RelationshipCatalogFingerprint? RelationshipCatalogFingerprint { get; }

    /// <summary>Semantic fingerprint of the effective output demand.</summary>
    public RelationQueryPlanComponentFingerprint DemandFingerprint { get; }

    /// <summary>Canonical semantic input identities sorted deterministically by their ordinal identity values.</summary>
    public ImmutableArray<RelationQueryInputId> Inputs { get; }

    /// <summary>Creates exact portable attribution from a compiled plan.</summary>
    /// <param name="plan">Compiled plan to identify.</param>
    /// <returns>
    /// A weakly cached immutable reference to the compiler profile, semantic snapshots, demand, and input identities.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A shape snapshot cannot be represented by the compiled-plan canonicalization profile.
    /// </exception>
    /// <exception cref="JsonException">A shape snapshot cannot be serialized as canonical JSON.</exception>
    /// <exception cref="NotSupportedException">
    /// A shape snapshot contains a runtime type unsupported by its JSON serializer.
    /// </exception>
    public static RelationQueryCompiledPlanReference From(CompiledRelationQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return References.GetValue(
            plan,
            static candidate => new(
                () => Create(candidate),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    internal ImmutableArray<string> GetMismatchedComponents(CompiledRelationQueryPlan plan)
    {
        var candidate = From(plan);
        ImmutableArray<string>.Builder mismatches = ImmutableArray.CreateBuilder<string>();
        if (!string.Equals(CompilerProfile, candidate.CompilerProfile, StringComparison.Ordinal))
            mismatches.Add(CompilerProfileComponent);
        if (!string.Equals(DefinitionSchemaVersion, candidate.DefinitionSchemaVersion, StringComparison.Ordinal))
            mismatches.Add(DefinitionSchemaVersionComponent);
        if (!Equals(DefinitionFingerprint, candidate.DefinitionFingerprint))
            mismatches.Add(DefinitionComponent);
        if (!Equals(ShapeSnapshotsFingerprint, candidate.ShapeSnapshotsFingerprint))
            mismatches.Add(ShapesComponent);
        if (!Equals(RelationshipCatalogFingerprint, candidate.RelationshipCatalogFingerprint))
            mismatches.Add(CatalogComponent);
        if (!Equals(DemandFingerprint, candidate.DemandFingerprint))
            mismatches.Add(DemandComponent);
        if (!Inputs.SequenceEqual(candidate.Inputs))
            mismatches.Add(InputsComponent);
        return mismatches.ToImmutable();
    }

    static RelationQueryCompiledPlanReference Create(CompiledRelationQueryPlan plan) =>
        new(
            plan.Provenance.CompilerProfile,
            plan.Provenance.DefinitionDocument.SchemaVersion,
            plan.Provenance.DefinitionFingerprint,
            RelationQueryCompiledPlanFingerprinter.ComputeShapeSnapshots(plan.Provenance.ShapeDocuments),
            plan.Provenance.RelationshipCatalogFingerprint,
            RelationQueryCompiledPlanFingerprinter.ComputeDemand(plan.Demand),
            [.. plan.RequirementGraph.Inputs.Select(static input => input.Id)]);
}

static class RelationQueryCompiledPlanFingerprinter
{
    const string Algorithm = "sha256";
    const string ShapeSnapshotsCanonicalization = "relation-query-plan-shapes/v1-c14n/v2";
    const string DemandCanonicalization = "relation-query-plan-demand/v1-c14n/v1";

    internal static RelationQueryPlanComponentFingerprint ComputeShapeSnapshots(
        ImmutableArray<ShapeGraphDocument> documents)
    {
        ArrayBufferWriter<byte> canonical = new();
        Append(canonical, ShapeSnapshotsCanonicalization);
        var ordered = documents.IsDefault
            ? []
            : documents
                .OrderBy(static document => document.Graph.Id.Value, StringComparer.Ordinal)
                .ThenBy(static document => document.SchemaVersion, StringComparer.Ordinal)
                .ToImmutableArray();
        Append(canonical, ordered.Length);
        foreach (var document in ordered)
        {
            Append(canonical, document.SchemaVersion);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var node = JsonSerializer.SerializeToNode(document.Graph, options)
                       ?? throw new InvalidOperationException(
                           "Failed to materialize a shape graph for compiled-plan canonicalization.");
            if (node is System.Text.Json.Nodes.JsonObject graph)
            {
                // Diagnostics describe graph construction; they are not semantic compiler inputs.
                graph.Remove("diagnostics");
                graph.Remove("hasErrors");
            }

            var graphBytes = CanonicalJsonWriter.GetCanonicalBytes(
                node,
                options,
                static propertyName => propertyName is "shapes" or "namedTypes" ? "id" : null);
            Append(canonical, graphBytes);
        }

        return Hash(ShapeSnapshotsCanonicalization, canonical.WrittenSpan);
    }

    internal static RelationQueryPlanComponentFingerprint ComputeDemand(RelationQueryCompilationDemand demand)
    {
        ArrayBufferWriter<byte> canonical = new();
        Append(canonical, DemandCanonicalization);
        Append(canonical, (int)demand.Kind);
        Append(canonical, demand.RelationFields.Length);
        foreach (var field in demand.RelationFields)
        {
            Append(canonical, field);
        }

        Append(canonical, demand.QueryResults.Length);
        foreach (var result in demand.QueryResults)
        {
            Append(canonical, result.Result.Value);
            Append(canonical, (int)result.Selection);
            Append(canonical, result.Fields.Length);
            foreach (var field in result.Fields)
            {
                Append(canonical, field);
            }
        }

        return Hash(DemandCanonicalization, canonical.WrittenSpan);
    }

    static RelationQueryPlanComponentFingerprint Hash(string canonicalization, ReadOnlySpan<byte> canonical)
    {
        var hash = SHA256.HashData(canonical);
        return new(Algorithm, canonicalization, Convert.ToHexString(hash).ToLowerInvariant());
    }

    static void Append(ArrayBufferWriter<byte> buffer, RelationQueryFieldReference field)
    {
        Append(buffer, field.Shape.GraphId.Value);
        Append(buffer, field.Shape.ShapeId.Value);
        Append(buffer, field.Path.Segments.Length);
        foreach (var segment in field.Path.Segments)
        {
            Append(buffer, (int)segment.Kind);
            AppendNullable(buffer, segment.Segment);
        }
    }

    static void Append(ArrayBufferWriter<byte> buffer, string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        Append(buffer, length);
        var destination = buffer.GetSpan(length);
        Encoding.UTF8.GetBytes(value.AsSpan(), destination);
        buffer.Advance(length);
    }

    static void AppendNullable(ArrayBufferWriter<byte> buffer, string? value)
    {
        if (value is null)
        {
            Append(buffer, -1);
            return;
        }

        Append(buffer, value);
    }

    static void Append(ArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> value)
    {
        Append(buffer, value.Length);
        value.CopyTo(buffer.GetSpan(value.Length));
        buffer.Advance(value.Length);
    }

    static void Append(ArrayBufferWriter<byte> buffer, int value)
    {
        var destination = buffer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(destination, value);
        buffer.Advance(sizeof(int));
    }
}
