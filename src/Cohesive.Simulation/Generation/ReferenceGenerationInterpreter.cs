using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Generation;

/// <summary>Compact evidence required to reproduce one exact generated value.</summary>
public sealed record GenerationReplayEvidence
{
    /// <summary>Creates generation replay evidence.</summary>
    /// <param name="rootSeed">Caller-supplied deterministic root seed.</param>
    /// <param name="sequenceIndex">Stable zero-based item index within a generated sequence.</param>
    /// <param name="scope">Stable semantic namespace isolating this generated stream.</param>
    /// <param name="definitionId">Stable logical generation-definition identity.</param>
    /// <param name="definitionRevision">Exact authored generation-definition revision.</param>
    /// <param name="definitionFingerprint">Exact compiled semantic fingerprint.</param>
    /// <param name="interpreter">Generation interpreter identity and version.</param>
    /// <param name="entropyAlgorithm">Addressable entropy algorithm identity and version.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequenceIndex"/> is negative.</exception>
    /// <exception cref="ArgumentException">The scope is default, or a string identity is empty or white-space.</exception>
    [JsonConstructor]
    public GenerationReplayEvidence(
        long rootSeed,
        long sequenceIndex,
        GenerationScope scope,
        string definitionId,
        string definitionRevision,
        string definitionFingerprint,
        string interpreter,
        string entropyAlgorithm)
    {
        if (sequenceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sequenceIndex), sequenceIndex, "Sequence index cannot be negative.");
        GenerationScope.Validate(scope, nameof(scope));

        RootSeed = rootSeed;
        SequenceIndex = sequenceIndex;
        Scope = scope;
        DefinitionId = Guard.RequireNotNullOrWhiteSpace(definitionId);
        DefinitionRevision = Guard.RequireNotNullOrWhiteSpace(definitionRevision);
        DefinitionFingerprint = Guard.RequireNotNullOrWhiteSpace(definitionFingerprint);
        Interpreter = Guard.RequireNotNullOrWhiteSpace(interpreter);
        EntropyAlgorithm = Guard.RequireNotNullOrWhiteSpace(entropyAlgorithm);
    }

    /// <summary>Gets the caller-supplied deterministic root seed.</summary>
    public long RootSeed { get; }

    /// <summary>Gets the stable zero-based sequence item index.</summary>
    public long SequenceIndex { get; }

    /// <summary>Gets the stable semantic namespace that isolated this generated stream.</summary>
    public GenerationScope Scope { get; }

    /// <summary>Gets the stable logical generation-definition identity.</summary>
    public string DefinitionId { get; }

    /// <summary>Gets the exact authored generation-definition revision.</summary>
    public string DefinitionRevision { get; }

    /// <summary>Gets the exact compiled semantic fingerprint.</summary>
    public string DefinitionFingerprint { get; }

    /// <summary>Gets the generation interpreter identity and version.</summary>
    public string Interpreter { get; }

    /// <summary>Gets the addressable entropy algorithm identity and version.</summary>
    public string EntropyAlgorithm { get; }

    /// <summary>Encodes this evidence as one opaque, URL-safe compact replay token.</summary>
    /// <returns>A versioned token that can restore the complete replay evidence.</returns>
    /// <exception cref="InvalidOperationException">The evidence has no canonical token representation.</exception>
    /// <exception cref="JsonException">The evidence violates its strict token payload contract.</exception>
    /// <exception cref="NotSupportedException">The evidence contains an unsupported serialization type.</exception>
    public string ToToken() => GenerationReplayTokenCodec.Encode(this);

    /// <summary>Decodes and validates one compact replay token.</summary>
    /// <param name="token">Opaque token previously returned by <see cref="ToToken"/>.</param>
    /// <returns>The exact replay evidence encoded by <paramref name="token"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">
    /// <paramref name="token"/> is empty, has another version, is not canonical URL-safe Base64, or does not contain
    /// valid current-version replay evidence.
    /// </exception>
    public static GenerationReplayEvidence ParseToken(string token) =>
        GenerationReplayTokenCodec.Decode(token);
}

/// <summary>One generated core observation and the separate evidence required to replay it.</summary>
public sealed record GeneratedObservation
{
    /// <summary>Creates a generated observation result.</summary>
    /// <param name="observation">Generated identity-free core observation.</param>
    /// <param name="replay">Replay evidence for this exact generated item.</param>
    /// <exception cref="ArgumentNullException"><paramref name="observation"/> or <paramref name="replay"/> is null.</exception>
    public GeneratedObservation(Observation observation, GenerationReplayEvidence replay)
    {
        Observation = Guard.RequireNotNull(observation);
        Replay = Guard.RequireNotNull(replay);
    }

