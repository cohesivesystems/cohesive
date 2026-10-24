using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Model;
using Cohesive.Prelude;

namespace Cohesive.Transitions.Authoring;

/// <summary>Assigns a CLR entity declaration to an explicitly selected domain module.</summary>
/// <param name="module">Marker type in the same assembly as this entity.</param>
/// <param name="name">Stable semantic entity name, independent of the CLR name.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class DomainEntityAttribute(Type module, string name) : Attribute
{
    /// <summary>The explicitly selected module.</summary>
    public Type Module { get; } = module ?? throw new ArgumentNullException(nameof(module));
    /// <summary>The semantic entity name.</summary>
    public string Name { get; } = Guard.RequireNotNullOrWhiteSpace(name);
}

/// <summary>Controls membership verification; shape equivalence is required in both modes.</summary>
public enum DomainDiscoveryCoverage
{
    /// <summary>All discovered, non-excluded entities must exist; additional explicit entities are allowed.</summary>
    DiscoveredEntities,
    /// <summary>The explicit and non-excluded discovered entity sets must match exactly.</summary>
    ExactMembership
}

/// <summary>Stable discovery and coverage diagnostic identifiers.</summary>
public static class DomainDiscoveryDiagnosticCodes
{
    /// <summary>Identifies entityMissing diagnostics.</summary>
    public const string EntityMissing = "domain.discovery.entityMissing";
    /// <summary>Identifies shapeMismatch diagnostics.</summary>
    public const string ShapeMismatch = "domain.discovery.shapeMismatch";
    /// <summary>Identifies entityUnexpected diagnostics.</summary>
    public const string EntityUnexpected = "domain.discovery.entityUnexpected";
    /// <summary>Identifies entityDuplicate diagnostics.</summary>
    public const string EntityDuplicate = "domain.discovery.entityDuplicate";
    /// <summary>Identifies typeUnsupported diagnostics.</summary>
    public const string TypeUnsupported = "domain.discovery.typeUnsupported";
    /// <summary>Identifies moduleEmpty diagnostics.</summary>
    public const string ModuleEmpty = "domain.discovery.moduleEmpty";
}

/// <summary>Inspectable discovery or verification failure retaining all diagnostics.</summary>
public sealed class DomainDiscoveryException : ArgumentException
{
    internal DomainDiscoveryException(DocumentValidationResult validation)
        : base(string.Join("; ", validation.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))) => Validation = validation;
    /// <summary>Stable codes, entity locations and explanations.</summary>
    public DocumentValidationResult Validation { get; }
}

/// <summary>A scoped, immutable authoring catalog. Reflection never survives into domain IR.</summary>
/// <remarks>Only attributed types in the selected module's assembly are considered. No loaded-assembly
/// scan, constructors, property getters, storage inference or transition inference is performed.
/// Catalogs are prepared once per module type; explicit exclusions retain their reasons.</remarks>
public sealed class DomainDiscovery
{
    readonly ImmutableDictionary<Type, EntityDefinition> declarations;
    DomainDiscovery(ImmutableDictionary<Type, EntityDefinition> declarations, ImmutableDictionary<Type, string> exclusions)
    {
        this.declarations = declarations;
        Exclusions = exclusions;
        Entities = [.. declarations.Where(pair => !exclusions.ContainsKey(pair.Key))
            .Select(pair => pair.Value).OrderBy(entity => entity.Name.Value, StringComparer.Ordinal)];
    }
    /// <summary>Canonical entity shapes in ordinal semantic-name order.</summary>
    public ImmutableArray<EntityDefinition> Entities { get; }
    /// <summary>Explicitly excluded declaration types and nonempty reasons.</summary>
    public ImmutableDictionary<Type, string> Exclusions { get; }

    /// <summary>Discovers marked entities belonging to one module, caching success or failure.</summary>
    /// <typeparam name="TModule">Module marker; only its assembly is scanned.</typeparam>
    /// <returns>The shared immutable declaration catalog.</returns>
    /// <exception cref="DomainDiscoveryException">Declarations conflict or the selected module is empty.</exception>
    /// <exception cref="ReflectionTypeLoadException">The selected assembly cannot be inspected completely.</exception>
    [RequiresUnreferencedCode("Domain discovery inspects attributed types and their public properties. Preserve them or use explicit entity registration.")]
    public static DomainDiscovery For<TModule>() => Cache<TModule>.Value.Value;

