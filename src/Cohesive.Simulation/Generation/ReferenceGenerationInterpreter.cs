using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;

namespace Cohesive.Simulation.Generation;

/// <summary>Compact evidence required to reproduce one exact generated value.</summary>
public sealed record GenerationReplayEvidence
{
    /// <summary>Creates generation replay evidence.</summary>
    /// <param name="rootSeed">Caller-supplied deterministic root seed.</param>
    /// <param name="sequenceIndex">Stable zero-based item index within a generated sequence.</param>
    /// <param name="definitionId">Stable logical generation-definition identity.</param>
    /// <param name="definitionRevision">Exact authored generation-definition revision.</param>
    /// <param name="definitionFingerprint">Exact compiled semantic fingerprint.</param>
    /// <param name="interpreter">Generation interpreter identity and version.</param>
    /// <param name="entropyAlgorithm">Addressable entropy algorithm identity and version.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequenceIndex"/> is negative.</exception>
    /// <exception cref="ArgumentException">A string identity is empty or white-space.</exception>
    public GenerationReplayEvidence(
        long rootSeed,
        long sequenceIndex,
        string definitionId,
        string definitionRevision,
        string definitionFingerprint,
        string interpreter,
        string entropyAlgorithm)
    {
        if (sequenceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sequenceIndex), sequenceIndex, "Sequence index cannot be negative.");

        RootSeed = rootSeed;
        SequenceIndex = sequenceIndex;
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
    public const string Identity = "cohesive-simulation-reference/v1";

    /// <summary>Stable addressable entropy algorithm identity and version.</summary>
    public const string EntropyAlgorithm = "cohesive-addressable-sha256/v1";

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
        long sequenceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (sequenceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sequenceIndex), sequenceIndex, "Sequence index cannot be negative.");

        var fields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        foreach (var member in plan.Members)
        {
            var address = new EntropyAddress(
                sequenceIndex,
                plan.OutputShape.ShapeId.Value,
                member.Identity.Value);
            fields.Add(member.Identity.Value, Generate(member.Generator, seed, address));
        }

        var observation = Observation.Create(plan.OutputShape, fields.ToImmutable());
        var replay = new GenerationReplayEvidence(
            rootSeed: seed,
            sequenceIndex: sequenceIndex,
            definitionId: plan.Definition.Id,
            definitionRevision: plan.Definition.Revision,
            definitionFingerprint: plan.Fingerprint,
            interpreter: Identity,
            entropyAlgorithm: EntropyAlgorithm);
        return new(observation, replay);
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
        int count)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Generation count cannot be negative.");
        if (count == 0)
            return [];

        var generated = ImmutableArray.CreateBuilder<GeneratedObservation>(count);
        for (var index = 0; index < count; index++)
            generated.Add(Generate(plan, seed, index));
        return generated.MoveToImmutable();
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

    readonly record struct EntropyAddress(
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
