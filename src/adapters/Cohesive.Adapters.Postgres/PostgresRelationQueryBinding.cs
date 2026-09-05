using Cohesive.Adapters.Sql;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Stable identity of a versioned PostgreSQL relation/query storage binding.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct PostgresRelationQueryBindingId
{
    /// <summary>Creates a PostgreSQL storage-binding identity.</summary>
    /// <param name="value">Stable versioned identity value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public PostgresRelationQueryBindingId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Stable versioned identity value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable identity of the physical PostgreSQL database interpreted by a storage binding.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct PostgresRelationQueryDatabaseId
{
    /// <summary>Creates a physical database identity.</summary>
    /// <param name="value">Stable non-secret database identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public PostgresRelationQueryDatabaseId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Stable non-secret database identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Deterministic fingerprint of one normalized PostgreSQL storage binding.</summary>
public sealed record PostgresRelationQueryBindingFingerprint
{
    /// <summary>Creates a PostgreSQL binding fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    public PostgresRelationQueryBindingFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal hash value.</summary>
    public string Value { get; }
}

/// <summary>Origin of a PostgreSQL storage-binding decision.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostgresRelationQueryBindingOrigin
{
    /// <summary>The effective binding contains an explicit local or scoped declaration.</summary>
    Explicit = 0,

    /// <summary>The complete binding was derived by a named deterministic convention set.</summary>
    Convention = 1
}

/// <summary>Physical PostgreSQL scalar type used to preserve a portable scalar value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostgresRelationQueryScalarType
{
    /// <summary>PostgreSQL <c>boolean</c>.</summary>
    Boolean = 0,

    /// <summary>PostgreSQL <c>integer</c>.</summary>
    Int32 = 1,

    /// <summary>PostgreSQL <c>bigint</c>.</summary>
    Int64 = 2,

    /// <summary>PostgreSQL exact <c>numeric</c>.</summary>
    Numeric = 3,

    /// <summary>PostgreSQL <c>text</c> or a compatible character type.</summary>
    Text = 4,

    /// <summary>PostgreSQL <c>uuid</c>.</summary>
    Uuid = 5,

    /// <summary>PostgreSQL <c>date</c>.</summary>
    Date = 6,

    /// <summary>PostgreSQL <c>timestamp without time zone</c>.</summary>
    Timestamp = 7,

    /// <summary>PostgreSQL <c>timestamp with time zone</c>.</summary>
    TimestampWithTimeZone = 8,

    /// <summary>PostgreSQL <c>bytea</c>.</summary>
    Bytea = 9
}

/// <summary>How semantic missing values are represented by a PostgreSQL column.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostgresRelationQueryMissingValueEncoding
{
    /// <summary>The semantic contract and physical schema prohibit a missing value.</summary>
    Prohibited = 0,

    /// <summary>SQL <c>NULL</c> represents semantic missing; the semantic value must therefore be non-nullable.</summary>
    SqlNull = 1
}

/// <summary>How semantic explicit null is represented by a PostgreSQL column.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostgresRelationQueryNullValueEncoding
{
    /// <summary>The semantic contract and physical schema prohibit explicit null.</summary>
    Prohibited = 0,

    /// <summary>SQL <c>NULL</c> represents semantic explicit null; semantic missing must therefore be prohibited.</summary>
    SqlNull = 1
}

/// <summary>Equality semantics attested for a PostgreSQL text column and collation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostgresRelationQueryTextEqualitySemantics
{
    /// <summary>No equivalence with canonical text equality is asserted.</summary>
    Unspecified = 0,

    /// <summary>The declared collation preserves canonical ordinal equality.</summary>
    Ordinal = 1
}

/// <summary>Ordering semantics attested for a PostgreSQL text column and collation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostgresRelationQueryTextOrderingSemantics
{
    /// <summary>No equivalence with canonical text ordering is asserted.</summary>
    Unspecified = 0,

    /// <summary>
    /// The declared collation and constrained physical text domain together preserve canonical ordinal ordering.
    /// </summary>
    Ordinal = 1
}

/// <summary>
/// Trusted physical-domain evidence that PostgreSQL bytewise text ordering is equivalent to canonical .NET
/// UTF-16 ordinal ordering for every persisted value.
/// </summary>
public sealed record PostgresRelationQueryTextOrderingDomainEvidence
{
    /// <summary>
    /// Versioned proof strategy restricting values to Unicode U+0001 through U+007F under PostgreSQL <c>C</c>
    /// collation, where UTF-8 byte order and UTF-16 code-unit order are equivalent. U+0000 is excluded because
    /// PostgreSQL <c>text</c> cannot represent it.
    /// </summary>
    public const string CanonicalAsciiStrategy =
        "postgres/text-order-domain/ascii-c-utf8-byte-to-utf16-ordinal/v1";

    /// <summary>Creates exact constrained text-ordering-domain evidence.</summary>
    /// <param name="validatedConstraintName">
    /// Trusted, validated PostgreSQL check constraint restricting every stored character to U+0001 through U+007F.
    /// </param>
    /// <param name="authority">Stable authority that validated the physical constraint and proof.</param>
    /// <param name="strategy">
    /// Versioned equivalence strategy; only <see cref="CanonicalAsciiStrategy"/> is supported.
    /// </param>
    /// <exception cref="ArgumentNullException">A string parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A string is empty, the constraint identifier is invalid, or the strategy is unsupported.
    /// </exception>
    public PostgresRelationQueryTextOrderingDomainEvidence(
        string validatedConstraintName,
        string authority,
        string strategy = CanonicalAsciiStrategy)
    {
        if (!string.Equals(strategy, CanonicalAsciiStrategy, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported PostgreSQL text-ordering-domain strategy '{strategy}'.",
                nameof(strategy));
        }
        ValidatedConstraintName = PostgresRelationQueryStorageBinding.RequireIdentifier(
            validatedConstraintName,
            nameof(validatedConstraintName));
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        Strategy = CanonicalAsciiStrategy;
    }

    /// <summary>Trusted validated ASCII-domain check-constraint name.</summary>
    public string ValidatedConstraintName { get; }

    /// <summary>Stable evidence authority.</summary>
    public string Authority { get; }

    /// <summary>Versioned text-ordering-domain equivalence strategy.</summary>
    public string Strategy { get; }

    /// <summary>Determines whether one runtime string satisfies this persisted ordering-domain strategy.</summary>
    /// <param name="value">Runtime text value to validate.</param>
    /// <returns>
    /// <see langword="true"/> when every character is in the strategy's U+0001 through U+007F domain; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public bool IsSatisfiedBy(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var character in value)
        {
            if (character is '\0' or > '\u007f')
                return false;
        }
        return true;
    }
}

/// <summary>Explicit collation evidence for a physical PostgreSQL text column.</summary>
public sealed record PostgresRelationQueryTextSemantics
{
    /// <summary>Creates PostgreSQL text-semantic evidence.</summary>
    /// <param name="collation">Exact PostgreSQL collation name used by generated comparisons and ordering.</param>
    /// <param name="equality">Equality semantics proven by the collation.</param>
    /// <param name="ordering">Ordering semantics proven by the collation and constrained domain.</param>
    /// <param name="orderingDomain">
    /// Persisted constrained-domain evidence required for canonical ordinal ordering, or <see langword="null"/>
    /// when ordering is unspecified.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="collation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="collation"/> is empty, contains a null character, or is schema-qualified; ordering evidence
    /// is absent or inconsistent; or the ordering strategy is paired with a collation other than <c>C</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="equality"/> or <paramref name="ordering"/> is unsupported.</exception>
    public PostgresRelationQueryTextSemantics(
        string collation,
        PostgresRelationQueryTextEqualitySemantics equality,
        PostgresRelationQueryTextOrderingSemantics ordering = PostgresRelationQueryTextOrderingSemantics.Unspecified,
        PostgresRelationQueryTextOrderingDomainEvidence? orderingDomain = null)
    {
        Collation = PostgresRelationQueryStorageBinding.RequireIdentifier(collation, nameof(collation));
        if (Collation.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "PostgreSQL text semantics currently require one unqualified collation identifier.",
                nameof(collation));
        }
        if (!Enum.IsDefined(equality))
            throw new ArgumentOutOfRangeException(nameof(equality), equality, "Unsupported PostgreSQL text-equality semantics.");
        if (!Enum.IsDefined(ordering))
            throw new ArgumentOutOfRangeException(nameof(ordering), ordering, "Unsupported PostgreSQL text-ordering semantics.");
        if (ordering == PostgresRelationQueryTextOrderingSemantics.Unspecified && orderingDomain is not null)
        {
            throw new ArgumentException(
                "Text ordering-domain evidence requires an explicit ordering semantic.",
                nameof(orderingDomain));
        }
        if (ordering == PostgresRelationQueryTextOrderingSemantics.Ordinal)
        {
            if (orderingDomain is null)
            {
                throw new ArgumentException(
                    "Canonical ordinal text ordering requires constrained physical-domain evidence.",
                    nameof(orderingDomain));
            }
            if (!string.Equals(Collation, "C", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Text ordering-domain strategy '{orderingDomain.Strategy}' requires PostgreSQL C collation.",
                    nameof(collation));
            }
        }
        Equality = equality;
        Ordering = ordering;
        OrderingDomain = orderingDomain;
    }

    /// <summary>Exact PostgreSQL collation name.</summary>
    public string Collation { get; }

    /// <summary>Attested text-equality semantics.</summary>
    public PostgresRelationQueryTextEqualitySemantics Equality { get; }

    /// <summary>Attested text-ordering semantics.</summary>
    public PostgresRelationQueryTextOrderingSemantics Ordering { get; }

    /// <summary>Persisted constrained-domain evidence completing ordinal ordering equivalence.</summary>
    public PostgresRelationQueryTextOrderingDomainEvidence? OrderingDomain { get; }
}