    /// <summary>Creates a catalog with an intentional omission, without modifying the cached catalog.</summary>
    /// <typeparam name="TEntity">A discovered declaration to omit.</typeparam>
    /// <param name="reason">Inspectable explanation of the omission.</param>
    /// <returns>An independently filtered catalog.</returns>
    /// <exception cref="ArgumentException">The reason is empty or the entity is outside the selected module.</exception>
    public DomainDiscovery Exclude<TEntity>(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!declarations.ContainsKey(typeof(TEntity)))
            throw new ArgumentException($"'{typeof(TEntity)}' is not declared in this module.");
        return new(declarations, Exclusions.SetItem(typeof(TEntity), reason));
    }

    /// <summary>Checks discovered entity coverage and canonical state shapes without changing either model.</summary>
    /// <param name="model">The explicit canonical model.</param>
    /// <param name="coverage">Whether additional explicit entities are allowed.</param>
    /// <returns>All membership and shape diagnostics in deterministic order.</returns>
    /// <exception cref="ArgumentNullException">The model is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The coverage mode is unknown.</exception>
    /// <remarks>Explicit invariants and transitions are not inferred or compared. This verifies entity
    /// state-shape coverage, not the correctness or completeness of domain behavior.</remarks>
    public DocumentValidationResult Verify(DomainModelDefinition model,
        DomainDiscoveryCoverage coverage = DomainDiscoveryCoverage.DiscoveredEntities)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!Enum.IsDefined(coverage)) throw new ArgumentOutOfRangeException(nameof(coverage));
        List<DocumentValidationDiagnostic> diagnostics = [];
        var actual = model.Entities.ToDictionary(entity => entity.Name.Value, StringComparer.Ordinal);
        foreach (var entity in Entities)
        {
            if (!actual.Remove(entity.Name.Value, out var declared))
                diagnostics.Add(Error(DomainDiscoveryDiagnosticCodes.EntityMissing, entity.Name.Value, "Discovered entity is not registered."));
            else if (entity.StateShape.QualifiedId != declared.StateShape.QualifiedId)
                diagnostics.Add(Error(DomainDiscoveryDiagnosticCodes.ShapeMismatch, entity.Name.Value, "Explicit and discovered canonical state shapes differ."));
        }
        if (coverage == DomainDiscoveryCoverage.ExactMembership)
            foreach (var name in actual.Keys.Order(StringComparer.Ordinal))
                diagnostics.Add(Error(DomainDiscoveryDiagnosticCodes.EntityUnexpected, name, "Explicit entity is outside the selected discovery set."));
        return new([.. diagnostics]);
    }

    static DocumentValidationDiagnostic Error(string code, string name, string message) =>
        new(code, DiagnosticSeverity.Error, $"Entity '{name}': {message}",
            "/domain/entities/" + name.Replace("~", "~0").Replace("/", "~1"));

    static class Cache<TModule>
    {
        internal static readonly Lazy<DomainDiscovery> Value = new(() => Discover(typeof(TModule)));
    }
    static DomainDiscovery Discover(Type module)
    {
        var declarations = ImmutableDictionary.CreateBuilder<Type, EntityDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        List<DocumentValidationDiagnostic> diagnostics = [];
        foreach (var type in module.Assembly.GetTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var metadata = type.CustomAttributes.SingleOrDefault(attribute => attribute.AttributeType == typeof(DomainEntityAttribute));
            if (metadata is null || !Equals(metadata.ConstructorArguments[0].Value, module)) continue;
            var attribute = type.GetCustomAttribute<DomainEntityAttribute>()!;
            if (!names.Add(attribute.Name))
            {
                diagnostics.Add(Error(DomainDiscoveryDiagnosticCodes.EntityDuplicate, attribute.Name, $"Multiple CLR declarations claim this identity; including '{type}'."));
                continue;
            }
            if (type.ContainsGenericParameters)
            {
                diagnostics.Add(Error(DomainDiscoveryDiagnosticCodes.TypeUnsupported, attribute.Name, "Open generic declarations cannot be projected."));
                continue;
            }
            declarations.Add(type, ObjectEntityDefinition.For(type, new(attribute.Name)));
        }
        if (diagnostics.Count > 0) throw new DomainDiscoveryException(new([.. diagnostics]));
        if (declarations.Count == 0)
            throw new DomainDiscoveryException(new([Error(DomainDiscoveryDiagnosticCodes.ModuleEmpty, module.FullName!, "No marked entities belong to this module.")]));
        return new(declarations.ToImmutable(), ImmutableDictionary<Type, string>.Empty);
    }
}
