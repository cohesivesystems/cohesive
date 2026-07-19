using System.Globalization;

namespace Cohesive.Processes.Runtime;

/// <summary>
/// Entity repository used by the process runtime for entity hydration, state mutations, and effect persistence.
/// </summary>
public interface IProcessEntityRepository
{
    /// <summary>
    /// Creates a new entity snapshot.
    /// </summary>
    Task<ProcessEntitySnapshot> Create(OperationContext context, ProcessEntityRef entity, EntityState state, string processId);

    /// <summary>
    /// Gets entity snapshot by reference.
    /// </summary>
    Task<ProcessEntitySnapshot> Get(OperationContext context, ProcessEntityRef entity, ProcessEntityReadOptions? options = null);

    /// <summary>
    /// Commits transition state and emitted effects using optimistic concurrency.
    /// </summary>
    Task Update(OperationContext context, ProcessEntityRef entity, TransitionResult transition, string processId, ProcessEntityWriteOptions options);
}

/// <summary>
/// Structured read request for loading process entity state.
/// </summary>
public sealed record ProcessEntityReadOptions
{
    /// <summary>
    /// Creates a read request.
    /// </summary>
    /// <param name="fieldSelection">Optional field-selection request, or <see langword="null"/> to load the full entity state.</param>
    /// <param name="expectedVersion">Optional expected logical entity version.</param>
    /// <param name="expectedConcurrencyToken">Optional expected storage concurrency token.</param>
    public ProcessEntityReadOptions(
        FieldSelection? fieldSelection = null,
        long? expectedVersion = null,
        ProcessEntityConcurrencyToken? expectedConcurrencyToken = null
        )
    {
        FieldSelection = fieldSelection ?? FieldSelection.Full;
        ExpectedVersion = expectedVersion;
        ExpectedConcurrencyToken = expectedConcurrencyToken;
    }

    /// <summary>
    /// Full-state read request.
    /// </summary>
    public static ProcessEntityReadOptions Full { get; } = new(FieldSelection.Full);

    /// <summary>
    /// Field-selection request for this read.
    /// </summary>
    public FieldSelection FieldSelection { get; }

    /// <summary>
    /// Optional projected field subset, or <see langword="null"/> for a full-state read.
    /// </summary>
    public IReadOnlySet<string>? Fields => FieldSelection.Fields;

    /// <summary>
    /// Optional expected logical entity version.
    /// </summary>
    public long? ExpectedVersion { get; }

    /// <summary>
    /// Optional expected storage concurrency token.
    /// </summary>
    public ProcessEntityConcurrencyToken? ExpectedConcurrencyToken { get; }

    /// <summary>
    /// Indicates whether this request projects a field subset rather than loading the full state.
    /// </summary>
    public bool HasFieldProjection => Fields is not null;

    /// <summary>
    /// Creates a projected-field read request.
    /// </summary>
    /// <param name="fields">Field names to load.</param>
    /// <returns>A read request containing the distinct supplied field names.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="fields"/> contains a null, empty, or white-space name.
    /// </exception>
    public static ProcessEntityReadOptions ForFields(params string[] fields) => new(FieldSelection.ForFields(fields));

    /// <summary>
    /// Creates a copy of this request with an expected version constraint.
    /// </summary>
    public ProcessEntityReadOptions WithExpectedVersion(long expectedVersion) => new(FieldSelection, expectedVersion, ExpectedConcurrencyToken);

    /// <summary>
    /// Creates a copy of this request with an expected storage concurrency-token constraint.
    /// </summary>
    public ProcessEntityReadOptions WithExpectedConcurrencyToken(ProcessEntityConcurrencyToken expectedConcurrencyToken) =>
        new(FieldSelection, ExpectedVersion, expectedConcurrencyToken);
}

/// <summary>
/// Structured write request for committing process entity state.
/// </summary>
public sealed record ProcessEntityWriteOptions
{
    /// <summary>
    /// Creates a write request.
    /// </summary>
    /// <param name="expectedConcurrencyToken">Expected storage concurrency token.</param>
    /// <param name="fieldSelection">Optional field-selection request, or <see langword="null"/> to commit the full entity state.</param>
    public ProcessEntityWriteOptions(
        ProcessEntityConcurrencyToken expectedConcurrencyToken,
        FieldSelection? fieldSelection = null
        )
    {
        ExpectedConcurrencyToken = expectedConcurrencyToken;
        FieldSelection = fieldSelection ?? FieldSelection.Full;
    }

    /// <summary>
    /// Expected storage concurrency token.
    /// </summary>
    public ProcessEntityConcurrencyToken ExpectedConcurrencyToken { get; }

    /// <summary>
    /// Field-selection request for this write.
    /// </summary>
    public FieldSelection FieldSelection { get; }

    /// <summary>
    /// Optional projected field subset, or <see langword="null"/> for a full-state write.
    /// </summary>
    public IReadOnlySet<string>? Fields => FieldSelection.Fields;

    /// <summary>
    /// Indicates whether this request writes a field subset rather than replacing the full state.
    /// </summary>
    public bool HasFieldProjection => Fields is not null;

    /// <summary>
    /// Creates a full-state write request.
    /// </summary>
    /// <param name="expectedConcurrencyToken">Expected storage concurrency token.</param>
    /// <returns>A full-state write request constrained by <paramref name="expectedConcurrencyToken"/>.</returns>
    public static ProcessEntityWriteOptions Full(ProcessEntityConcurrencyToken expectedConcurrencyToken) =>
        new(expectedConcurrencyToken, FieldSelection.Full);

    /// <summary>
    /// Creates a projected-field write request.
    /// </summary>
    /// <param name="expectedConcurrencyToken">Expected storage concurrency token.</param>
    /// <param name="fields">Field names to commit.</param>
    /// <returns>A projected write request containing the distinct supplied field names.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="fields"/> contains a null, empty, or white-space name.
    /// </exception>
    public static ProcessEntityWriteOptions ForFields(ProcessEntityConcurrencyToken expectedConcurrencyToken, params string[] fields) =>
        new(expectedConcurrencyToken, FieldSelection.ForFields(fields));
}

/// <summary>
/// Storage-specific optimistic concurrency token carried alongside a process entity snapshot.
/// </summary>
public readonly record struct ProcessEntityConcurrencyToken
{
    /// <summary>
    /// Creates a concurrency token wrapper.
    /// </summary>
    /// <param name="value">Underlying storage concurrency token value.</param>
    public ProcessEntityConcurrencyToken(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Underlying storage concurrency token value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Creates a token from a numeric version value.
    /// </summary>
    public static ProcessEntityConcurrencyToken FromVersion(long version) =>
        new(version.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override string ToString() => Value;
}
