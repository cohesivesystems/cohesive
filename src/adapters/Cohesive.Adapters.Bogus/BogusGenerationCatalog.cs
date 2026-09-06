using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Simulation.Generation;
using BogusFaker = global::Bogus.Faker;
using BogusRandomizer = global::Bogus.Randomizer;

namespace Cohesive.Adapters.Bogus;

/// <summary>Closed facilities provided by the current Bogus generation-catalog import profile.</summary>
[Flags]
public enum BogusGenerationCatalogCapability
{
    /// <summary>No catalog-import capability is asserted.</summary>
    None = 0,

    /// <summary>Materializes a finite provider-produced sample as exact portable catalog entries.</summary>
    FiniteSnapshot = 1,

    /// <summary>Selects one explicit Bogus locale for the complete import.</summary>
    LocaleSelection = 2,

    /// <summary>Uses one importer-owned local randomizer rather than Bogus global random state.</summary>
    LocalSeed = 4,

    /// <summary>Uses the profile's fixed UTC date-time reference for Bogus date providers.</summary>
    FixedUtcDateTimeReference = 8,

    /// <summary>All facilities in the current import profile.</summary>
    All = FiniteSnapshot | LocaleSelection | LocalSeed | FixedUtcDateTimeReference
}

/// <summary>Bounded inputs governing one Bogus generation-catalog import.</summary>
public sealed class BogusGenerationCatalogImportOptions
{
    /// <summary>Creates exact inputs for one finite catalog import.</summary>
    /// <param name="id">Stable logical catalog identity.</param>
    /// <param name="revision">Exact application-owned catalog revision.</param>
    /// <param name="count">Positive number of provider samples to retain.</param>
    /// <param name="seed">Local Bogus randomizer seed.</param>
    /// <param name="sourceReferences">
    /// Exact application sources defining the transient producer callback. Adapter and provider package references are
    /// attached automatically.
    /// </param>
    /// <param name="locale">Explicit Bogus locale used by every sample; defaults to Bogus English.</param>
    /// <exception cref="ArgumentNullException">A string parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity, revision, or locale is empty, or source references are empty, invalid, or duplicated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    public BogusGenerationCatalogImportOptions(
        string id,
        string revision,
        int count,
        int seed,
        ImmutableArray<SourceReference> sourceReferences,
        string locale = "en")
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "A Bogus catalog import requires at least one sample.");

        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
        Count = count;
        Seed = seed;
        Locale = Guard.RequireNotNullOrWhiteSpace(locale);
        SourceReferences = SourceReference.NormalizeSet(sourceReferences, requireNonEmpty: true);
    }

    /// <summary>Gets the stable logical catalog identity.</summary>
    public string Id { get; }

    /// <summary>Gets the exact application-owned catalog revision.</summary>
    public string Revision { get; }

    /// <summary>Gets the number of provider samples retained as weighted catalog entries.</summary>
    public int Count { get; }

    /// <summary>Gets the local Bogus randomizer seed.</summary>
    public int Seed { get; }

    /// <summary>Gets the explicit Bogus locale.</summary>
    public string Locale { get; }

    /// <summary>Gets normalized application sources defining the transient producer callback.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }
}

/// <summary>Imports bounded Bogus output into exact portable generation-catalog authority.</summary>
/// <remarks>
/// Bogus objects and callbacks exist only while <see cref="Import{TValue}"/> runs. The returned catalog embeds exact
/// values and complete provider evidence, so subsequent generation, replay, serialization, and provisioning require
/// neither this adapter nor Bogus. Import reproducibility additionally depends on the caller-provided producer using
/// only the supplied <see cref="BogusFaker"/> and deterministic application state.
/// </remarks>
public static class BogusGenerationCatalog
{
    const string AdapterPackageVersionMetadata = "CohesiveAdapterPackageVersion";
    const string BogusPackageVersionMetadata = "BogusPackageVersion";

    static readonly DefaultClrTypeRefMapper TypeMapper = new();
    static readonly SourceReference ProfileSource = SourceReference.Repository(
        new("src/adapters/Cohesive.Adapters.Bogus/README.md"));

    /// <summary>Stable identity of the current adapter capability and convention profile.</summary>
    public const string CapabilityProfileIdentity = "cohesive.adapters.bogus/catalog-snapshot/v1";

