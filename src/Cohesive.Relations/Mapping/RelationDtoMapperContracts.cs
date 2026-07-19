using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Mapping;

/// <summary>Convention used after explicit bindings when matching relation fields to CLR members.</summary>
public enum RelationDtoMemberConvention
{
    /// <summary>Only bindings explicitly supplied by the mapper profile are considered.</summary>
    ExplicitOnly = 0,

    /// <summary>Exact, ordinal CLR member names are considered after explicit bindings.</summary>
    ExactMemberName = 1,

    /// <summary>
    /// <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/> names are considered before exact,
    /// ordinal CLR member names and after explicit bindings.
    /// </summary>
    SerializedNameThenExactMemberName = 2
}

/// <summary>Source of one resolved relation-field-to-CLR-member binding.</summary>
public enum RelationDtoMemberBindingSource
{
    /// <summary>The binding was declared explicitly in a mapper profile.</summary>
    Explicit = 0,

    /// <summary>The relation field matched a member's serialized JSON name.</summary>
    SerializedName = 1,

    /// <summary>The relation field matched the CLR member name exactly.</summary>
    ExactMemberName = 2
}

/// <summary>Permitted numeric conversion family for compiled DTO kernels.</summary>
public enum RelationDtoNumericConversionPolicy
{
    /// <summary>The CLR numeric kind must exactly match the semantic scalar kind.</summary>
    ExactOnly = 0,

    /// <summary>Lossless widening from Int32 to Int64 or Decimal and from Int64 to Decimal is permitted.</summary>
    LosslessWidening = 1
}

/// <summary>Behavior when one or more canonical output rows cannot be materialized as DTOs.</summary>
public enum RelationDtoMappingFailurePolicy
{
    /// <summary>Fail the complete mapping operation and return no typed rows.</summary>
    Strict = 0,

    /// <summary>Retain successfully mapped rows and report all invalid rows as an incomplete result.</summary>
    CollectDiagnostics = 1,

    /// <summary>Retain successfully mapped rows and explicitly classify invalid rows as skipped.</summary>
    SkipInvalidRows = 2
}

/// <summary>Outcome of applying one compiled DTO kernel to a canonical relation execution.</summary>
public enum RelationDtoMappingStatus
{
    /// <summary>Every canonical row was materialized and the source execution was conclusive.</summary>
    Succeeded = 0,

    /// <summary>Invalid rows were skipped under explicit policy while the source execution was conclusive.</summary>
    SucceededWithSkippedRows = 1,

    /// <summary>The source execution or DTO materialization remained attributable but inconclusive.</summary>
    Incomplete = 2,

    /// <summary>No trustworthy typed result can be consumed under the selected policy.</summary>
    Failed = 3
}

/// <summary>Phase in which a relation DTO mapper diagnostic was produced.</summary>
public enum RelationDtoMapperDiagnosticPhase
{
    /// <summary>The target contract could not be compiled safely.</summary>
    Compilation = 0,

    /// <summary>A compiled kernel could not consume one execution or row safely.</summary>
    Runtime = 1
}

/// <summary>Stable diagnostic codes emitted by relation DTO mapper compilation and invocation.</summary>
public static class RelationDtoMapperDiagnosticCodes
{
    /// <summary>The plan does not expose a canonical relation terminal supported by this mapper.</summary>
    public const string UnsupportedTerminal = "REL3301";

    /// <summary>The plan's persisted output-shape snapshot could not be resolved.</summary>
    public const string OutputShapeUnavailable = "REL3302";

    /// <summary>The CLR target type cannot be materialized by the v1 mapper.</summary>
    public const string UnsupportedTargetType = "REL3303";

    /// <summary>A relation field or CLR member has more than one binding candidate at the same precedence.</summary>
    public const string AmbiguousMemberBinding = "REL3304";

    /// <summary>No unique usable CLR construction strategy exists.</summary>
    public const string ConstructorUnavailable = "REL3305";

    /// <summary>A required CLR target member has no relation-field binding.</summary>
    public const string RequiredTargetMemberUnmapped = "REL3306";

    /// <summary>A demanded relation output field has no CLR target member binding.</summary>
    public const string OutputFieldUnmapped = "REL3307";

    /// <summary>The semantic field type cannot be converted to the CLR member type under current options.</summary>
    public const string UnsupportedConversion = "REL3308";