/// <summary>Ordering evidence attached to one physical PostgreSQL field.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostgresRelationQueryOrderingCapability
{
    /// <summary>No canonical ordering guarantee is asserted.</summary>
    None = 0,

    /// <summary>Physical ordering is exactly equivalent to canonical ordering.</summary>
    Exact = 1,

    /// <summary>The physical column is a stable unique final ordering key.</summary>
    StableUnique = 2
}

/// <summary>Trusted physical-domain evidence that a PostgreSQL <c>numeric</c> column is a canonical CLR decimal.</summary>
public sealed record PostgresRelationQueryNumericDomainEvidence
{
    /// <summary>Versioned strategy for finite, typmod-constrained CLR-decimal equivalence.</summary>
    public const string CanonicalStrategy = "postgres/numeric-domain/finite-clr-decimal/v1";

    /// <summary>Creates exact numeric-domain evidence.</summary>
    /// <param name="precision">PostgreSQL numeric precision, from 1 through 28.</param>
    /// <param name="scale">PostgreSQL numeric scale, from 0 through <paramref name="precision"/>.</param>
    /// <param name="validatedConstraintName">Trusted constraint excluding special and out-of-domain values.</param>
    /// <param name="authority">Stable authority that validated the physical evidence.</param>
    /// <param name="strategy">Versioned equivalence strategy; only <see cref="CanonicalStrategy"/> is supported.</param>
    /// <exception cref="ArgumentOutOfRangeException">Precision or scale is outside the exact CLR-decimal boundary.</exception>
    /// <exception cref="ArgumentNullException">A string parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A string is empty, an identifier is invalid, or the strategy is unsupported.</exception>
    public PostgresRelationQueryNumericDomainEvidence(
        int precision,
        int scale,
        string validatedConstraintName,
        string authority,
        string strategy = CanonicalStrategy)
    {
        if (precision is < 1 or > 28)
            throw new ArgumentOutOfRangeException(nameof(precision), precision, "Exact CLR-decimal precision must be from 1 through 28.");
        if (scale < 0 || scale > precision)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Exact CLR-decimal scale must be nonnegative and no greater than precision.");
        if (!string.Equals(strategy, CanonicalStrategy, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported PostgreSQL numeric-domain strategy '{strategy}'.", nameof(strategy));
        Precision = precision;
        Scale = scale;
        ValidatedConstraintName = PostgresRelationQueryStorageBinding.RequireIdentifier(
            validatedConstraintName,
            nameof(validatedConstraintName));
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        Strategy = CanonicalStrategy;
    }

    /// <summary>PostgreSQL numeric precision.</summary>
    public int Precision { get; }

    /// <summary>PostgreSQL numeric scale.</summary>
    public int Scale { get; }

    /// <summary>Trusted finite/range constraint name.</summary>
    public string ValidatedConstraintName { get; }

    /// <summary>Stable evidence authority.</summary>
    public string Authority { get; }

    /// <summary>Versioned domain-equivalence strategy.</summary>
    public string Strategy { get; }
}

/// <summary>Exact decimal aggregate behaviors attested for one plan-affine numeric input.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostgresRelationQueryDecimalAggregateGuarantee
{
    /// <summary>No aggregate equivalence is attested.</summary>
    None = 0,

    /// <summary>Every SUM intermediate and result remains exactly representable as canonical decimal.</summary>
    SumIntermediateRange = 1,

    /// <summary>PostgreSQL AVG division and rounding is exactly equivalent to canonical decimal evaluation.</summary>
    AverageRounding = 2
}

/// <summary>Trusted, versioned strategy evidence for exact decimal SUM or AVG realization.</summary>
public sealed record PostgresRelationQueryDecimalAggregateAttestation
{
    /// <summary>Versioned native PostgreSQL aggregate-equivalence strategy.</summary>
    public const string CanonicalStrategy = "postgres/numeric-aggregate/canonical-decimal/v1";

    /// <summary>Creates query-domain aggregate evidence.</summary>
    /// <param name="guarantees">Exact aggregate behaviors proven for the bound input domain.</param>
    /// <param name="domainEvidence">Stable query-domain proof, constraint, or validated analysis identity.</param>
    /// <param name="authority">Stable authority that supplied the proof.</param>
    /// <param name="strategy">Versioned strategy; only <see cref="CanonicalStrategy"/> is supported.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="guarantees"/> is empty or unsupported.</exception>
    /// <exception cref="ArgumentNullException">A string parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A string is empty or the strategy is unsupported.</exception>
    public PostgresRelationQueryDecimalAggregateAttestation(
        PostgresRelationQueryDecimalAggregateGuarantee guarantees,
        string domainEvidence,
        string authority,
        string strategy = CanonicalStrategy)
    {
        const PostgresRelationQueryDecimalAggregateGuarantee all =
            PostgresRelationQueryDecimalAggregateGuarantee.SumIntermediateRange
            | PostgresRelationQueryDecimalAggregateGuarantee.AverageRounding;
        if (guarantees == PostgresRelationQueryDecimalAggregateGuarantee.None || (guarantees & ~all) != 0)
            throw new ArgumentOutOfRangeException(nameof(guarantees), guarantees, "Aggregate evidence must select supported exact guarantees.");
        if (!string.Equals(strategy, CanonicalStrategy, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported PostgreSQL decimal-aggregate strategy '{strategy}'.", nameof(strategy));
        Guarantees = guarantees;
        DomainEvidence = Guard.RequireNotNullOrWhiteSpace(domainEvidence);
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        Strategy = CanonicalStrategy;
    }

    /// <summary>Exact aggregate behaviors proven for the bound input domain.</summary>
    public PostgresRelationQueryDecimalAggregateGuarantee Guarantees { get; }

    /// <summary>Stable proof or validated-analysis identity.</summary>
    public string DomainEvidence { get; }

    /// <summary>Stable evidence authority.</summary>
    public string Authority { get; }

    /// <summary>Versioned aggregate-equivalence strategy.</summary>
    public string Strategy { get; }
}

/// <summary>Trusted physical evidence that a PostgreSQL temporal column is exactly within its canonical CLR domain.</summary>
public sealed record PostgresRelationQueryTemporalDomainEvidence
{
    /// <summary>Versioned finite canonical CLR temporal-domain strategy.</summary>
    public const string CanonicalStrategy = "postgres/temporal-domain/canonical-clr/v1";

    /// <summary>Creates exact physical temporal-domain evidence.</summary>
    /// <param name="validatedConstraintName">
    /// Trusted constraint excluding infinity and values outside the corresponding CLR range, and excluding
    /// sub-microsecond values for timestamp columns.
    /// </param>
    /// <param name="authority">Stable authority that validated the evidence.</param>
    /// <param name="strategy">Versioned strategy; only <see cref="CanonicalStrategy"/> is supported.</param>
    /// <exception cref="ArgumentNullException">A string parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A string is empty, an identifier is invalid, or the strategy is unsupported.</exception>
    public PostgresRelationQueryTemporalDomainEvidence(
        string validatedConstraintName,
        string authority,
        string strategy = CanonicalStrategy)
    {
        if (!string.Equals(strategy, CanonicalStrategy, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported PostgreSQL temporal-domain strategy '{strategy}'.", nameof(strategy));
        ValidatedConstraintName = PostgresRelationQueryStorageBinding.RequireIdentifier(
            validatedConstraintName,
            nameof(validatedConstraintName));
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        Strategy = CanonicalStrategy;
    }

    /// <summary>Trusted finite/range/precision constraint name.</summary>
    public string ValidatedConstraintName { get; }

    /// <summary>Stable evidence authority.</summary>
    public string Authority { get; }

    /// <summary>Versioned temporal-equivalence strategy.</summary>
    public string Strategy { get; }
}

/// <summary>One exact demanded semantic field bound to a physical PostgreSQL column.</summary>
public sealed record PostgresRelationQueryFieldBinding
{
    /// <summary>Creates a demanded-field column binding.</summary>
    /// <param name="input">Exact compiled field-input identity.</param>
    /// <param name="semanticPath">Canonical semantic field path.</param>
    /// <param name="columnName">Physical PostgreSQL column name.</param>
    /// <param name="scalarType">Physical scalar type.</param>
    /// <param name="missingValueEncoding">Physical representation of semantic missing.</param>
    /// <param name="nullValueEncoding">Physical representation of semantic explicit null.</param>
    /// <param name="textSemantics">Text collation evidence, required only for text operations needing it.</param>
    /// <param name="ordering">Exact and stable-unique ordering evidence.</param>
    /// <param name="numericDomain">Finite CLR-decimal physical-domain evidence for numeric columns.</param>
    /// <param name="decimalAggregates">Optional exact SUM/AVG evidence for this plan-affine numeric input.</param>
    /// <param name="temporalDomain">Finite CLR-range evidence for date and timestamp columns, including microsecond alignment for timestamps.</param>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or path is default; a column is invalid; or null and missing encodings conflict.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value or ordering flag is unsupported.</exception>
    public PostgresRelationQueryFieldBinding(
        RelationQueryInputId input,
        FieldPath semanticPath,
        string columnName,
        PostgresRelationQueryScalarType scalarType,
        PostgresRelationQueryMissingValueEncoding missingValueEncoding,
        PostgresRelationQueryNullValueEncoding nullValueEncoding,
        PostgresRelationQueryTextSemantics? textSemantics = null,
        PostgresRelationQueryOrderingCapability ordering = PostgresRelationQueryOrderingCapability.None,
        PostgresRelationQueryNumericDomainEvidence? numericDomain = null,
        PostgresRelationQueryDecimalAggregateAttestation? decimalAggregates = null,
        PostgresRelationQueryTemporalDomainEvidence? temporalDomain = null)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("A PostgreSQL field binding requires a compiled input identity.", nameof(input));
        if (semanticPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A PostgreSQL field binding requires a semantic path.", nameof(semanticPath));
        RequireValueSemantics(scalarType, missingValueEncoding, nullValueEncoding, textSemantics, ordering);
        if (scalarType != PostgresRelationQueryScalarType.Numeric
            && (numericDomain is not null || decimalAggregates is not null))
        {
            throw new ArgumentException("Numeric-domain and aggregate evidence applies only to numeric columns.", nameof(numericDomain));
        }
        if (decimalAggregates is not null && numericDomain is null)
            throw new ArgumentException("Decimal aggregate evidence requires exact numeric-domain evidence.", nameof(decimalAggregates));
        if (scalarType is not (PostgresRelationQueryScalarType.Date
                or PostgresRelationQueryScalarType.Timestamp
                or PostgresRelationQueryScalarType.TimestampWithTimeZone)
            && temporalDomain is not null)
        {
            throw new ArgumentException("Temporal-domain evidence applies only to PostgreSQL date or timestamp columns.", nameof(temporalDomain));
        }
        Input = input;
        SemanticPath = semanticPath;
        ColumnName = PostgresRelationQueryStorageBinding.RequireIdentifier(columnName, nameof(columnName));
        ScalarType = scalarType;
        MissingValueEncoding = missingValueEncoding;
        NullValueEncoding = nullValueEncoding;
        TextSemantics = textSemantics;
        Ordering = ordering;
        NumericDomain = numericDomain;
        DecimalAggregates = decimalAggregates;
        TemporalDomain = temporalDomain;
    }

    /// <summary>Exact compiled field-input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Canonical semantic field path.</summary>
    public FieldPath SemanticPath { get; }

    /// <summary>Physical PostgreSQL column name.</summary>
    public string ColumnName { get; }

    /// <summary>Physical scalar type.</summary>
    public PostgresRelationQueryScalarType ScalarType { get; }

    /// <summary>Physical representation of semantic missing.</summary>
    public PostgresRelationQueryMissingValueEncoding MissingValueEncoding { get; }

    /// <summary>Physical representation of semantic explicit null.</summary>
    public PostgresRelationQueryNullValueEncoding NullValueEncoding { get; }

    /// <summary>Text collation evidence, or <see langword="null"/> for non-text or unattested text.</summary>
    public PostgresRelationQueryTextSemantics? TextSemantics { get; }

    /// <summary>Exact and stable-unique ordering evidence.</summary>
    public PostgresRelationQueryOrderingCapability Ordering { get; }

    /// <summary>Finite CLR-decimal domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryNumericDomainEvidence? NumericDomain { get; }

    /// <summary>Exact plan-affine SUM/AVG evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryDecimalAggregateAttestation? DecimalAggregates { get; }

    /// <summary>Finite canonical CLR temporal-domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTemporalDomainEvidence? TemporalDomain { get; }

    internal static void RequireValueSemantics(
        PostgresRelationQueryScalarType scalarType,
        PostgresRelationQueryMissingValueEncoding missing,
        PostgresRelationQueryNullValueEncoding @null,
        PostgresRelationQueryTextSemantics? text,
        PostgresRelationQueryOrderingCapability ordering)
    {
        if (!Enum.IsDefined(scalarType))
            throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Unsupported PostgreSQL scalar type.");
        if (!Enum.IsDefined(missing))
            throw new ArgumentOutOfRangeException(nameof(missing), missing, "Unsupported PostgreSQL missing-value encoding.");
        if (!Enum.IsDefined(@null))
            throw new ArgumentOutOfRangeException(nameof(@null), @null, "Unsupported PostgreSQL null-value encoding.");
        const PostgresRelationQueryOrderingCapability all =
            PostgresRelationQueryOrderingCapability.Exact | PostgresRelationQueryOrderingCapability.StableUnique;
        if ((ordering & ~all) != 0)
            throw new ArgumentOutOfRangeException(nameof(ordering), ordering, "Unsupported PostgreSQL ordering capability.");
        if (missing == PostgresRelationQueryMissingValueEncoding.SqlNull
            && @null == PostgresRelationQueryNullValueEncoding.SqlNull)
        {
            throw new ArgumentException("One SQL NULL cannot distinguish semantic missing from explicit null.", nameof(missing));
        }
        if (scalarType != PostgresRelationQueryScalarType.Text && text is not null)
            throw new ArgumentException("PostgreSQL text semantics apply only to text columns.", nameof(text));
        if (scalarType == PostgresRelationQueryScalarType.Text
            && ordering.HasFlag(PostgresRelationQueryOrderingCapability.Exact)
            && (text?.Ordering != PostgresRelationQueryTextOrderingSemantics.Ordinal
                || text.OrderingDomain is null))
        {
            throw new ArgumentException(
                "Exact text ordering requires ordinal collation and constrained-domain evidence.",
                nameof(ordering));
        }
    }
}

/// <summary>Physical identity-column evidence for one PostgreSQL table binding.</summary>
public sealed record PostgresRelationQueryIdentityBinding
{
    /// <summary>Creates identity-column evidence.</summary>
    /// <param name="semanticPath">Semantic field supplying observation identity.</param>
    /// <param name="columnName">Physical unique, non-null PostgreSQL column.</param>
    /// <param name="scalarType">Physical identity scalar type.</param>
    /// <param name="textSemantics">Text equality evidence, or <see langword="null"/> for non-text identity.</param>
    /// <param name="numericDomain">Finite CLR-decimal physical-domain evidence for a numeric identity.</param>
    /// <param name="temporalDomain">Finite canonical CLR temporal-domain evidence for a date or timestamp identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A path or column is invalid, or exact text, numeric, or temporal evidence conflicts with the scalar type.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scalarType"/> is unsupported.</exception>
    public PostgresRelationQueryIdentityBinding(
        FieldPath semanticPath,
        string columnName,
        PostgresRelationQueryScalarType scalarType,
        PostgresRelationQueryTextSemantics? textSemantics = null,
        PostgresRelationQueryNumericDomainEvidence? numericDomain = null,
        PostgresRelationQueryTemporalDomainEvidence? temporalDomain = null)
    {
        if (semanticPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A PostgreSQL identity binding requires a semantic path.", nameof(semanticPath));
        PostgresRelationQueryFieldBinding.RequireValueSemantics(
            scalarType,
            PostgresRelationQueryMissingValueEncoding.Prohibited,
            PostgresRelationQueryNullValueEncoding.Prohibited,
            textSemantics,
            PostgresRelationQueryOrderingCapability.None);
        RequireKeyDomainEvidence(scalarType, numericDomain, temporalDomain);
        SemanticPath = semanticPath;
        ColumnName = PostgresRelationQueryStorageBinding.RequireIdentifier(columnName, nameof(columnName));
        ScalarType = scalarType;
        TextSemantics = textSemantics;
        NumericDomain = numericDomain;
        TemporalDomain = temporalDomain;
    }

    /// <summary>Semantic identity field path.</summary>
    public FieldPath SemanticPath { get; }

    /// <summary>Physical unique, non-null PostgreSQL column.</summary>
    public string ColumnName { get; }

    /// <summary>Physical identity scalar type.</summary>
    public PostgresRelationQueryScalarType ScalarType { get; }

    /// <summary>Text equality evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTextSemantics? TextSemantics { get; }

    /// <summary>Finite CLR-decimal domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryNumericDomainEvidence? NumericDomain { get; }

    /// <summary>Finite canonical CLR temporal-domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTemporalDomainEvidence? TemporalDomain { get; }

    internal static void RequireKeyDomainEvidence(
        PostgresRelationQueryScalarType scalarType,
        PostgresRelationQueryNumericDomainEvidence? numericDomain,
        PostgresRelationQueryTemporalDomainEvidence? temporalDomain)
    {
        if (scalarType != PostgresRelationQueryScalarType.Numeric && numericDomain is not null)
            throw new ArgumentException("Numeric-domain evidence applies only to numeric relationship keys.", nameof(numericDomain));
        if (scalarType == PostgresRelationQueryScalarType.Numeric && numericDomain is null)
            throw new ArgumentException("A numeric relationship key requires exact finite CLR-decimal domain evidence.", nameof(numericDomain));
        if (scalarType is not (PostgresRelationQueryScalarType.Date
                or PostgresRelationQueryScalarType.Timestamp
                or PostgresRelationQueryScalarType.TimestampWithTimeZone)
            && temporalDomain is not null)
        {
            throw new ArgumentException("Temporal-domain evidence applies only to date or timestamp relationship keys.", nameof(temporalDomain));
        }
        if (scalarType is PostgresRelationQueryScalarType.Date
                or PostgresRelationQueryScalarType.Timestamp
                or PostgresRelationQueryScalarType.TimestampWithTimeZone
            && temporalDomain is null)
        {
            throw new ArgumentException("A temporal relationship key requires exact finite canonical CLR domain evidence.", nameof(temporalDomain));
        }
    }
}

/// <summary>Exact logical-partition selector bound to one non-null PostgreSQL table column.</summary>
public sealed record PostgresRelationQueryPartitionBinding
{
    /// <summary>Creates physical partition-column evidence.</summary>
    /// <param name="sourceSelector">Exact adapter selector retained by the canonical placement.</param>
    /// <param name="semanticPath">Semantic scalar field represented by the partition column.</param>
    /// <param name="columnName">Physical non-null PostgreSQL partition column.</param>
    /// <param name="scalarType">Physical partition scalar type.</param>
    /// <param name="textSemantics">Ordinal text equality evidence, or <see langword="null"/> for non-text values.</param>
    /// <param name="numericDomain">Finite CLR-decimal physical-domain evidence for a numeric partition.</param>
    /// <param name="temporalDomain">Finite canonical CLR temporal-domain evidence for a temporal partition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> or <paramref name="columnName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A selector, path, column, or exact scalar-domain or text-equality requirement is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scalarType"/> is unsupported.</exception>
    public PostgresRelationQueryPartitionBinding(
        string sourceSelector,
        FieldPath semanticPath,
        string columnName,
        PostgresRelationQueryScalarType scalarType,
        PostgresRelationQueryTextSemantics? textSemantics = null,
        PostgresRelationQueryNumericDomainEvidence? numericDomain = null,
        PostgresRelationQueryTemporalDomainEvidence? temporalDomain = null)
    {
        if (semanticPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A PostgreSQL partition binding requires a semantic path.", nameof(semanticPath));
        PostgresRelationQueryFieldBinding.RequireValueSemantics(
            scalarType,
            PostgresRelationQueryMissingValueEncoding.Prohibited,
            PostgresRelationQueryNullValueEncoding.Prohibited,
            textSemantics,
            PostgresRelationQueryOrderingCapability.None);
        PostgresRelationQueryIdentityBinding.RequireKeyDomainEvidence(scalarType, numericDomain, temporalDomain);
        if (scalarType == PostgresRelationQueryScalarType.Text
            && textSemantics?.Equality != PostgresRelationQueryTextEqualitySemantics.Ordinal)
        {
            throw new ArgumentException(
                "A text partition requires exact ordinal equality evidence.",
                nameof(textSemantics));
        }

        SourceSelector = Guard.RequireNotNullOrWhiteSpace(sourceSelector);
        SemanticPath = semanticPath;
        ColumnName = PostgresRelationQueryStorageBinding.RequireIdentifier(columnName, nameof(columnName));
        ScalarType = scalarType;
        TextSemantics = textSemantics;
        NumericDomain = numericDomain;
        TemporalDomain = temporalDomain;
    }

    /// <summary>Exact adapter selector retained by the canonical placement.</summary>
    public string SourceSelector { get; }

    /// <summary>Semantic scalar field represented by the partition column.</summary>
    public FieldPath SemanticPath { get; }

    /// <summary>Physical non-null PostgreSQL partition column.</summary>
    public string ColumnName { get; }

    /// <summary>Physical partition scalar type.</summary>
    public PostgresRelationQueryScalarType ScalarType { get; }

    /// <summary>Ordinal text equality evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTextSemantics? TextSemantics { get; }

    /// <summary>Finite CLR-decimal domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryNumericDomainEvidence? NumericDomain { get; }

    /// <summary>Finite canonical CLR temporal-domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTemporalDomainEvidence? TemporalDomain { get; }
}

/// <summary>Physical source-reference column used to correlate one semantic relationship traversal.</summary>
public sealed record PostgresRelationQueryRelationshipReferenceBinding
{
    /// <summary>Creates a relationship-reference column binding.</summary>
    /// <param name="input">Exact compiled traversal-input identity.</param>
    /// <param name="semanticPath">Canonical source-reference field path.</param>
    /// <param name="columnName">Physical PostgreSQL reference column.</param>
    /// <param name="scalarType">Physical reference scalar type.</param>
    /// <param name="uniqueness">Global source-reference uniqueness guarantee.</param>
    /// <param name="missingValueEncoding">Physical representation of semantic missing.</param>
    /// <param name="nullValueEncoding">Physical representation of semantic explicit null.</param>
    /// <param name="textSemantics">Text equality evidence, or <see langword="null"/>.</param>
    /// <param name="numericDomain">Finite CLR-decimal physical-domain evidence for a numeric reference.</param>
    /// <param name="temporalDomain">Finite canonical CLR temporal-domain evidence for a date or timestamp reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, path, column, or value-semantic combination is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is unsupported.</exception>
    public PostgresRelationQueryRelationshipReferenceBinding(
        RelationQueryInputId input,
        FieldPath semanticPath,
        string columnName,
        PostgresRelationQueryScalarType scalarType,
        SourceReferenceUniqueness uniqueness,
        PostgresRelationQueryMissingValueEncoding missingValueEncoding,
        PostgresRelationQueryNullValueEncoding nullValueEncoding,
        PostgresRelationQueryTextSemantics? textSemantics = null,
        PostgresRelationQueryNumericDomainEvidence? numericDomain = null,
        PostgresRelationQueryTemporalDomainEvidence? temporalDomain = null)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("A PostgreSQL relationship reference requires a traversal input.", nameof(input));
        if (semanticPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A PostgreSQL relationship reference requires a semantic path.", nameof(semanticPath));
        if (!Enum.IsDefined(uniqueness))
            throw new ArgumentOutOfRangeException(nameof(uniqueness), uniqueness, "Unsupported source-reference uniqueness.");
        PostgresRelationQueryFieldBinding.RequireValueSemantics(
            scalarType,
            missingValueEncoding,
            nullValueEncoding,
            textSemantics,
            PostgresRelationQueryOrderingCapability.None);
        PostgresRelationQueryIdentityBinding.RequireKeyDomainEvidence(scalarType, numericDomain, temporalDomain);
        Input = input;
        SemanticPath = semanticPath;
        ColumnName = PostgresRelationQueryStorageBinding.RequireIdentifier(columnName, nameof(columnName));
        ScalarType = scalarType;
        Uniqueness = uniqueness;
        MissingValueEncoding = missingValueEncoding;
        NullValueEncoding = nullValueEncoding;
        TextSemantics = textSemantics;
        NumericDomain = numericDomain;
        TemporalDomain = temporalDomain;
    }

    /// <summary>Exact compiled traversal-input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Canonical relationship-reference path.</summary>
    public FieldPath SemanticPath { get; }

    /// <summary>Physical PostgreSQL reference column.</summary>
    public string ColumnName { get; }

    /// <summary>Physical reference scalar type.</summary>
    public PostgresRelationQueryScalarType ScalarType { get; }

    /// <summary>Global source-reference uniqueness guarantee.</summary>
    public SourceReferenceUniqueness Uniqueness { get; }

    /// <summary>Physical representation of semantic missing.</summary>
    public PostgresRelationQueryMissingValueEncoding MissingValueEncoding { get; }

    /// <summary>Physical representation of semantic explicit null.</summary>
    public PostgresRelationQueryNullValueEncoding NullValueEncoding { get; }

    /// <summary>Text equality evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTextSemantics? TextSemantics { get; }

    /// <summary>Finite CLR-decimal domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryNumericDomainEvidence? NumericDomain { get; }

    /// <summary>Finite canonical CLR temporal-domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTemporalDomainEvidence? TemporalDomain { get; }
}

/// <summary>
/// Trusted PostgreSQL check-constraint evidence that one exact semantic interval is valid whenever both endpoints
/// are bounded.
/// </summary>
public sealed record PostgresRelationQueryIntervalValidityBinding
{
    /// <summary>Creates exact persisted interval-validity evidence.</summary>
    /// <param name="lowerInput">Compiled field input supplying the lower endpoint.</param>
    /// <param name="lowerPath">Exact semantic path of the lower endpoint.</param>
    /// <param name="lowerNullBehavior">Canonical meaning of a null lower endpoint.</param>
    /// <param name="upperInput">Compiled field input supplying the upper endpoint.</param>
    /// <param name="upperPath">Exact semantic path of the upper endpoint.</param>
    /// <param name="upperNullBehavior">Canonical meaning of a null upper endpoint.</param>
    /// <param name="validatedCheckConstraintName">
    /// Name of a trusted, validated PostgreSQL check constraint proving <c>lower &lt;= upper</c> whenever both
    /// endpoints are non-null.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="validatedCheckConstraintName"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An input, path, or constraint name is invalid, or both endpoints identify the same field.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A null behavior is unsupported.</exception>
    public PostgresRelationQueryIntervalValidityBinding(
        RelationQueryInputId lowerInput,
        FieldPath lowerPath,
        TemporalNullBoundBehavior lowerNullBehavior,
        RelationQueryInputId upperInput,
        FieldPath upperPath,
        TemporalNullBoundBehavior upperNullBehavior,
        string validatedCheckConstraintName)
    {
        if (string.IsNullOrWhiteSpace(lowerInput.Value))
            throw new ArgumentException("Interval validity requires a lower field input.", nameof(lowerInput));
        if (string.IsNullOrWhiteSpace(upperInput.Value))
            throw new ArgumentException("Interval validity requires an upper field input.", nameof(upperInput));
        if (lowerInput == upperInput)
            throw new ArgumentException("Interval validity requires distinct endpoint fields.", nameof(upperInput));
        if (lowerPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("Interval validity requires a lower semantic path.", nameof(lowerPath));
        if (upperPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("Interval validity requires an upper semantic path.", nameof(upperPath));
        if (!Enum.IsDefined(lowerNullBehavior))
            throw new ArgumentOutOfRangeException(nameof(lowerNullBehavior), lowerNullBehavior, "Unsupported lower null behavior.");
        if (!Enum.IsDefined(upperNullBehavior))
            throw new ArgumentOutOfRangeException(nameof(upperNullBehavior), upperNullBehavior, "Unsupported upper null behavior.");

        LowerInput = lowerInput;
        LowerPath = lowerPath;
        LowerNullBehavior = lowerNullBehavior;
        UpperInput = upperInput;
        UpperPath = upperPath;
        UpperNullBehavior = upperNullBehavior;
        ValidatedCheckConstraintName = PostgresRelationQueryStorageBinding.RequireIdentifier(
            validatedCheckConstraintName,
            nameof(validatedCheckConstraintName));
    }

    /// <summary>Compiled field input supplying the lower endpoint.</summary>
    public RelationQueryInputId LowerInput { get; }

    /// <summary>Exact semantic path of the lower endpoint.</summary>
    public FieldPath LowerPath { get; }

    /// <summary>Canonical meaning of a null lower endpoint.</summary>
    public TemporalNullBoundBehavior LowerNullBehavior { get; }

    /// <summary>Compiled field input supplying the upper endpoint.</summary>
    public RelationQueryInputId UpperInput { get; }

    /// <summary>Exact semantic path of the upper endpoint.</summary>
    public FieldPath UpperPath { get; }

    /// <summary>Canonical meaning of a null upper endpoint.</summary>
    public TemporalNullBoundBehavior UpperNullBehavior { get; }

    /// <summary>
    /// Trusted, validated PostgreSQL check constraint proving bounded endpoints satisfy <c>lower &lt;= upper</c>.
    /// </summary>
    public string ValidatedCheckConstraintName { get; }
}

/// <summary>One exact placed semantic input bound to a PostgreSQL table.</summary>
public sealed record PostgresRelationQueryTableBinding
{
    /// <summary>Creates a normalized PostgreSQL table binding.</summary>
    /// <param name="source">Physical source instance that reaches the database.</param>
    /// <param name="placementBinding">Exact plan-scoped placement binding.</param>
    /// <param name="input">Exact compiled source or traversal input.</param>
    /// <param name="shape">Semantic shape stored by the table.</param>
    /// <param name="schemaName">Physical PostgreSQL schema name.</param>
    /// <param name="tableName">Physical PostgreSQL table or view name.</param>
    /// <param name="identity">Unique non-null observation identity column, or <see langword="null"/> when not demanded.</param>
    /// <param name="fields">Exact demanded field-column bindings.</param>
    /// <param name="relationshipReferences">Relationship correlation columns owned by this table.</param>
    /// <param name="intervalValidities">Trusted validity evidence for exact semantic interval endpoint pairs.</param>
    /// <param name="partition">Exact physical partition column, or <see langword="null"/> for an unpartitioned placement.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaName"/> or <paramref name="tableName"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity, name, or collection is invalid or repeated.</exception>
    public PostgresRelationQueryTableBinding(
        RelationQuerySourceInstanceId source,
        RelationQuerySourcePlacementBindingId placementBinding,
        RelationQueryInputId input,
        QualifiedShapeId shape,
        string schemaName,
        string tableName,
        PostgresRelationQueryIdentityBinding? identity,
        ImmutableArray<PostgresRelationQueryFieldBinding> fields,
        ImmutableArray<PostgresRelationQueryRelationshipReferenceBinding> relationshipReferences = default,
        ImmutableArray<PostgresRelationQueryIntervalValidityBinding> intervalValidities = default,
        PostgresRelationQueryPartitionBinding? partition = null)
    {
        if (string.IsNullOrWhiteSpace(source.Value) || string.IsNullOrWhiteSpace(placementBinding.Value)
            || string.IsNullOrWhiteSpace(input.Value))
        {
            throw new ArgumentException("A PostgreSQL table binding requires non-default source, placement, and input identities.", nameof(input));
        }
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A PostgreSQL table binding requires a graph-qualified shape.", nameof(shape));
        Source = source;
        PlacementBinding = placementBinding;
        Input = input;
        Shape = shape;
        SchemaName = PostgresRelationQueryStorageBinding.RequireIdentifier(schemaName, nameof(schemaName));
        TableName = PostgresRelationQueryStorageBinding.RequireIdentifier(tableName, nameof(tableName));
        Identity = identity;
        Fields = Normalize(fields, static field => field.Input.Value, nameof(fields));
        RelationshipReferences = Normalize(
            relationshipReferences,
            static reference => reference.Input.Value,
            nameof(relationshipReferences));
        IntervalValidities = Normalize(
            intervalValidities,
            static interval => $"{interval.LowerInput.Value}\n{interval.UpperInput.Value}",
            nameof(intervalValidities));
        Partition = partition;
        foreach (var interval in IntervalValidities)
        {
            var lower = Fields.SingleOrDefault(field => field.Input == interval.LowerInput);
            var upper = Fields.SingleOrDefault(field => field.Input == interval.UpperInput);
            if (lower is null || lower.SemanticPath != interval.LowerPath
                || upper is null || upper.SemanticPath != interval.UpperPath)
            {
                throw new ArgumentException(
                    "Interval validity endpoints must identify exact field bindings on the same table.",
                    nameof(intervalValidities));
            }
            if (lower.ScalarType != upper.ScalarType || lower.ScalarType is not (
                    PostgresRelationQueryScalarType.Date
                    or PostgresRelationQueryScalarType.Timestamp
                    or PostgresRelationQueryScalarType.TimestampWithTimeZone))
            {
                throw new ArgumentException(
                    "Interval validity endpoints must share one exact PostgreSQL temporal scalar type.",
                    nameof(intervalValidities));
            }
        }
    }

    /// <summary>Physical source instance that reaches the database.</summary>
    public RelationQuerySourceInstanceId Source { get; }

    /// <summary>Exact plan-scoped placement-binding identity.</summary>
    public RelationQuerySourcePlacementBindingId PlacementBinding { get; }

    /// <summary>Exact compiled source or traversal input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Semantic shape stored by the table.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Physical PostgreSQL schema name.</summary>
    public string SchemaName { get; }

    /// <summary>Physical PostgreSQL table or view name.</summary>
    public string TableName { get; }

    /// <summary>Observation identity column, or <see langword="null"/>.</summary>
    public PostgresRelationQueryIdentityBinding? Identity { get; }

    /// <summary>Exact demanded field-column bindings.</summary>
    public ImmutableArray<PostgresRelationQueryFieldBinding> Fields { get; }

    /// <summary>Relationship correlation columns owned by this table.</summary>
    public ImmutableArray<PostgresRelationQueryRelationshipReferenceBinding> RelationshipReferences { get; }

    /// <summary>Trusted check-constraint evidence for exact semantic interval endpoint pairs.</summary>
    public ImmutableArray<PostgresRelationQueryIntervalValidityBinding> IntervalValidities { get; }

    /// <summary>Exact physical partition column, or <see langword="null"/>.</summary>
    public PostgresRelationQueryPartitionBinding? Partition { get; }

    /// <summary>Resolves one exact demanded field binding.</summary>
    /// <param name="input">Compiled field-input identity.</param>
    /// <returns>The exact physical field binding.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="input"/> is not bound by this table.</exception>
    public PostgresRelationQueryFieldBinding ResolveField(RelationQueryInputId input) =>
        Fields.SingleOrDefault(field => field.Input == input)
        ?? throw new KeyNotFoundException($"PostgreSQL table binding '{PlacementBinding.Value}' has no field '{input.Value}'.");

    /// <summary>Resolves the source-reference binding for one traversal.</summary>
    /// <param name="input">Compiled traversal-input identity.</param>
    /// <returns>The exact physical relationship-reference binding.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="input"/> is not bound by this table.</exception>
    public PostgresRelationQueryRelationshipReferenceBinding ResolveRelationshipReference(RelationQueryInputId input) =>
        RelationshipReferences.SingleOrDefault(reference => reference.Input == input)
        ?? throw new KeyNotFoundException($"PostgreSQL table binding '{PlacementBinding.Value}' has no relationship reference '{input.Value}'.");

    /// <summary>Resolves exact interval-validity evidence by compiled endpoint fields.</summary>
    /// <param name="lowerInput">Compiled lower-endpoint field input.</param>
    /// <param name="upperInput">Compiled upper-endpoint field input.</param>
    /// <returns>The exact trusted interval-validity evidence.</returns>
    /// <exception cref="KeyNotFoundException">The endpoint pair has no validity attestation.</exception>
    public PostgresRelationQueryIntervalValidityBinding ResolveIntervalValidity(
        RelationQueryInputId lowerInput,
        RelationQueryInputId upperInput) =>
        IntervalValidities.SingleOrDefault(interval =>
            interval.LowerInput == lowerInput && interval.UpperInput == upperInput)
        ?? throw new KeyNotFoundException(
            $"PostgreSQL table binding '{PlacementBinding.Value}' has no interval validity for "
            + $"'{lowerInput.Value}' and '{upperInput.Value}'.");

    static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values, Func<T, string> key, string parameterName)
        where T : class
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static value => value is null))
            throw new ArgumentException("PostgreSQL table-binding collections cannot contain null entries.", parameterName);
        if (normalized.GroupBy(key, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException("PostgreSQL table-binding collection identities cannot repeat.", parameterName);
        return [.. normalized.OrderBy(key, StringComparer.Ordinal)];
    }
}

/// <summary>Immutable, versioned binding from one exact placement to co-located PostgreSQL tables.</summary>
public sealed class PostgresRelationQueryStorageBinding
{
    /// <summary>Current PostgreSQL relation/query storage-binding schema.</summary>
    public const string CurrentSchemaVersion = "cohesive.relations.postgres-binding/v3";

    /// <summary>Default deterministic convention set for table-column binding.</summary>
    public const string SemanticPathConventionSet = "cohesive.adapters.postgres.sql/semantic-path-conventions/v1";

    /// <summary>
    /// Fixed database semantics required by the canonical PostgreSQL compiler: UTF-8 server encoding and the standard 63-byte
    /// identifier boundary.
    /// </summary>
    public const string CanonicalDatabaseSemanticsProfile =
        "cohesive.adapters.postgres.sql/database-semantics/utf8-standard-identifiers/v1";

    /// <summary>Creates a normalized PostgreSQL storage binding and computes its fingerprint.</summary>
    /// <param name="id">Stable versioned binding identity.</param>
    /// <param name="database">Stable non-secret physical database identity.</param>
    /// <param name="target">Expected PostgreSQL target identity.</param>
    /// <param name="targetProfile">Expected target capability-profile identity.</param>
    /// <param name="tables">
    /// Table bindings for exact database-acquired source and traversal placements. The collection may be empty when
    /// every demanded value is supplied and the compiler emits a parameter-only PostgreSQL statement.
    /// </param>
    /// <param name="origin">Overall binding origin.</param>
    /// <param name="conventionSetVersion">Attributable convention-set identity.</param>
    /// <param name="configurationDecisions">Normalized effective configuration provenance.</param>
    /// <param name="compiledPlanFingerprint">Exact compiled-plan fingerprint, or <see langword="null"/> with placement fingerprint.</param>
    /// <param name="placementFingerprint">Exact source-placement fingerprint, or <see langword="null"/> with plan fingerprint.</param>
    /// <param name="ownedCollections">Decomposed owned-collection component tables keyed to root placements.</param>
    /// <exception cref="ArgumentException">An identity, collection, provenance fact, or affinity pair is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="origin"/> is unsupported.</exception>
    public PostgresRelationQueryStorageBinding(
        PostgresRelationQueryBindingId id,
        PostgresRelationQueryDatabaseId database,
        RelationQueryTargetId target,
        RelationQueryTargetProfileId targetProfile,
        ImmutableArray<PostgresRelationQueryTableBinding> tables,
        PostgresRelationQueryBindingOrigin origin = PostgresRelationQueryBindingOrigin.Explicit,
        string? conventionSetVersion = null,
        ImmutableArray<EffectiveConfigurationDecision> configurationDecisions = default,
        RelationQueryPlanComponentFingerprint? compiledPlanFingerprint = null,
        RelationQuerySourcePlacementFingerprint? placementFingerprint = null,
        ImmutableArray<PostgresRelationQueryOwnedCollectionBinding> ownedCollections = default)
        : this(
            CurrentSchemaVersion,
            fingerprint: null,
            CanonicalDatabaseSemanticsProfile,
            id,
            database,
            target,
            targetProfile,
            tables,
            origin,
            conventionSetVersion,
            configurationDecisions,
            compiledPlanFingerprint,
            placementFingerprint,
            ownedCollections)
    {
    }

    /// <summary>Rehydrates a persisted PostgreSQL storage binding and verifies its fingerprint.</summary>
    /// <param name="schemaVersion">Persisted schema version.</param>
    /// <param name="fingerprint">Persisted fingerprint to verify.</param>
    /// <param name="databaseSemanticsProfile">Persisted PostgreSQL database-semantics profile.</param>
    /// <param name="id">Stable versioned binding identity.</param>
    /// <param name="database">Stable non-secret physical database identity.</param>
    /// <param name="target">Expected PostgreSQL target identity.</param>
    /// <param name="targetProfile">Expected target capability-profile identity.</param>
    /// <param name="tables">
    /// Table bindings for exact database-acquired source and traversal placements. The collection may be empty for
    /// a parameter-only supplied-input realization.
    /// </param>
    /// <param name="origin">Overall binding origin.</param>
    /// <param name="conventionSetVersion">Attributable convention-set identity.</param>
    /// <param name="configurationDecisions">Normalized effective configuration provenance.</param>
    /// <param name="compiledPlanFingerprint">Exact compiled-plan fingerprint, or <see langword="null"/> with placement fingerprint.</param>
    /// <param name="placementFingerprint">Exact source-placement fingerprint, or <see langword="null"/> with plan fingerprint.</param>
    /// <param name="ownedCollections">Decomposed owned-collection component tables keyed to root placements.</param>
    /// <exception cref="ArgumentException">The schema, persisted fingerprint, identity, collection, provenance, or affinity is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="origin"/> is unsupported.</exception>
    [JsonConstructor]
    public PostgresRelationQueryStorageBinding(
        string schemaVersion,
        PostgresRelationQueryBindingFingerprint? fingerprint,
        string databaseSemanticsProfile,
        PostgresRelationQueryBindingId id,
        PostgresRelationQueryDatabaseId database,
        RelationQueryTargetId target,
        RelationQueryTargetProfileId targetProfile,
        ImmutableArray<PostgresRelationQueryTableBinding> tables,
        PostgresRelationQueryBindingOrigin origin,
        string? conventionSetVersion,
        ImmutableArray<EffectiveConfigurationDecision> configurationDecisions,
        RelationQueryPlanComponentFingerprint? compiledPlanFingerprint,
        RelationQuerySourcePlacementFingerprint? placementFingerprint,
        ImmutableArray<PostgresRelationQueryOwnedCollectionBinding> ownedCollections = default)
    {
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported PostgreSQL binding schema '{schemaVersion}'.", nameof(schemaVersion));
        if (!string.Equals(databaseSemanticsProfile, CanonicalDatabaseSemanticsProfile, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported PostgreSQL database-semantics profile '{databaseSemanticsProfile}'.",
                nameof(databaseSemanticsProfile));
        }
        if (string.IsNullOrWhiteSpace(id.Value) || string.IsNullOrWhiteSpace(database.Value)
            || string.IsNullOrWhiteSpace(target.Value) || string.IsNullOrWhiteSpace(targetProfile.Value))
        {
            throw new ArgumentException("A PostgreSQL storage binding requires non-default identities.", nameof(id));
        }
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported PostgreSQL binding origin.");
        if (origin == PostgresRelationQueryBindingOrigin.Convention && string.IsNullOrWhiteSpace(conventionSetVersion))
            throw new ArgumentException("A convention-origin PostgreSQL binding requires convention attribution.", nameof(conventionSetVersion));
        if (conventionSetVersion is not null && string.IsNullOrWhiteSpace(conventionSetVersion))
            throw new ArgumentException("A PostgreSQL convention-set identity cannot be empty.", nameof(conventionSetVersion));
        if ((compiledPlanFingerprint is null) != (placementFingerprint is null))
            throw new ArgumentException("PostgreSQL plan and placement affinity must be supplied together or both omitted.", nameof(compiledPlanFingerprint));

        var normalizedTables = tables.IsDefault ? [] : tables;
        if (normalizedTables.Any(static table => table is null))
            throw new ArgumentException("PostgreSQL table bindings cannot contain null entries.", nameof(tables));
        if (normalizedTables.GroupBy(static table => table.PlacementBinding).Any(static group => group.Count() > 1)
            || normalizedTables.GroupBy(static table => table.Input).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A PostgreSQL storage binding cannot repeat a placement or compiled input.", nameof(tables));
        }

        var normalizedOwnedCollections = ownedCollections.IsDefault ? [] : ownedCollections;
        if (normalizedOwnedCollections.Any(static collection => collection is null))
        {
            throw new ArgumentException(
                "PostgreSQL owned-collection bindings cannot contain null entries.",
                nameof(ownedCollections));
        }
        if (normalizedOwnedCollections.GroupBy(static collection => collection.Collection)
                .Any(static group => group.Count() > 1)
            || normalizedOwnedCollections.GroupBy(static collection => collection.CollectionInput)
                .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "A PostgreSQL storage binding cannot repeat an owned collection or collection input.",
                nameof(ownedCollections));
        }
        foreach (var collection in normalizedOwnedCollections)
        {
            if (!normalizedTables.Any(table => table.PlacementBinding == collection.RootPlacementBinding))
            {
                throw new ArgumentException(
                    $"Owned collection '{collection.Collection.Value}' references an unbound root placement.",
                    nameof(ownedCollections));
            }
        }

        var normalizedDecisions = configurationDecisions.IsDefault ? [] : configurationDecisions;
        if (normalizedDecisions.Any(static decision => decision is null)
            || normalizedDecisions.GroupBy(static decision => decision.Setting, StringComparer.Ordinal).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("PostgreSQL configuration decisions cannot contain null or repeated settings.", nameof(configurationDecisions));
        }
        if (origin == PostgresRelationQueryBindingOrigin.Convention
            && normalizedDecisions.Any(static decision => decision.Origin is
                EffectiveConfigurationOrigin.Explicit or EffectiveConfigurationOrigin.ScopedProfile))
        {
            throw new ArgumentException("A convention-origin binding cannot retain explicit or scoped decisions.", nameof(configurationDecisions));
        }

        SchemaVersion = CurrentSchemaVersion;
        DatabaseSemanticsProfile = CanonicalDatabaseSemanticsProfile;
        Id = id;
        Database = database;
        Target = target;
        TargetProfile = targetProfile;
        Tables = [.. normalizedTables.OrderBy(static table => table.PlacementBinding.Value, StringComparer.Ordinal)];
        OwnedCollections =
        [
            .. normalizedOwnedCollections.OrderBy(
                static collection => collection.Collection.Value,
                StringComparer.Ordinal)
        ];
        Origin = origin;
        ConventionSetVersion = conventionSetVersion;
        ConfigurationDecisions = [.. normalizedDecisions.OrderBy(static decision => decision.Setting, StringComparer.Ordinal)];
        CompiledPlanFingerprint = compiledPlanFingerprint;
        PlacementFingerprint = placementFingerprint;
        var computed = PostgresRelationQueryBindingFingerprinter.Compute(this);
        if (fingerprint is not null && !Equals(fingerprint, computed))
            throw new ArgumentException("Persisted PostgreSQL storage-binding fingerprint does not match normalized content.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Portable PostgreSQL storage-binding schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Required, fingerprinted PostgreSQL server-encoding and identifier semantics.</summary>
    public string DatabaseSemanticsProfile { get; }

    /// <summary>Stable versioned binding identity.</summary>
    public PostgresRelationQueryBindingId Id { get; }

    /// <summary>Stable non-secret physical database identity.</summary>
    public PostgresRelationQueryDatabaseId Database { get; }

    /// <summary>Expected PostgreSQL target identity.</summary>
    public RelationQueryTargetId Target { get; }

    /// <summary>Expected PostgreSQL capability-profile identity.</summary>
    public RelationQueryTargetProfileId TargetProfile { get; }

    /// <summary>Exact table bindings in placement-identity order.</summary>
    public ImmutableArray<PostgresRelationQueryTableBinding> Tables { get; }

    /// <summary>Decomposed owned-collection component tables in canonical collection-identity order.</summary>
    public ImmutableArray<PostgresRelationQueryOwnedCollectionBinding> OwnedCollections { get; }

    /// <summary>Overall effective binding origin.</summary>
    public PostgresRelationQueryBindingOrigin Origin { get; }

    /// <summary>Attributable convention-set identity, or <see langword="null"/>.</summary>
    public string? ConventionSetVersion { get; }

    /// <summary>Normalized effective configuration provenance.</summary>
    public ImmutableArray<EffectiveConfigurationDecision> ConfigurationDecisions { get; }

    /// <summary>Exact compiled-plan affinity, or <see langword="null"/> for an unverified low-level binding.</summary>
    public RelationQueryPlanComponentFingerprint? CompiledPlanFingerprint { get; }

    /// <summary>Exact source-placement affinity, or <see langword="null"/> for an unverified low-level binding.</summary>
    public RelationQuerySourcePlacementFingerprint? PlacementFingerprint { get; }

    /// <summary>Deterministic fingerprint of all normalized binding facts.</summary>
    public PostgresRelationQueryBindingFingerprint Fingerprint { get; }

    /// <summary>Resolves a table by exact placement-binding identity.</summary>
    /// <param name="placementBinding">Exact placement-binding identity.</param>
    /// <returns>The exact table binding.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="placementBinding"/> is not bound.</exception>
    public PostgresRelationQueryTableBinding ResolveTable(RelationQuerySourcePlacementBindingId placementBinding) =>
        Tables.SingleOrDefault(table => table.PlacementBinding == placementBinding)
        ?? throw new KeyNotFoundException($"PostgreSQL storage binding has no placement '{placementBinding.Value}'.");

    /// <summary>Resolves a table by exact compiled source or traversal input identity.</summary>
    /// <param name="input">Exact compiled input identity.</param>
    /// <returns>The exact table binding.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="input"/> is not bound.</exception>
    public PostgresRelationQueryTableBinding ResolveTable(RelationQueryInputId input) =>
        Tables.SingleOrDefault(table => table.Input == input)
        ?? throw new KeyNotFoundException($"PostgreSQL storage binding has no input '{input.Value}'.");

    /// <summary>Resolves a decomposed owned collection by its exact compiled collection-field input.</summary>
    /// <param name="collectionInput">Exact compiled root collection-field input.</param>
    /// <returns>The exact decomposed component-table binding.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="collectionInput"/> is not bound.</exception>
    public PostgresRelationQueryOwnedCollectionBinding ResolveOwnedCollection(
        RelationQueryInputId collectionInput) =>
        OwnedCollections.SingleOrDefault(collection => collection.CollectionInput == collectionInput)
        ?? throw new KeyNotFoundException(
            $"PostgreSQL storage binding has no owned collection input '{collectionInput.Value}'.");

    internal static string RequireIdentifier(string value, string parameterName)
    {
        value = Guard.RequireNotNullOrWhiteSpace(value, parameterName);
        try
        {
            return PostgresSqlDialect.Identifier(value).Value;
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(exception.Message, parameterName, exception);
        }
    }
}

static class PostgresRelationQueryBindingFingerprinter
{
    const string Algorithm = "sha256";
    const string Canonicalization = "cohesive.relations.postgres-binding/v3-c14n/v1";
    const string DerivedIdentityCanonicalization = "cohesive.relations.postgres-binding-id/v3-c14n/v1";

    public static PostgresRelationQueryBindingFingerprint Compute(PostgresRelationQueryStorageBinding binding)
    {
        StringBuilder canonical = new();
        Append(canonical, Canonicalization);
        Append(canonical, binding.SchemaVersion);
        Append(canonical, binding.DatabaseSemanticsProfile);
        Append(canonical, binding.Id.Value);
        Append(canonical, binding.Database.Value);
        Append(canonical, binding.Target.Value);
        Append(canonical, binding.TargetProfile.Value);
        Append(canonical, (int)binding.Origin);
        Append(canonical, binding.ConventionSetVersion);
        AppendFingerprint(canonical, binding.CompiledPlanFingerprint);
        AppendFingerprint(canonical, binding.PlacementFingerprint);
        Append(canonical, binding.Tables.Length);
        foreach (var table in binding.Tables)
            AppendTable(canonical, table);
        Append(canonical, binding.OwnedCollections.Length);
        foreach (var collection in binding.OwnedCollections)
            AppendOwnedCollection(canonical, collection);
        Append(canonical, binding.ConfigurationDecisions.Length);
        foreach (var decision in binding.ConfigurationDecisions)
        {
            Append(canonical, decision.Setting);
            Append(canonical, (int)decision.Origin);
            Append(canonical, decision.Authority);
        }
        return new(Algorithm, Canonicalization, ComputeHash(canonical));
    }

    internal static string ComputeDerivedIdentity(
        PostgresRelationQueryDatabaseId database,
        RelationQueryPlanComponentFingerprint plan,
        RelationQuerySourcePlacementFingerprint placement,
        string conventionSetVersion,
        IEnumerable<PostgresRelationQueryTableBinding> tables)
    {
        var normalizedTables = tables
            .OrderBy(static table => table.PlacementBinding.Value, StringComparer.Ordinal)
            .ToArray();
        StringBuilder canonical = new();
        Append(canonical, DerivedIdentityCanonicalization);
        Append(canonical, PostgresRelationQueryStorageBinding.CurrentSchemaVersion);
        Append(canonical, PostgresRelationQueryStorageBinding.CanonicalDatabaseSemanticsProfile);
        Append(canonical, PostgresRelationQueryTargetProfile.Target.Value);
        Append(canonical, PostgresRelationQueryTargetProfile.ProfileId.Value);
        Append(canonical, database.Value);
        Append(canonical, conventionSetVersion);
        AppendFingerprint(canonical, plan);
        AppendFingerprint(canonical, placement);
        Append(canonical, normalizedTables.Length);
        foreach (var table in normalizedTables)
            AppendTable(canonical, table);
        Append(canonical, 0);
        return ComputeHash(canonical);
    }

    static void AppendOwnedCollection(
        StringBuilder canonical,
        PostgresRelationQueryOwnedCollectionBinding collection)
    {
        Append(canonical, collection.Collection.Value);
        Append(canonical, collection.RootPlacementBinding.Value);
        Append(canonical, collection.CollectionInput.Value);
        AppendPath(canonical, collection.CollectionPath);
        Append(canonical, collection.ComponentType.Value);
        Append(canonical, collection.SchemaName);
        Append(canonical, collection.TableName);
        AppendPath(canonical, collection.ParentRoot.SemanticPath);
        Append(canonical, collection.ParentRoot.ColumnName);
        Append(canonical, (int)collection.ParentRoot.ScalarType);
        AppendText(canonical, collection.ParentRoot.TextSemantics);
        AppendNumericDomain(canonical, collection.ParentRoot.NumericDomain);
        AppendTemporalDomain(canonical, collection.ParentRoot.TemporalDomain);
        AppendPartition(canonical, collection.Partition);
        AppendPath(canonical, collection.LocalIdentityPath);
        AppendPath(canonical, collection.OrdinalPath);
        Append(canonical, collection.Fields.Length);
        foreach (var field in collection.Fields)
        {
            AppendPath(canonical, field.SemanticPath);
            Append(canonical, field.ColumnName);
            Append(canonical, (int)field.ScalarType);
            Append(canonical, (int)field.MissingValueEncoding);
            Append(canonical, (int)field.NullValueEncoding);
            AppendText(canonical, field.TextSemantics);
            Append(canonical, (int)field.Ordering);
            AppendNumericDomain(canonical, field.NumericDomain);
            AppendTemporalDomain(canonical, field.TemporalDomain);
        }
        Append(canonical, collection.ValidatedParentForeignKeyName);
        Append(canonical, collection.ValidatedAggregateIdentityName);
        Append(canonical, collection.AtomicityEvidenceReference);
        Append(canonical, collection.ChangeCaptureEvidenceReference);
    }

    static void AppendTable(StringBuilder canonical, PostgresRelationQueryTableBinding table)
    {
        Append(canonical, table.Source.Value);
        Append(canonical, table.PlacementBinding.Value);
        Append(canonical, table.Input.Value);
        Append(canonical, table.Shape.GraphId.Value);
        Append(canonical, table.Shape.ShapeId.Value);
        Append(canonical, table.SchemaName);
        Append(canonical, table.TableName);
        AppendIdentity(canonical, table.Identity);
        AppendPartition(canonical, table.Partition);
        Append(canonical, table.Fields.Length);
        foreach (var field in table.Fields)
        {
            Append(canonical, field.Input.Value);
            AppendPath(canonical, field.SemanticPath);
            Append(canonical, field.ColumnName);
            Append(canonical, (int)field.ScalarType);
            Append(canonical, (int)field.MissingValueEncoding);
            Append(canonical, (int)field.NullValueEncoding);
            AppendText(canonical, field.TextSemantics);
            Append(canonical, (int)field.Ordering);
            AppendNumericDomain(canonical, field.NumericDomain);
            AppendDecimalAggregates(canonical, field.DecimalAggregates);
            AppendTemporalDomain(canonical, field.TemporalDomain);
        }
        Append(canonical, table.RelationshipReferences.Length);
        foreach (var reference in table.RelationshipReferences)
        {
            Append(canonical, reference.Input.Value);
            AppendPath(canonical, reference.SemanticPath);
            Append(canonical, reference.ColumnName);
            Append(canonical, (int)reference.ScalarType);
            Append(canonical, (int)reference.Uniqueness);
            Append(canonical, (int)reference.MissingValueEncoding);
            Append(canonical, (int)reference.NullValueEncoding);
            AppendText(canonical, reference.TextSemantics);
            AppendNumericDomain(canonical, reference.NumericDomain);
            AppendTemporalDomain(canonical, reference.TemporalDomain);
        }
        Append(canonical, table.IntervalValidities.Length);
        foreach (var interval in table.IntervalValidities)
        {
            Append(canonical, interval.LowerInput.Value);
            AppendPath(canonical, interval.LowerPath);
            Append(canonical, (int)interval.LowerNullBehavior);
            Append(canonical, interval.UpperInput.Value);
            AppendPath(canonical, interval.UpperPath);
            Append(canonical, (int)interval.UpperNullBehavior);
            Append(canonical, interval.ValidatedCheckConstraintName);
        }
    }

    static void AppendIdentity(StringBuilder builder, PostgresRelationQueryIdentityBinding? identity)
    {
        Append(builder, identity is null ? 0 : 1);
        if (identity is null)
            return;
        AppendPath(builder, identity.SemanticPath);
        Append(builder, identity.ColumnName);
        Append(builder, (int)identity.ScalarType);
        AppendText(builder, identity.TextSemantics);
        AppendNumericDomain(builder, identity.NumericDomain);
        AppendTemporalDomain(builder, identity.TemporalDomain);
    }

    static void AppendPartition(StringBuilder builder, PostgresRelationQueryPartitionBinding? partition)
    {
        Append(builder, partition is null ? 0 : 1);
        if (partition is null)
            return;
        Append(builder, partition.SourceSelector);
        AppendPath(builder, partition.SemanticPath);
        Append(builder, partition.ColumnName);
        Append(builder, (int)partition.ScalarType);
        AppendText(builder, partition.TextSemantics);
        AppendNumericDomain(builder, partition.NumericDomain);
        AppendTemporalDomain(builder, partition.TemporalDomain);
    }

    static void AppendText(StringBuilder builder, PostgresRelationQueryTextSemantics? text)
    {
        Append(builder, text is null ? 0 : 1);
        if (text is null)
            return;
        Append(builder, text.Collation);
        Append(builder, (int)text.Equality);
        Append(builder, (int)text.Ordering);
        Append(builder, text.OrderingDomain is null ? 0 : 1);
        if (text.OrderingDomain is null)
            return;
        Append(builder, text.OrderingDomain.Strategy);
        Append(builder, text.OrderingDomain.ValidatedConstraintName);
        Append(builder, text.OrderingDomain.Authority);
    }

    static void AppendNumericDomain(
        StringBuilder builder,
        PostgresRelationQueryNumericDomainEvidence? evidence)
    {
        Append(builder, evidence is null ? 0 : 1);
        if (evidence is null)
            return;
        Append(builder, evidence.Strategy);
        Append(builder, evidence.Precision);
        Append(builder, evidence.Scale);
        Append(builder, evidence.ValidatedConstraintName);
        Append(builder, evidence.Authority);
    }

    static void AppendDecimalAggregates(
        StringBuilder builder,
        PostgresRelationQueryDecimalAggregateAttestation? evidence)
    {
        Append(builder, evidence is null ? 0 : 1);
        if (evidence is null)
            return;
        Append(builder, evidence.Strategy);
        Append(builder, (int)evidence.Guarantees);
        Append(builder, evidence.DomainEvidence);
        Append(builder, evidence.Authority);
    }

    static void AppendTemporalDomain(
        StringBuilder builder,
        PostgresRelationQueryTemporalDomainEvidence? evidence)
    {
        Append(builder, evidence is null ? 0 : 1);
        if (evidence is null)
            return;
        Append(builder, evidence.Strategy);
        Append(builder, evidence.ValidatedConstraintName);
        Append(builder, evidence.Authority);
    }

    static void AppendPath(StringBuilder builder, FieldPath path)
    {
        Append(builder, path.Segments.Length);
        foreach (var segment in path.Segments)
        {
            Append(builder, (int)segment.Kind);
            Append(builder, segment.Segment);
        }
    }

    static void AppendFingerprint(StringBuilder builder, RelationQueryPlanComponentFingerprint? fingerprint)
    {
        Append(builder, fingerprint is null ? 0 : 1);
        if (fingerprint is null)
            return;
        Append(builder, fingerprint.Algorithm);
        Append(builder, fingerprint.Canonicalization);
        Append(builder, fingerprint.Value);
    }

    static void AppendFingerprint(StringBuilder builder, RelationQuerySourcePlacementFingerprint? fingerprint)
    {
        Append(builder, fingerprint is null ? 0 : 1);
        if (fingerprint is null)
            return;
        Append(builder, fingerprint.Algorithm);
        Append(builder, fingerprint.Canonicalization);
        Append(builder, fingerprint.Value);
    }

    static void Append(StringBuilder builder, int value) =>
        Append(builder, value.ToString(CultureInfo.InvariantCulture));

    static void Append(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }
        builder.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }

    static string ComputeHash(StringBuilder canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
}