    /// <summary>Gets the generated identity-free core observation.</summary>
    public Observation Observation { get; }

    /// <summary>Gets replay evidence kept outside observation semantics.</summary>
    public GenerationReplayEvidence Replay { get; }
}

/// <summary>Deterministic reference interpretation of canonical generation IR.</summary>
public static class ReferenceGenerationInterpreter
{
    /// <summary>Stable reference-interpreter identity and version.</summary>
    public const string Identity = "cohesive-simulation-reference/v2";

    /// <summary>Stable addressable entropy algorithm identity and version.</summary>
    public const string EntropyAlgorithm = "cohesive-addressable-sha256/v2";

    /// <summary>Generates one observation at the requested semantic sequence address.</summary>
    /// <param name="plan">Validated provider-neutral generation plan.</param>
    /// <param name="seed">Caller-supplied deterministic root seed.</param>
    /// <param name="sequenceIndex">Stable zero-based item index.</param>
    /// <returns>The generated observation and compact replay evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequenceIndex"/> is negative.</exception>
    /// <exception cref="NotSupportedException">The plan contains a node unknown to this interpreter.</exception>
    public static GeneratedObservation Generate(
        CompiledGenerationPlan plan,
        long seed,
        long sequenceIndex = 0) =>
        Generate(plan, seed, GenerationScope.Default, sequenceIndex);

    /// <summary>Generates one observation in an isolated semantic scope at the requested sequence address.</summary>
    /// <param name="plan">Validated provider-neutral generation plan.</param>
    /// <param name="seed">Caller-supplied deterministic root seed.</param>
    /// <param name="scope">Stable semantic namespace isolating this generated stream.</param>
    /// <param name="sequenceIndex">Stable zero-based item index.</param>
    /// <returns>The generated observation and compact replay evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is default or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequenceIndex"/> is negative.</exception>
    /// <exception cref="NotSupportedException">The plan contains a node unknown to this interpreter.</exception>
    public static GeneratedObservation Generate(
        CompiledGenerationPlan plan,
        long seed,
        GenerationScope scope,
        long sequenceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        GenerationScope.Validate(scope, nameof(scope));
        if (sequenceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sequenceIndex), sequenceIndex, "Sequence index cannot be negative.");

        var fields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        foreach (var member in plan.Members)
        {
            var address = new EntropyAddress(
                scope,
                sequenceIndex,
                plan.OutputShape.ShapeId.Value,
                member.Identity.Value);
            fields.Add(member.Identity.Value, Generate(member.Generator, seed, address));
        }

