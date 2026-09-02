using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

/// <summary>Versioned fingerprint metadata for one canonical identity-free observation.</summary>
public sealed record ObservationFingerprint
{
    /// <summary>Creates observation fingerprint metadata.</summary>
    /// <param name="algorithm">Hash-algorithm identity.</param>
    /// <param name="canonicalization">Canonicalization-profile identity.</param>
    /// <param name="value">Fingerprint value emitted by the named algorithm.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="algorithm"/>, <paramref name="canonicalization"/>, or <paramref name="value"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="algorithm"/>, <paramref name="canonicalization"/>, or <paramref name="value"/> is empty or
    /// consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ObservationFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash-algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization-profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Fingerprint value emitted by <see cref="Algorithm"/>.</summary>
    public string Value { get; }
}

/// <summary>
/// A concrete, identity-free object value governed by one graph-qualified semantic shape.
/// </summary>
/// <remarks>
/// An observation answers what was observed. Entity or occurrence identity, source version, lineage, storage
/// placement, and runtime layout belong to explicit surrounding interpretations. The retained
/// <see cref="ObservationValue"/> is immutable and is the sole field-value authority.
/// </remarks>
public sealed class Observation : IEquatable<Observation>, IObservationFieldReader
{
    readonly ObservationValue value;

    /// <summary>Versioned identity of the canonical portable JSON representation.</summary>
    public const string CanonicalFormat = "cohesive-observation/v1";

    /// <summary>Hash algorithm used by <see cref="ComputeFingerprint"/>.</summary>
    public const string FingerprintAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by <see cref="ComputeFingerprint"/>.</summary>
    public const string FingerprintCanonicalization = "cohesive-observation/v1-c14n/v1";

    [JsonConstructor]
    Observation(QualifiedShapeId shapeId, ObservationValue value)
    {
        ShapeId = shapeId;
        this.value = value;
    }

    /// <summary>Gets the qualified identity of the semantic shape governing this value.</summary>
    public QualifiedShapeId ShapeId { get; }

    /// <summary>Gets the immutable concrete object value.</summary>
    public ObservationValue Value => value;

    /// <summary>Gets immutable field values keyed by canonical semantic identity.</summary>
    public IReadOnlyDictionary<string, ObservationValue> Fields => value.Fields!;

    /// <summary>Creates and validates an observation from exact graph-scoped shape evidence.</summary>
    /// <param name="shape">Exact graph and shape that govern the value.</param>
    /// <param name="value">Concrete, present, non-null object value.</param>
    /// <returns>A validated identity-free observation.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="shape"/> is default and does not contain a graph.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is not a concrete portable object or does not adhere to the shape.
    /// </exception>
    public static Observation Create(GraphShapeId shape, ObservationValue value)
    {
        ArgumentNullException.ThrowIfNull(shape.Graph);
        var definition = shape.Graph.GetShape(shape.ShapeId);
        if (!ObservationValidator.TryValidateAgainstShape(
                value: value,
                shape: definition,
                validationError: out var validationError,
                graph: shape.Graph))
        {
            throw new ArgumentException(
                $"Observed value does not adhere to shape '{shape.QualifiedId}': {validationError}",
                nameof(value));
        }

        return new(shape.QualifiedId, value);
    }

    /// <summary>Creates and validates an observation from canonical field values.</summary>
    /// <param name="shape">Exact graph and shape that govern the fields.</param>
    /// <param name="fields">Field values keyed by canonical semantic identity.</param>
    /// <returns>A validated identity-free observation.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="shape"/> is default or <paramref name="fields"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The fields do not adhere to the shape.</exception>
    public static Observation Create(
        GraphShapeId shape,
        IReadOnlyDictionary<string, ObservationValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return Create(shape, ObservationValue.FromObject(fields));
    }

