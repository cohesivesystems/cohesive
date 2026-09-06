using System.Collections.Immutable;
using System.Reflection;
using Cohesive.Model;
using Cohesive.Simulation.ExternalProcess;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Mimesis;

/// <summary>Application-owned inputs for one finite Mimesis generation-catalog import.</summary>
public sealed class MimesisGenerationCatalogImportOptions
{
    /// <summary>Creates exact semantic coordinates for one Mimesis snapshot import.</summary>
    /// <param name="id">Stable logical catalog identity.</param>
    /// <param name="revision">Exact application-owned catalog revision.</param>
    /// <param name="count">Positive number of Mimesis records to retain.</param>
    /// <param name="seed">Signed 64-bit import-local Mimesis seed.</param>
    /// <param name="sourceReferences">Exact application sources defining the record bindings and import.</param>
    /// <param name="locale">Explicit Mimesis locale used for the complete import; defaults to English.</param>
    /// <exception cref="ArgumentNullException">A required string or source reference is null.</exception>
    /// <exception cref="ArgumentException">
    /// An identity, revision, or locale is empty, or source references are empty, invalid, or duplicated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    public MimesisGenerationCatalogImportOptions(
        string id,
        string revision,
        int count,
        long seed,
        ImmutableArray<SourceReference> sourceReferences,
        string locale = "en")
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
        Count = count > 0
            ? count
            : throw new ArgumentOutOfRangeException(nameof(count), count, "A Mimesis import requires at least one record.");
        Seed = seed;
        SourceReferences = SourceReference.NormalizeSet(sourceReferences, requireNonEmpty: true);
        Locale = Guard.RequireNotNullOrWhiteSpace(locale);
    }

    /// <summary>Gets the stable logical catalog identity.</summary>
    public string Id { get; }

    /// <summary>Gets the exact application-owned catalog revision.</summary>
    public string Revision { get; }

    /// <summary>Gets the positive number of Mimesis records retained.</summary>
    public int Count { get; }

    /// <summary>Gets the signed 64-bit import-local Mimesis seed.</summary>
    public long Seed { get; }

    /// <summary>Gets normalized application sources defining the bindings and import.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Gets the explicit Mimesis locale used for the complete import.</summary>
    public string Locale { get; }
}

/// <summary>Explicit Python runtime location used by the bundled Mimesis provider.</summary>
public sealed class MimesisRuntimeOptions
{
    /// <summary>Creates Python runtime options without creating or mutating an environment.</summary>
    /// <param name="pythonExecutable">
    /// Python executable file name or path; defaults to <c>python</c> on Windows and <c>python3</c> elsewhere.
    /// </param>
    /// <param name="workingDirectory">Optional provider-process working directory.</param>
    /// <exception cref="ArgumentException">The executable or supplied working directory is empty.</exception>
    public MimesisRuntimeOptions(
        string? pythonExecutable = null,
        string? workingDirectory = null)
    {
        PythonExecutable = Guard.RequireNotNullOrWhiteSpace(
            pythonExecutable ?? (OperatingSystem.IsWindows() ? "python" : "python3"));
        WorkingDirectory = workingDirectory switch
        {
            null => null,
            { Length: > 0 } when !string.IsNullOrWhiteSpace(workingDirectory) => workingDirectory,
            _ => throw new ArgumentException("A Mimesis working directory cannot be empty.", nameof(workingDirectory))
        };
    }

    /// <summary>Gets the Python executable launched directly without a command shell.</summary>
    public string PythonExecutable { get; }

    /// <summary>Gets the optional provider-process working directory.</summary>
    public string? WorkingDirectory { get; }
}

/// <summary>Imports declaratively configured Mimesis records into exact portable generation catalogs.</summary>
/// <remarks>
/// Mimesis and the Python process are transient producers. The returned catalog embeds all generated values and
/// versioned provenance, so subsequent generation, replay, serialization, and provisioning do not require Python,
/// Mimesis, this package, or the original CLR expressions.
/// </remarks>
public static class MimesisGenerationCatalog
{
    const string PackageVersionMetadata = "CohesiveMimesisPackageVersion";
    const string ProviderVersionMetadata = "MimesisPackageVersion";
    const string TypingExtensionsVersionMetadata = "TypingExtensionsPackageVersion";
    const string ProviderResourceName = "Cohesive.Simulation.Mimesis.Provider.py";
    static readonly Lazy<string> ProviderSource = new(LoadProviderSource);

    /// <summary>Current closed declarative configuration schema understood by the bundled provider.</summary>
    public const string ConfigurationSchemaVersion = "cohesive-simulation-mimesis-record/v1";

    /// <summary>Stable identity of the current Mimesis capability and convention profile.</summary>
    public const string CapabilityProfileIdentity = "cohesive.simulation.mimesis/field-record-snapshot/v1";

    /// <summary>Stable external provider identity retained in catalog provenance.</summary>
    public const string ProviderIdentity = "Mimesis";