    /// <summary>Semantic presence or nullability cannot satisfy the CLR target contract.</summary>
    public const string PresenceOrNullabilityMismatch = "REL3309";

    /// <summary>The execution was produced from a different compiled plan.</summary>
    public const string PlanMismatch = "REL3310";

    /// <summary>The execution has no matching canonical relation terminal.</summary>
    public const string RelationTerminalMismatch = "REL3311";

    /// <summary>A canonical row does not have the compiled output shape.</summary>
    public const string RowShapeMismatch = "REL3312";

    /// <summary>A canonical row contains a missing, null, or incompatible field value.</summary>
    public const string RuntimeFieldConversionFailed = "REL3313";

    /// <summary>A physical execution did not produce canonical interpretation output.</summary>
    public const string PhysicalInterpretationUnavailable = "REL3314";
}

/// <summary>Explicitly maps one top-level canonical relation output field to a CLR target member.</summary>
public sealed record RelationDtoMemberBinding
{
    /// <summary>Creates an explicit DTO member binding.</summary>
    /// <param name="outputField">Top-level output field emitted by the canonical relation terminal.</param>
    /// <param name="targetMember">Exact CLR property name to populate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="targetMember"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputField"/> is not a single field segment, or <paramref name="targetMember"/> is empty.
    /// </exception>
    public RelationDtoMemberBinding(FieldPath outputField, string targetMember)
    {
        if (outputField.Segments.Length != 1
            || !outputField.Segments[0].TryGetFieldIdentity(out _))
        {
            throw new ArgumentException("A v1 DTO binding requires one top-level output field.", nameof(outputField));
        }

        OutputField = outputField;
        TargetMember = Guard.RequireNotNullOrWhiteSpace(targetMember);
    }

    /// <summary>Top-level canonical output field.</summary>
    public FieldPath OutputField { get; }

    /// <summary>Exact CLR property name populated from <see cref="OutputField"/>.</summary>
    public string TargetMember { get; }
}

/// <summary>Deterministic member-binding policy used to compile relation DTO kernels.</summary>
public sealed class RelationDtoMapperProfile
{
    /// <summary>Default profile using serialized names and then exact CLR member names.</summary>
    public static RelationDtoMapperProfile Conventional { get; } = new("conventional-v1");

    /// <summary>Creates a deterministic DTO mapper profile.</summary>
    /// <param name="id">Stable profile identity used for attribution and caching.</param>
    /// <param name="bindings">Explicit field-to-member bindings, which take precedence over conventions.</param>
    /// <param name="memberConvention">Convention considered after explicit bindings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is empty; <paramref name="bindings"/> contains <see langword="null"/>, repeats an
    /// output field, or repeats a target member.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="memberConvention"/> is unsupported.</exception>
    public RelationDtoMapperProfile(
        string id,
        ImmutableArray<RelationDtoMemberBinding> bindings = default,
        RelationDtoMemberConvention memberConvention = RelationDtoMemberConvention.SerializedNameThenExactMemberName)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        if (!Enum.IsDefined(memberConvention))
        {
            throw new ArgumentOutOfRangeException(
                nameof(memberConvention), memberConvention, "Unsupported DTO member convention.");
        }

        var normalized = bindings.IsDefault ? [] : bindings;
        if (normalized.Any(static binding => binding is null))
            throw new ArgumentException("DTO mapper bindings cannot contain null entries.", nameof(bindings));
        if (normalized.GroupBy(static binding => binding.OutputField).Any(static group => group.Count() > 1))
            throw new ArgumentException("DTO mapper bindings cannot repeat an output field.", nameof(bindings));
        if (normalized.GroupBy(static binding => binding.TargetMember, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("DTO mapper bindings cannot repeat a target member.", nameof(bindings));
        }

        MemberConvention = memberConvention;
        Bindings =
        [
            .. normalized.OrderBy(static binding => binding.OutputField.ToString(), StringComparer.Ordinal)
                .ThenBy(static binding => binding.TargetMember, StringComparer.Ordinal)
        ];
        Fingerprint = RelationDtoMapperFingerprint.ComputeProfile(Id, MemberConvention, Bindings);
    }

    /// <summary>Stable profile identity.</summary>
    public string Id { get; }