    /// <summary>
    /// Creates and validates an observation after proving that a qualified shape belongs to the supplied graph.
    /// </summary>
    /// <param name="graph">Exact semantic shape graph.</param>
    /// <param name="shapeId">Qualified shape identity expected in <paramref name="graph"/>.</param>
    /// <param name="value">Concrete, present, non-null object value.</param>
    /// <returns>A validated identity-free observation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="shapeId"/> belongs to another graph, names an absent shape, or <paramref name="value"/> is
    /// invalid for the resolved shape.
    /// </exception>
    public static Observation Create(
        ShapeGraph graph,
        QualifiedShapeId shapeId,
        ObservationValue value)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Id != shapeId.GraphId)
        {
            throw new ArgumentException(
                $"Shape '{shapeId}' belongs to graph '{shapeId.GraphId.Value}', not supplied graph '{graph.Id.Value}'.",
                nameof(shapeId));
        }

        return Create(new GraphShapeId(graph, shapeId.ShapeId), value);
    }

    /// <summary>Gets a top-level field by canonical semantic identity.</summary>
    /// <param name="fieldIdentity">Canonical field identity.</param>
    /// <returns>The immutable field value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldIdentity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldIdentity"/> is empty or white-space.</exception>
    /// <exception cref="KeyNotFoundException">The field is not present.</exception>
    public ObservationValue GetField(string fieldIdentity) =>
        TryGetField(fieldIdentity, out var field)
            ? field
            : throw new KeyNotFoundException(
                $"Observation of shape '{ShapeId}' does not contain field '{fieldIdentity}'.");

    /// <summary>Gets a top-level field by its semantic definition.</summary>
    /// <param name="field">Semantic field definition.</param>
    /// <returns>The immutable field value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">The field is not present.</exception>
    public ObservationValue GetField(FieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return GetField(field.Name.Value);
    }

    /// <summary>Attempts to get a top-level field by canonical semantic identity.</summary>
    /// <param name="fieldIdentity">Canonical field identity.</param>
    /// <param name="field">Field value when present; otherwise the default value.</param>
    /// <returns><see langword="true"/> when the field is present; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldIdentity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldIdentity"/> is empty or white-space.</exception>
    public bool TryGetField(string fieldIdentity, out ObservationValue field) =>
        value.TryGetProperty(fieldIdentity, out field);

    /// <summary>Attempts to get a top-level field by its semantic definition.</summary>
    /// <param name="field">Semantic field definition.</param>
    /// <param name="value">Field value when present; otherwise the default value.</param>
    /// <returns><see langword="true"/> when the field is present; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public bool TryGetField(FieldDefinition field, out ObservationValue value)
    {
        ArgumentNullException.ThrowIfNull(field);
        return TryGetField(field.Name.Value, out value);
    }

    /// <summary>Gets the value at an object-field path.</summary>
    /// <param name="path">Non-empty path containing field segments.</param>
    /// <returns>The immutable value at the path.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="NotSupportedException"><paramref name="path"/> contains collection-element navigation.</exception>
    /// <exception cref="KeyNotFoundException">The path cannot be resolved.</exception>
    public ObservationValue GetField(FieldPath path) =>
        TryGetField(path, out var field)
            ? field
            : throw new KeyNotFoundException(
                $"Observation of shape '{ShapeId}' does not contain path '{path}'.");

    /// <summary>Attempts to get the value at an object-field path.</summary>
    /// <param name="path">Non-empty path containing field segments.</param>
    /// <param name="field">Value at the path when present; otherwise the default value.</param>
    /// <returns><see langword="true"/> when every field segment resolves; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="NotSupportedException"><paramref name="path"/> contains collection-element navigation.</exception>
    public bool TryGetField(FieldPath path, out ObservationValue field)
    {
        if (path.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("An observation field path requires at least one segment.", nameof(path));

        return value.TryGetField(path, out field);
    }

    /// <summary>
    /// Materializes this observation using the cached deterministic default plan for <typeparamref name="T"/> and
    /// this observation's exact qualified shape.
    /// </summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <returns>The materialized CLR value.</returns>
    /// <remarks>
    /// The default plan maps semantic field identities to CLR property names, uses web JSON conversion with string
    /// enums, and permits defaults for optional members. Use <see cref="ObservationMaterializer.For{T}(GraphShapeId)"/>
    /// when mappings, metadata, conversion, or missing-field policy must be configured explicitly.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A target constructor cannot be selected, a mapped property is not settable, a required field is absent, or
    /// conversion fails.
    /// </exception>
    public T Materialize<T>() => ObservationMaterializer.GetDefault<T>(this).Materialize(this);

    /// <summary>Serializes the qualified shape and value to canonical portable JSON.</summary>
    /// <returns>Canonical JSON whose object properties are ordered ordinally.</returns>
    /// <exception cref="InvalidOperationException">A retained value has no canonical portable JSON encoding.</exception>
    public string ToCanonicalJson()
    {
        using PooledByteBufferWriter buffer = new();
        WriteCanonicalJson(buffer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writes the qualified shape and value as canonical portable UTF-8 JSON.</summary>
    /// <param name="output">Caller-owned destination that receives the complete canonical representation.</param>
    /// <remarks>
    /// Output is appended at the destination's current position. The observation retains no reference to destination
    /// storage, allowing callers to reuse or pool it after consuming the written bytes. After the per-thread JSON
    /// writer is warm, this operation allocates no managed memory when the destination has sufficient capacity.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A retained value has no canonical portable JSON encoding.</exception>
    public void WriteCanonicalJson(IBufferWriter<byte> output) =>
        CanonicalJsonWriter.WriteCanonicalObservation(output, this);

    /// <summary>Serializes the qualified shape and value to canonical portable UTF-8 JSON.</summary>
    /// <returns>A newly allocated byte array containing the canonical representation.</returns>
    /// <exception cref="InvalidOperationException">A retained value has no canonical portable JSON encoding.</exception>
    public byte[] ToCanonicalJsonUtf8()
    {
        using PooledByteBufferWriter buffer = new();
        WriteCanonicalJson(buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Computes the versioned SHA-256 fingerprint of the canonical qualified shape and value.</summary>
    /// <returns>Fingerprint metadata and a lowercase hexadecimal digest.</returns>
    /// <remarks>
    /// Canonical bytes are streamed through bounded staging storage into incremental SHA-256 state; the complete JSON
    /// payload is never materialized solely for hashing.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A retained value has no canonical portable JSON encoding.</exception>
    public ObservationFingerprint ComputeFingerprint()
    {
        using Sha256BufferWriter hash = new();
        CanonicalJsonWriter.WriteCanonicalObservationStreaming(hash, this);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        hash.Complete(digest);
        return new(
            algorithm: FingerprintAlgorithm,
            canonicalization: FingerprintCanonicalization,
            value: Convert.ToHexStringLower(digest));
    }

    /// <summary>Compares the qualified shape and concrete value of two identity-free observations.</summary>
    /// <param name="other">Observation to compare, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when shape and value are semantically equal.</returns>
    public bool Equals(Observation? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || ShapeId == other.ShapeId && value.Equals(other.value));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Observation other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ShapeId, value);
}

sealed class PooledByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    const int InitialCapacity = 512;
    byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);
    int writtenCount;
    bool disposed;

    public ReadOnlySpan<byte> WrittenSpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return buffer.AsSpan(0, writtenCount);
        }
    }

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (count < 0 || count > buffer.Length - writtenCount)
            throw new ArgumentOutOfRangeException(nameof(count), count, "The committed byte count exceeds the supplied buffer.");
        writtenCount += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return buffer.AsMemory(writtenCount);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return buffer.AsSpan(writtenCount);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        buffer = [];
        writtenCount = 0;
    }

    void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint), sizeHint, "A buffer size hint cannot be negative.");

        var requiredCapacity = checked(writtenCount + Math.Max(sizeHint, 1));
        if (requiredCapacity <= buffer.Length)
            return;

        var replacement = ArrayPool<byte>.Shared.Rent(Math.Max(requiredCapacity, checked(buffer.Length * 2)));
        buffer.AsSpan(0, writtenCount).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        buffer = replacement;
    }
}