        var observation = Observation.Create(plan.OutputShape, fields.ToImmutable());
        var replay = new GenerationReplayEvidence(
            rootSeed: seed,
            sequenceIndex: sequenceIndex,
            scope: scope,
            definitionId: plan.Definition.Id,
            definitionRevision: plan.Definition.Revision,
            definitionFingerprint: plan.Fingerprint,
            interpreter: Identity,
            entropyAlgorithm: EntropyAlgorithm);
        return new(observation, replay);
    }

    /// <summary>Replays one exact generation using previously retained evidence.</summary>
    /// <param name="plan">Validated generation plan named by the replay evidence.</param>
    /// <param name="replay">Exact replay evidence to apply.</param>
    /// <returns>The deterministically regenerated observation and equivalent replay evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="replay"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The evidence names another definition, revision, fingerprint, interpreter, or entropy algorithm.
    /// </exception>
    public static GeneratedObservation Replay(
        CompiledGenerationPlan plan,
        GenerationReplayEvidence replay)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(replay);
        ValidateReplayCoordinates(plan, replay);
        return Generate(plan, replay.RootSeed, replay.Scope, replay.SequenceIndex);
    }

    /// <summary>Replays one exact generation from a compact token.</summary>
    /// <param name="plan">Validated generation plan named by the replay token.</param>
    /// <param name="token">Opaque compact replay token.</param>
    /// <returns>The deterministically regenerated observation and equivalent replay evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="token"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="FormatException"><paramref name="token"/> is not a valid current-version replay token.</exception>
    /// <exception cref="ArgumentException">
    /// The token names another definition, revision, fingerprint, interpreter, or entropy algorithm.
    /// </exception>
    public static GeneratedObservation Replay(CompiledGenerationPlan plan, string token)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Replay(plan, GenerationReplayEvidence.ParseToken(token));
    }

    /// <summary>Generates an eagerly materialized bounded sequence addressed by item index.</summary>
    /// <param name="plan">Validated provider-neutral generation plan.</param>
    /// <param name="seed">Caller-supplied deterministic root seed shared by the sequence.</param>
    /// <param name="count">Number of items to generate.</param>
    /// <returns>Generated observations in ascending zero-based sequence-index order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static ImmutableArray<GeneratedObservation> GenerateSequence(
        CompiledGenerationPlan plan,
        long seed,
        int count) =>
        GenerateSequence(plan, seed, GenerationScope.Default, count);

    /// <summary>Generates an eagerly materialized bounded sequence in an isolated semantic scope.</summary>
    /// <param name="plan">Validated provider-neutral generation plan.</param>
    /// <param name="seed">Caller-supplied deterministic root seed shared by the sequence.</param>
    /// <param name="scope">Stable semantic namespace isolating this generated stream.</param>
    /// <param name="count">Number of items to generate.</param>
    /// <returns>Generated observations in ascending zero-based sequence-index order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is default or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static ImmutableArray<GeneratedObservation> GenerateSequence(
        CompiledGenerationPlan plan,
        long seed,
        GenerationScope scope,
        int count)
    {
        ArgumentNullException.ThrowIfNull(plan);
        GenerationScope.Validate(scope, nameof(scope));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Generation count cannot be negative.");
        if (count == 0)
            return [];

        var generated = ImmutableArray.CreateBuilder<GeneratedObservation>(count);
        for (var index = 0; index < count; index++)
            generated.Add(Generate(plan, seed, scope, index));
        return generated.MoveToImmutable();
    }

    /// <summary>Lazily enumerates a bounded generated sequence addressed by item index.</summary>
    /// <param name="plan">Validated provider-neutral generation plan.</param>
    /// <param name="seed">Caller-supplied deterministic root seed shared by the sequence.</param>
    /// <param name="count">Maximum number of items exposed by the returned sequence.</param>
    /// <returns>A lazy single-pass-compatible sequence in ascending zero-based sequence-index order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static IEnumerable<GeneratedObservation> EnumerateSequence(
        CompiledGenerationPlan plan,
        long seed,
        int count) =>
        EnumerateSequence(plan, seed, GenerationScope.Default, count);

    /// <summary>Lazily enumerates a bounded generated sequence in an isolated semantic scope.</summary>
    /// <param name="plan">Validated provider-neutral generation plan.</param>
    /// <param name="seed">Caller-supplied deterministic root seed shared by the sequence.</param>
    /// <param name="scope">Stable semantic namespace isolating this generated stream.</param>
    /// <param name="count">Maximum number of items exposed by the returned sequence.</param>
    /// <returns>A lazy single-pass-compatible sequence in ascending zero-based sequence-index order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is default or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static IEnumerable<GeneratedObservation> EnumerateSequence(
        CompiledGenerationPlan plan,
        long seed,
        GenerationScope scope,
        int count)
    {
        ArgumentNullException.ThrowIfNull(plan);
        GenerationScope.Validate(scope, nameof(scope));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Generation count cannot be negative.");

        return EnumerateSequenceCore(plan, seed, scope, count);
    }

    static IEnumerable<GeneratedObservation> EnumerateSequenceCore(
        CompiledGenerationPlan plan,
        long seed,
        GenerationScope scope,
        int count)
    {
        for (var index = 0; index < count; index++)
            yield return Generate(plan, seed, scope, index);
    }

    static ObservationValue Generate(ValueGeneratorNode generator, long seed, EntropyAddress address) => generator switch
    {
        ConstantGenerationNode constant => constant.Value,
        Int32GenerationNode integer => ObservationValue.FromInt64(GenerateInt32(integer, seed, address)),
        BernoulliGenerationNode bernoulli => ObservationValue.FromBool(
            bernoulli.Probability >= 1d
            || bernoulli.Probability > 0d && ToUnitInterval(AddressableEntropy.Next(seed, address, attempt: 0)) < bernoulli.Probability),
        WeightedCategoricalGenerationNode categorical => GenerateCategorical(categorical, seed, address),
        _ => throw new NotSupportedException(
            $"Reference generation interpreter does not support node '{generator.GetType().Name}'.")
    };

    static int GenerateInt32(Int32GenerationNode node, long seed, EntropyAddress address)
    {
        var range = checked((ulong)((long)node.Maximum - node.Minimum + 1L));
        var threshold = unchecked(0UL - range) % range;
        for (var attempt = 0; ; attempt++)
        {
            var sample = AddressableEntropy.Next(seed, address, attempt);
            if (sample >= threshold)
                return checked((int)(node.Minimum + (long)(sample % range)));
        }
    }

    static ObservationValue GenerateCategorical(
        WeightedCategoricalGenerationNode node,
        long seed,
        EntropyAddress address)
    {
        var totalWeight = 0d;
        foreach (var option in node.Options)
            totalWeight += option.Weight;

        var threshold = ToUnitInterval(AddressableEntropy.Next(seed, address, attempt: 0)) * totalWeight;
        var cumulative = 0d;
        for (var index = 0; index < node.Options.Length - 1; index++)
        {
            cumulative += node.Options[index].Weight;
            if (threshold < cumulative)
                return node.Options[index].Value;
        }

        return node.Options[^1].Value;
    }

    static double ToUnitInterval(ulong value) =>
        (value >> 11) * (1d / (1UL << 53));

    static void ValidateReplayCoordinates(
        CompiledGenerationPlan plan,
        GenerationReplayEvidence replay)
    {
        List<string> mismatches = [];
        AddMismatch(mismatches, "definition id", plan.Definition.Id, replay.DefinitionId);
        AddMismatch(mismatches, "definition revision", plan.Definition.Revision, replay.DefinitionRevision);
        AddMismatch(mismatches, "definition fingerprint", plan.Fingerprint, replay.DefinitionFingerprint);
        AddMismatch(mismatches, "interpreter", Identity, replay.Interpreter);
        AddMismatch(mismatches, "entropy algorithm", EntropyAlgorithm, replay.EntropyAlgorithm);
        if (mismatches.Count > 0)
        {
            throw new ArgumentException(
                $"Replay evidence is incompatible with the selected generation plan: {string.Join("; ", mismatches)}.",
                nameof(replay));
        }
    }

    static void AddMismatch(
        ICollection<string> mismatches,
        string coordinate,
        string expected,
        string observed)
    {
        if (!string.Equals(expected, observed, StringComparison.Ordinal))
            mismatches.Add($"{coordinate} expected '{expected}' but observed '{observed}'");
    }

    readonly record struct EntropyAddress(
        GenerationScope Scope,
        long SequenceIndex,
        string RecordIdentity,
        string MemberIdentity);

    static class AddressableEntropy
    {
        public static ulong Next(long seed, EntropyAddress address, int attempt)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, EntropyAlgorithm);
            Append(hash, seed);
            Append(hash, address.Scope.Value);
            Append(hash, address.SequenceIndex);
            Append(hash, address.RecordIdentity);
            Append(hash, address.MemberIdentity);
            Append(hash, attempt);
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            if (!hash.TryGetHashAndReset(digest, out var written) || written != digest.Length)
                throw new InvalidOperationException("Addressable entropy hash could not be completed.");
            return BinaryPrimitives.ReadUInt64BigEndian(digest);
        }

        static void Append(IncrementalHash hash, string value)
        {
            var byteCount = Encoding.UTF8.GetByteCount(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, byteCount);
            hash.AppendData(length);
            if (byteCount == 0)
                return;

            var rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                var written = Encoding.UTF8.GetBytes(value, rented);
                hash.AppendData(rented.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        static void Append(IncrementalHash hash, long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            hash.AppendData(bytes);
        }
    }
}