    /// <summary>Convention applied after <see cref="Bindings"/>.</summary>
    public RelationDtoMemberConvention MemberConvention { get; }

    /// <summary>Explicit bindings in deterministic output-field order.</summary>
    public ImmutableArray<RelationDtoMemberBinding> Bindings { get; }

    /// <summary>Deterministic fingerprint of the effective profile.</summary>
    public string Fingerprint { get; }
}

/// <summary>Target-independent policy knobs used while compiling a CLR DTO kernel.</summary>
public sealed class RelationDtoMapperCompilationOptions
{
    /// <summary>Default compilation options.</summary>
    public static RelationDtoMapperCompilationOptions Conventional { get; } = new();

    /// <summary>Creates DTO mapper compilation options.</summary>
    /// <param name="numericConversions">Permitted numeric conversion family.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="numericConversions"/> is unsupported.</exception>
    public RelationDtoMapperCompilationOptions(
        RelationDtoNumericConversionPolicy numericConversions = RelationDtoNumericConversionPolicy.ExactOnly)
    {
        if (!Enum.IsDefined(numericConversions))
        {
            throw new ArgumentOutOfRangeException(
                nameof(numericConversions), numericConversions, "Unsupported DTO numeric conversion policy.");
        }

        NumericConversions = numericConversions;
        Fingerprint = RelationDtoMapperFingerprint.ComputeOptions(numericConversions);
    }

    /// <summary>Permitted numeric conversions.</summary>
    public RelationDtoNumericConversionPolicy NumericConversions { get; }

    /// <summary>Deterministic fingerprint of the effective options.</summary>
    public string Fingerprint { get; }
}

/// <summary>One resolved output-field-to-CLR-member decision in a compiled DTO mapper.</summary>
public sealed class RelationDtoMapperMemberDescriptor
{
    internal RelationDtoMapperMemberDescriptor(
        RelationQueryFieldReference outputField,
        string targetMember,
        Type targetType,
        RelationDtoMemberBindingSource bindingSource,
        RelationQueryOutputReference? outputReference)
    {
        OutputField = outputField;
        TargetMember = targetMember;
        TargetType = targetType;
        BindingSource = bindingSource;
        OutputReference = outputReference;
    }

    /// <summary>Graph-qualified demanded relation output field.</summary>
    public RelationQueryFieldReference OutputField { get; }

    /// <summary>Exact CLR property receiving the value.</summary>
    public string TargetMember { get; }

    /// <summary>CLR type accepted by the target member or constructor parameter.</summary>
    public Type TargetType { get; }

    /// <summary>Precedence tier that selected this binding.</summary>
    public RelationDtoMemberBindingSource BindingSource { get; }

    /// <summary>Demanded-output provenance for this field, or <see langword="null"/> when unavailable.</summary>
    public RelationQueryOutputReference? OutputReference { get; }
}

/// <summary>Exact plan, CLR target, profile, and options attribution for one mapper compilation attempt.</summary>
public sealed class RelationDtoMapperCompilationDescriptor
{
    internal RelationDtoMapperCompilationDescriptor(
        RelationQueryCompiledPlanReference planReference,
        Type outputType,
        string profileId,
        string profileFingerprint,
        string optionsFingerprint,
        string compilationIdentity)
    {
        PlanReference = planReference;
        OutputType = outputType;
        ProfileId = profileId;
        ProfileFingerprint = profileFingerprint;
        OptionsFingerprint = optionsFingerprint;
        CompilationIdentity = compilationIdentity;
    }

    /// <summary>Exact portable plan attribution supplied to compilation.</summary>
    public RelationQueryCompiledPlanReference PlanReference { get; }

    /// <summary>CLR output type requested from compilation.</summary>
    public Type OutputType { get; }

    /// <summary>Stable mapper profile identity.</summary>
    public string ProfileId { get; }

    /// <summary>Effective mapper profile fingerprint.</summary>
    public string ProfileFingerprint { get; }

    /// <summary>Effective compilation-options fingerprint.</summary>
    public string OptionsFingerprint { get; }

    /// <summary>Deterministic identity of this exact compilation attempt and cache entry.</summary>
    public string CompilationIdentity { get; }
}