sealed class Sha256BufferWriter : IBufferWriter<byte>, IDisposable
{
    const int BufferSize = 4 * 1024;
    readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    int bufferedCount;
    bool disposed;

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (count < 0 || count > buffer.Length - bufferedCount)
            throw new ArgumentOutOfRangeException(nameof(count), count, "The committed byte count exceeds the supplied buffer.");
        bufferedCount += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return buffer.AsMemory(bufferedCount);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return buffer.AsSpan(bufferedCount);
    }

    internal void Complete(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Flush();
        if (!hash.TryGetHashAndReset(destination, out var written) || written != SHA256.HashSizeInBytes)
            throw new InvalidOperationException("The canonical observation fingerprint could not be completed.");
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        hash.Dispose();
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        buffer = [];
        bufferedCount = 0;
    }

    void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint), sizeHint, "A buffer size hint cannot be negative.");

        sizeHint = Math.Max(sizeHint, 1);
        if (sizeHint <= buffer.Length - bufferedCount)
            return;

        Flush();
        if (sizeHint <= buffer.Length)
            return;

        var replacement = ArrayPool<byte>.Shared.Rent(sizeHint);
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        buffer = replacement;
    }

    void Flush()
    {
        if (bufferedCount == 0)
            return;

        hash.AppendData(buffer.AsSpan(0, bufferedCount));
        bufferedCount = 0;
    }
}