static class GenerationReplayTokenCodec
{
    const string Prefix = "csimr2.";

    public static string Encode(GenerationReplayEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var payload = StrictDocumentJson.GetCanonicalBytes(
            evidence,
            StrictDocumentJson.CreateOptions());
        return Prefix + Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static GenerationReplayEvidence Decode(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!token.StartsWith(Prefix, StringComparison.Ordinal) || token.Length == Prefix.Length)
            throw new FormatException($"A generation replay token must use the '{Prefix}' format.");

        var encoded = token[Prefix.Length..];
        byte[] payload;
        try
        {
            var paddingLength = (4 - encoded.Length % 4) % 4;
            var padded = encoded
                .Replace('-', '+')
                .Replace('_', '/')
                .PadRight(encoded.Length + paddingLength, '=');
            payload = Convert.FromBase64String(padded);
        }
        catch (FormatException exception)
        {
            throw new FormatException("Generation replay token payload is not URL-safe Base64.", exception);
        }

        var json = Encoding.UTF8.GetString(payload);
        if (!StrictDocumentJson.TryReadCanonicalObject(
                json,
                StrictDocumentJson.CreateOptions(),
                "generation replay evidence",
                out GenerationReplayEvidence? evidence,
                out var error)
            || evidence is null)
        {
            throw new FormatException(
                $"Generation replay token payload is invalid at '{error.Location}': {error.Message}");
        }

        if (!string.Equals(token, Encode(evidence), StringComparison.Ordinal))
            throw new FormatException("Generation replay token is not in canonical current-version form.");

        return evidence;
    }
}