/// <summary>Explainable identity and binding decisions for one compiled relation DTO kernel.</summary>
public sealed class RelationDtoMapperDescriptor
{
    internal RelationDtoMapperDescriptor(
        RelationDtoMapperCompilationDescriptor compilation,
        RelationId relation,
        QualifiedShapeId outputShape,
        RelationOutputMode outputMode,
        ImmutableArray<RelationDtoMapperMemberDescriptor> members)
    {
        Compilation = compilation;
        Relation = relation;
        OutputShape = outputShape;
        OutputMode = outputMode;
        Members = members;
    }

    /// <summary>Exact compilation-attempt attribution shared with the compilation result.</summary>
    public RelationDtoMapperCompilationDescriptor Compilation { get; }

    /// <summary>Exact portable plan attribution accepted by the kernel.</summary>
    public RelationQueryCompiledPlanReference PlanReference => Compilation.PlanReference;

    /// <summary>Canonical relation terminal accepted by the kernel.</summary>
    public RelationId Relation { get; }

    /// <summary>Graph-qualified canonical output shape accepted by the kernel.</summary>
    public QualifiedShapeId OutputShape { get; }

    /// <summary>Canonical relation output mode accepted by the kernel.</summary>
    public RelationOutputMode OutputMode { get; }

    /// <summary>CLR output type produced by the kernel.</summary>
    public Type OutputType => Compilation.OutputType;

    /// <summary>Stable mapper profile identity.</summary>
    public string ProfileId => Compilation.ProfileId;

    /// <summary>Effective mapper profile fingerprint.</summary>
    public string ProfileFingerprint => Compilation.ProfileFingerprint;

    /// <summary>Effective compilation-options fingerprint.</summary>
    public string OptionsFingerprint => Compilation.OptionsFingerprint;

    /// <summary>Deterministic identity of this plan, target type, profile, and options combination.</summary>
    public string CompilationIdentity => Compilation.CompilationIdentity;

    /// <summary>Resolved member decisions in canonical output-field order.</summary>
    public ImmutableArray<RelationDtoMapperMemberDescriptor> Members { get; }
}

/// <summary>Structured, provenance-attributed DTO mapper compilation or runtime diagnostic.</summary>
public sealed class RelationDtoMapperDiagnostic
{
    internal RelationDtoMapperDiagnostic(
        string code,
        DiagnosticSeverity severity,
        RelationDtoMapperDiagnosticPhase phase,
        string message,
        RelationId? relation = null,
        QualifiedShapeId? shape = null,
        FieldPath? field = null,
        string? targetMember = null,
        QueryNodeId? node = null,
        QueryAssignmentId? assignment = null,
        RelationQueryEvaluationId? evaluation = null,
        RelationQueryOccurrenceId? occurrence = null,
        int? rowIndex = null)
    {
        Code = code;
        Severity = severity;
        Phase = phase;
        Message = message;
        Relation = relation;
        Shape = shape;
        Field = field;
        TargetMember = targetMember;
        Node = node;
        Assignment = assignment;
        Evaluation = evaluation;
        Occurrence = occurrence;
        RowIndex = rowIndex;
    }

    /// <summary>Stable machine-readable REL33xx code.</summary>
    public string Code { get; }

    /// <summary>Diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Compilation or runtime phase that detected the condition.</summary>
    public RelationDtoMapperDiagnosticPhase Phase { get; }

    /// <summary>Human-readable explanation that excludes source payload values.</summary>
    public string Message { get; }

    /// <summary>Affected canonical relation, or <see langword="null"/>.</summary>
    public RelationId? Relation { get; }

    /// <summary>Affected graph-qualified shape, or <see langword="null"/>.</summary>
    public QualifiedShapeId? Shape { get; }

    /// <summary>Affected canonical output field, or <see langword="null"/>.</summary>
    public FieldPath? Field { get; }

    /// <summary>Affected CLR member, or <see langword="null"/>.</summary>
    public string? TargetMember { get; }

    /// <summary>Producing canonical node, or <see langword="null"/>.</summary>
    public QueryNodeId? Node { get; }

    /// <summary>Producing canonical projection assignment, or <see langword="null"/>.</summary>
    public QueryAssignmentId? Assignment { get; }

    /// <summary>Runtime evaluation identity, or <see langword="null"/> for compilation diagnostics.</summary>
    public RelationQueryEvaluationId? Evaluation { get; }

    /// <summary>Root occurrence affected at runtime, or <see langword="null"/>.</summary>
    public RelationQueryOccurrenceId? Occurrence { get; }

