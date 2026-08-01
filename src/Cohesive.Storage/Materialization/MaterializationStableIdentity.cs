using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Shared canonical digest mechanism for materialization execution identities.</summary>
internal static class MaterializationStableIdentity
{
    internal static string Digest(string first)
    {
        using DigestBuilder builder = new();
        builder.Append(first);
        return builder.Complete();
    }

    internal static string Digest(string first, string second)
    {
        using DigestBuilder builder = new();
        builder.Append(first);
        builder.Append(second);
        return builder.Complete();
    }

    internal static string Digest(string first, string second, string third)
    {
        using DigestBuilder builder = new();
        builder.Append(first);
        builder.Append(second);
        builder.Append(third);
        return builder.Complete();
    }

    internal static string Digest(string first, string second, string third, string fourth)
    {
        using DigestBuilder builder = new();
        builder.Append(first);
        builder.Append(second);
        builder.Append(third);
        builder.Append(fourth);
        return builder.Complete();
    }

    internal static string Digest(
        string first,
        string second,
        string third,
        string fourth,
        string fifth)
    {
        using DigestBuilder builder = new();
        builder.Append(first);
        builder.Append(second);
        builder.Append(third);
        builder.Append(fourth);
        builder.Append(fifth);
        return builder.Complete();
    }

    internal static string Digest(
        string first,
        string second,
        string third,
        string fourth,
        string fifth,
        string sixth,
        string seventh)
    {
        using DigestBuilder builder = new();
        builder.Append(first);
        builder.Append(second);
        builder.Append(third);
        builder.Append(fourth);
        builder.Append(fifth);
        builder.Append(sixth);
        builder.Append(seventh);
        return builder.Complete();
    }

    /// <summary>Hashes a length-delimited sequence of Unicode values using SHA-256.</summary>
    /// <param name="values">Ordered semantic identity components.</param>
    /// <returns>A lower-case hexadecimal SHA-256 digest.</returns>
    internal static string Digest(ReadOnlySpan<string> values)
    {
        using DigestBuilder builder = new();
        foreach (var value in values)
            builder.Append(value);

        return builder.Complete();
    }

    internal ref struct DigestBuilder
    {
        readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bool completed;

        public DigestBuilder()
        {
        }

        internal void Append(string value)
        {
            if (completed)
                throw new InvalidOperationException("A completed stable-identity digest cannot accept more values.");

            ArgumentNullException.ThrowIfNull(value);
            var byteCount = Encoding.UTF8.GetByteCount(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, byteCount);
            hash.AppendData(length);

            Span<byte> stackBuffer = stackalloc byte[256];
            byte[]? rented = null;
            Span<byte> bytes = byteCount <= stackBuffer.Length
                ? stackBuffer[..byteCount]
                : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
            try
            {
                Encoding.UTF8.GetBytes(value, bytes);
                hash.AppendData(bytes);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        internal string Complete()
        {
            if (completed)
                throw new InvalidOperationException("A stable-identity digest can be completed only once.");

            completed = true;
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        public void Dispose() => hash.Dispose();
    }
}

/// <summary>Canonical target-item identity projection shared by baseline and incremental execution.</summary>
internal static class MaterializationItemIdentity
{
    const string Prefix = "materialization-rebuild-item/v1/";
    static readonly JsonSerializerOptions CanonicalJsonOptions = MaterializationJsonSerializer.CreateOptions();

    /// <summary>Projects one concrete scalar Relations identity into the stable target keyspace.</summary>
    /// <param name="identity">Concrete scalar output identity.</param>
    /// <returns>The canonical target item identity.</returns>
    /// <exception cref="ArgumentException"><paramref name="identity"/> is not a concrete scalar value.</exception>
    internal static MaterializationItemId FromRelationIdentity(ObservationValue identity)
    {
        if (identity.Kind is ObservationValueKind.Undefined
            or ObservationValueKind.Null
            or ObservationValueKind.Array
            or ObservationValueKind.Object)
        {
            throw new ArgumentException(
                "A materialized Relations row requires one concrete scalar identity.",
                nameof(identity));
        }

        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new IdentityContent(identity),
            CanonicalJsonOptions);
        return new(Prefix + Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    /// <summary>Projects one canonical string root identity into the shared target keyspace.</summary>
    /// <param name="rootIdentity">Stable root observation identity.</param>
    /// <returns>The same target key baseline hydration emits for a string row identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rootIdentity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rootIdentity"/> is empty or ill-formed Unicode.</exception>
    internal static MaterializationItemId FromRootIdentity(string rootIdentity) =>
        FromRelationIdentity(ObservationValue.FromString(
            MaterializationContract.RequireUnicodeIdentity(rootIdentity, nameof(rootIdentity))));

    sealed record IdentityContent(ObservationValue Value);
}