    /// <summary>Random-algorithm profile used for locally seeded Mimesis Field imports.</summary>
    public const string RandomAlgorithmIdentity = "Mimesis.Field/local-seed/v1";

    /// <summary>Gets the exact Cohesive Mimesis package version retained as capability evidence.</summary>
    public static string PackageVersion { get; } = RequirePackageVersion(PackageVersionMetadata);

    /// <summary>Gets the exact supported Mimesis Python package version.</summary>
    public static string ProviderVersion { get; } = RequirePackageVersion(ProviderVersionMetadata);

    /// <summary>Gets the exact required <c>typing_extensions</c> Python package version.</summary>
    public static string TypingExtensionsVersion { get; } = RequirePackageVersion(TypingExtensionsVersionMetadata);

    /// <summary>Gets complete versioned producer capability evidence retained in every imported catalog.</summary>
    public static GenerationCatalogCapabilityProfile CapabilityProfile { get; } = new(
        CapabilityProfileIdentity,
        [
            GenerationCatalogProducerCapability.FiniteSnapshot,
            GenerationCatalogProducerCapability.StructuredValues,
            GenerationCatalogProducerCapability.LocaleSelection,
            GenerationCatalogProducerCapability.LocalSeed
        ],
        [
            SourceReference.Repository(new("src/Cohesive.Simulation.Mimesis/README.md")),
            SourceReference.Create("nuget", $"Cohesive.Simulation.Mimesis/{PackageVersion}"),
            SourceReference.Create("pypi", $"mimesis/{ProviderVersion}"),
            SourceReference.Create("pypi", $"typing-extensions/{TypingExtensionsVersion}")
        ]);

    /// <summary>Defines a declarative Mimesis record snapshot using typed direct CLR member selectors.</summary>
    /// <typeparam name="T">Portable CLR object type represented by each catalog entry.</typeparam>
    /// <param name="configure">Callback that binds CLR members to fully qualified Mimesis fields.</param>
    /// <returns>An immutable closed provider definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException">The resulting definition is invalid or incomplete.</exception>
    public static MimesisRecordDefinition<T> Define<T>(Action<MimesisRecordBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        MimesisRecordBuilder<T> builder = new();
        configure(builder);
        return builder.Build();
    }

    /// <summary>Invokes pinned Mimesis once and retains its complete output as a portable generation catalog.</summary>
    /// <typeparam name="T">Portable CLR object type represented by every Mimesis record.</typeparam>
    /// <param name="definition">Closed typed member bindings and provider configuration.</param>
    /// <param name="options">Catalog identity, bound, locale, seed, and application-source evidence.</param>
    /// <param name="runtime">
    /// Optional explicit Python runtime location. The selected environment must already contain the pinned packages.
    /// </param>
    /// <param name="cancellationToken">Token that cancels invocation and terminates the provider process tree.</param>
    /// <returns>A fingerprinted finite catalog independent of the provider process.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The definition or its produced values violate the portable CLR contract.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    /// <exception cref="ExternalGenerationCatalogException">
    /// Python cannot start, the provider times out or fails, a required package version differs, protocol output is
    /// invalid, or produced values do not satisfy the requested contract.
    /// </exception>
    public static Task<GenerationCatalogDocument> ImportAsync<T>(
        MimesisRecordDefinition<T> definition,
        MimesisGenerationCatalogImportOptions options,
        MimesisRuntimeOptions? runtime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);
        runtime ??= new();

        var provider = new ExternalGenerationCatalogProvider(
            executable: runtime.PythonExecutable,
            arguments:
            [
                "-c",
                ProviderSource.Value,
                ProviderIdentity,
                ProviderVersion,
                TypingExtensionsVersion,
                ExternalGenerationCatalogProtocol.CurrentSchemaVersion,
                ConfigurationSchemaVersion
            ],
            provider: ProviderIdentity,
            providerVersion: ProviderVersion,
            randomAlgorithm: RandomAlgorithmIdentity,
            capabilityProfile: CapabilityProfile,
            workingDirectory: runtime.WorkingDirectory);
        var externalOptions = new ExternalGenerationCatalogImportOptions(
            id: options.Id,
            revision: options.Revision,
            count: options.Count,
            seed: options.Seed,
            configuration: definition.Configuration,
            sourceReferences: options.SourceReferences,
            locale: options.Locale);
        return ExternalGenerationCatalogImporter.ImportAsync<T>(provider, externalOptions, cancellationToken);
    }

    static string LoadProviderSource()
    {
        var assembly = typeof(MimesisGenerationCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ProviderResourceName)
            ?? throw new InvalidOperationException($"Embedded Mimesis provider '{ProviderResourceName}' is missing.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    static string RequirePackageVersion(string key)
    {
        foreach (var metadata in typeof(MimesisGenerationCatalog).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(metadata.Key, key, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(metadata.Value))
            {
                return metadata.Value;
            }
        }

        throw new InvalidOperationException($"Mimesis assembly metadata '{key}' is required.");
    }
}