    /// <summary>Zero-based canonical terminal row index, or <see langword="null"/>.</summary>
    public int? RowIndex { get; }
}

/// <summary>Result of compiling or retrieving one cached relation DTO kernel.</summary>
/// <typeparam name="TOutput">CLR DTO type produced by a successful mapper.</typeparam>
public sealed class RelationDtoMapperCompilationResult<TOutput>
{
    internal RelationDtoMapperCompilationResult(
        RelationDtoMapperCompilationDescriptor descriptor,
        CompiledRelationDtoMapper<TOutput>? mapper,
        ImmutableArray<RelationDtoMapperDiagnostic> diagnostics
        )
    {
        Descriptor = descriptor;
        Mapper = mapper;
        Diagnostics = diagnostics;
    }

    /// <summary>Exact plan, CLR target, profile, options, and cache identity for this compilation attempt.</summary>
    public RelationDtoMapperCompilationDescriptor Descriptor { get; }

    /// <summary>Compiled mapper, or <see langword="null"/> when fail-closed validation rejected the target.</summary>
    public CompiledRelationDtoMapper<TOutput>? Mapper { get; }

    /// <summary>Deterministically ordered compilation diagnostics.</summary>
    public ImmutableArray<RelationDtoMapperDiagnostic> Diagnostics { get; }

    /// <summary>Whether a trustworthy compiled mapper is available.</summary>
    public bool IsSuccessful => Mapper is not null && Diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}

/// <summary>One successfully materialized DTO paired with its exact canonical source row.</summary>
/// <typeparam name="TOutput">CLR DTO type.</typeparam>
public readonly struct RelationDtoMappedRow<TOutput>
{
    internal RelationDtoMappedRow(RelationQueryOutputRow source, TOutput value)
    {
        Source = source;
        Value = value;
    }

    /// <summary>Exact canonical relation output row consumed by the kernel.</summary>
    public RelationQueryOutputRow Source { get; }

    /// <summary>Completely materialized CLR DTO.</summary>
    public TOutput Value { get; }
}

/// <summary>One canonical row rejected before a partial DTO could escape.</summary>
public sealed class RelationDtoRowFailure
{
    internal RelationDtoRowFailure(
        int rowIndex,
        RelationQueryOutputRow source,
        ImmutableArray<RelationDtoMapperDiagnostic> diagnostics
        )
    {
        RowIndex = rowIndex;
        Source = source;
        Diagnostics = diagnostics;
    }

    /// <summary>Zero-based row index in the canonical relation terminal.</summary>
    public int RowIndex { get; }

    /// <summary>Exact canonical relation output row that could not be materialized.</summary>
    public RelationQueryOutputRow Source { get; }

    /// <summary>Attributable runtime diagnostics for this row.</summary>
    public ImmutableArray<RelationDtoMapperDiagnostic> Diagnostics { get; }
}

/// <summary>Typed materialization layered over an exact canonical or physical execution result.</summary>
/// <typeparam name="TOutput">CLR DTO type.</typeparam>
public sealed class RelationDtoMappingResult<TOutput>
{
    internal RelationDtoMappingResult(
        RelationDtoMappingStatus status,
        RelationQueryExecutionResult? execution,
        RelationQueryPhysicalExecutionResult? physicalExecution,
        ImmutableArray<RelationDtoMappedRow<TOutput>> rows,
        ImmutableArray<RelationDtoRowFailure> failedRows,
        ImmutableArray<RelationDtoMapperDiagnostic> diagnostics)
    {
        Status = status;
        Execution = execution;
        PhysicalExecution = physicalExecution;
        Rows = rows;
        FailedRows = failedRows;
        Diagnostics = diagnostics;
    }

    /// <summary>Typed materialization outcome.</summary>
    public RelationDtoMappingStatus Status { get; }

    /// <summary>
    /// Exact canonical execution interpreted by the mapper, or <see langword="null"/> when physical execution
    /// failed before canonical interpretation.
    /// </summary>
    public RelationQueryExecutionResult? Execution { get; }

    /// <summary>Exact physical execution supplied to the mapper overload, or <see langword="null"/>.</summary>
    public RelationQueryPhysicalExecutionResult? PhysicalExecution { get; }