    /// <summary>Stable external provider identity retained in catalog provenance.</summary>
    public const string ProviderIdentity = "Bogus";

    /// <summary>Random-algorithm profile used for locally seeded Bogus imports.</summary>
    public const string RandomAlgorithmIdentity = "Bogus.Randomizer/local-seed/v1";

    /// <summary>Gets facilities asserted by the current adapter profile.</summary>
    public static BogusGenerationCatalogCapability Capabilities => BogusGenerationCatalogCapability.All;

    /// <summary>Gets the fixed UTC reference used by Bogus date providers during import.</summary>
    public static DateTime DateTimeReference { get; } = DateTime.UnixEpoch;

    /// <summary>Gets the exact adapter package version retained in produced catalog provenance.</summary>
    public static string AdapterVersion { get; } = RequirePackageVersion(AdapterPackageVersionMetadata);

    /// <summary>Gets the exact Bogus package version retained in produced catalog provenance.</summary>
    public static string ProviderVersion { get; } = RequirePackageVersion(BogusPackageVersionMetadata);

    /// <summary>Materializes a bounded, locally seeded Bogus sample into a portable catalog.</summary>
    /// <typeparam name="TValue">CLR value type returned by the transient provider callback.</typeparam>
    /// <param name="options">Exact identity, bound, locale, seed, and application-source evidence.</param>
    /// <param name="produce">Transient callback that creates one coherent value from the importer-owned faker.</param>
    /// <param name="cancellationToken">Token checked before faker creation and before every provider invocation.</param>
    /// <returns>
    /// A strict fingerprinted catalog whose equally weighted entries are the complete executable generation authority.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> or <paramref name="produce"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    /// <exception cref="ArgumentException">
    /// A produced value does not satisfy the inferred portable <typeparamref name="TValue"/> contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="TValue"/> or a produced runtime value has no portable observation representation.
    /// </exception>
    public static GenerationCatalogDocument Import<TValue>(
        BogusGenerationCatalogImportOptions options,
        Func<BogusFaker, TValue> produce,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(produce);
        cancellationToken.ThrowIfCancellationRequested();

        BogusFaker faker = new(options.Locale)
        {
            Random = new BogusRandomizer(options.Seed),
            DateTimeReference = DateTimeReference
        };
        var entries = ImmutableArray.CreateBuilder<GenerationCatalogEntry>(options.Count);
        for (var index = 0; index < options.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(new(
                $"sample/{index.ToString("D8", CultureInfo.InvariantCulture)}",
                ObservationValue.FromObject(produce(faker))));
        }

        return GenerationCatalogDocument.FromDefinition(new(
            options.Id,
            options.Revision,
            TypeMapper.Map(typeof(TValue), nullability: null),
            entries.MoveToImmutable(),
            new(
                adapter: CapabilityProfileIdentity,
                adapterVersion: AdapterVersion,
                provider: ProviderIdentity,
                providerVersion: ProviderVersion,
                locale: options.Locale,
                randomAlgorithm: RandomAlgorithmIdentity,
                seed: options.Seed.ToString(CultureInfo.InvariantCulture),
                sourceReferences: CompleteSourceReferences(options.SourceReferences))));
    }

    static ImmutableArray<SourceReference> CompleteSourceReferences(ImmutableArray<SourceReference> applicationSources)
    {
        var references = ImmutableArray.CreateBuilder<SourceReference>(applicationSources.Length + 3);
        references.AddRange(applicationSources);
        AddIfMissing(references, ProfileSource);
        AddIfMissing(references, SourceReference.Create("nuget", $"Cohesive.Adapters.Bogus/{AdapterVersion}"));
        AddIfMissing(references, SourceReference.Create("nuget", $"Bogus/{ProviderVersion}"));
        return SourceReference.NormalizeSet(references.ToImmutable(), requireNonEmpty: true);
    }

    static void AddIfMissing(ImmutableArray<SourceReference>.Builder references, SourceReference candidate)
    {
        if (!references.Contains(candidate))
            references.Add(candidate);
    }

    static string RequirePackageVersion(string key)
    {
        foreach (var metadata in typeof(BogusGenerationCatalog).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(metadata.Key, key, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(metadata.Value))
                return metadata.Value;
        }

        throw new InvalidOperationException($"Adapter assembly metadata '{key}' is required.");
    }
}