    /// <summary>Successfully mapped rows, each retaining its exact canonical source row.</summary>
    public ImmutableArray<RelationDtoMappedRow<TOutput>> Rows { get; }

    /// <summary>Rows rejected without exposing partial DTO instances.</summary>
    public ImmutableArray<RelationDtoRowFailure> FailedRows { get; }

    /// <summary>Operation-level and row-level DTO mapper diagnostics.</summary>
    public ImmutableArray<RelationDtoMapperDiagnostic> Diagnostics { get; }

    /// <summary>Whether typed output is conclusive under the selected failure policy.</summary>
    public bool IsSuccessful => Status is RelationDtoMappingStatus.Succeeded or RelationDtoMappingStatus.SucceededWithSkippedRows;
}

static class RelationDtoMapperFingerprint
{
    internal static string ComputeProfile(string id, RelationDtoMemberConvention convention, ImmutableArray<RelationDtoMemberBinding> bindings)
    {
        StringBuilder canonical = new("relation-dto-profile/v1\n");
        AppendToken(canonical, "profile-id");
        AppendToken(canonical, id);
        AppendToken(canonical, "member-convention");
        AppendToken(canonical, ((int)convention).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendToken(canonical, "binding-count");
        AppendToken(canonical, bindings.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var binding in bindings)
        {
            AppendToken(canonical, "binding");
            AppendToken(canonical, binding.OutputField.ToString());
            AppendToken(canonical, binding.TargetMember);
        }

        return Hash(canonical.ToString());
    }

    internal static string ComputeOptions(RelationDtoNumericConversionPolicy numericConversions) =>
        Hash(string.Concat(
            "relation-dto-options/v1\n",
            ((int)numericConversions).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "\n"));

    internal static string ComputeCompilation(
        RelationQueryCompiledPlanReference planReference,
        Type outputType,
        string profileFingerprint,
        string optionsFingerprint)
    {
        StringBuilder canonical = new("relation-dto-compilation/v1\n");
        AppendToken(canonical, "compiler-profile");
        AppendToken(canonical, planReference.CompilerProfile);
        AppendToken(canonical, "definition-schema-version");
        AppendToken(canonical, planReference.DefinitionSchemaVersion);
        AppendToken(canonical, "definition-fingerprint");
        AppendFingerprint(
            canonical,
            planReference.DefinitionFingerprint.Algorithm,
            planReference.DefinitionFingerprint.Canonicalization,
            planReference.DefinitionFingerprint.Value);
        AppendToken(canonical, "shape-snapshots-fingerprint");
        AppendFingerprint(
            canonical,
            planReference.ShapeSnapshotsFingerprint.Algorithm,
            planReference.ShapeSnapshotsFingerprint.Canonicalization,
            planReference.ShapeSnapshotsFingerprint.Value);
        if (planReference.RelationshipCatalogFingerprint is { } catalog)
        {
            AppendToken(canonical, "catalog-fingerprint");
            AppendFingerprint(canonical, catalog.Algorithm, catalog.Canonicalization, catalog.Value);
        }
        else
        {
            AppendToken(canonical, "catalog-absent");
        }
        AppendToken(canonical, "demand-fingerprint");
        AppendFingerprint(
            canonical,
            planReference.DemandFingerprint.Algorithm,
            planReference.DemandFingerprint.Canonicalization,
            planReference.DemandFingerprint.Value);
        AppendToken(canonical, "input-count");
        AppendToken(canonical, planReference.Inputs.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var input in planReference.Inputs)
        {
            AppendToken(canonical, "input");
            AppendToken(canonical, input.Value);
        }
        AppendToken(canonical, "output-type");
        AppendToken(canonical, outputType.AssemblyQualifiedName ?? outputType.FullName ?? outputType.Name);
        AppendToken(canonical, "profile-fingerprint");
        AppendToken(canonical, profileFingerprint);
        AppendToken(canonical, "options-fingerprint");
        AppendToken(canonical, optionsFingerprint);
        return Hash(canonical.ToString());
    }

    static void AppendFingerprint(
        StringBuilder canonical,
        string algorithm,
        string canonicalization,
        string value)
    {
        AppendToken(canonical, algorithm);
        AppendToken(canonical, canonicalization);
        AppendToken(canonical, value);
    }

    static void AppendToken(StringBuilder canonical, string value) =>
        canonical.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');

    static string Hash(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
