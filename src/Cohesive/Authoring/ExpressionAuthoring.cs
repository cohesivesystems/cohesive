using Cohesive.Model;
namespace Cohesive.Authoring;

// Only framework-owned markers may be read from captured closure fields. Never invoke their getters.
internal interface IExpressionParameterMarker
{
    string ParameterId { get; }
    object? Owner => null;
    bool IsProvablyNonNull => true;
}

/// <summary>Portable binding identity plus CLR metadata used only while lowering source.</summary>
/// <remarks>No CLR type, resolver, or owner from this handle survives in canonical expressions.</remarks>
public abstract class ExpressionBinding
{
    internal ExpressionBinding(object owner, ValueBindingId id, TypeRef type, QualifiedShapeId? shape,
        ClrMemberPathResolver? memberPathResolver, Func<Type, TypeRef>? typeResolver, bool usesImportedMapping)
    {
        Owner = owner;
        Id = id;
        Type = Guard.RequireNotNull(type);
        Shape = shape;
        MemberPathResolver = memberPathResolver;
        TypeResolver = typeResolver;
        UsesImportedMapping = usesImportedMapping;
    }

    internal object Owner { get; }
    internal abstract Type ClrType { get; }
    internal ClrMemberPathResolver? MemberPathResolver { get; }
    internal Func<Type, TypeRef>? TypeResolver { get; }
    internal bool UsesImportedMapping { get; }

    /// <summary>Canonical identity of this input.</summary>
    public ValueBindingId Id { get; }
    /// <summary>Portable type of this input.</summary>
    public TypeRef Type { get; }
    /// <summary>Exact shape identity, or null for a value without a root shape.</summary>
    public QualifiedShapeId? Shape { get; }
    /// <summary>Returns the canonical binding identity.</summary>
    /// <returns>The binding identifier.</returns>
    public override string ToString() => Id.ToString();
}

/// <summary>A typed input usable by shared expression consumers.</summary>
/// <typeparam name="T">CLR projection of the input contract.</typeparam>
public interface IExpressionInput<T> where T : notnull
{
    /// <summary>Existing binding metadata; it does not execute or retrieve a value.</summary>
    ExpressionBinding Binding { get; }
}

/// <summary>An input declared in a neutral expression authoring session.</summary>
/// <typeparam name="T">CLR projection of the input contract.</typeparam>
public sealed class ExpressionInput<T> : ExpressionBinding, IExpressionInput<T> where T : notnull
{
    internal ExpressionInput(ExpressionAuthoring owner, ValueBindingId id, ClrAuthoringShape<T> shape)
        : base(owner, id, shape.Type, shape.Id, shape.ResolveMemberPath, shape.ResolveType,
            shape.IdentityOrigin == ClrIdentityOrigin.Imported) { }

    internal override Type ClrType => typeof(T);
    ExpressionBinding IExpressionInput<T>.Binding => this;
}

/// <summary>Declares typed inputs without creating query nodes or selecting an execution domain.</summary>
/// <remarks>
/// A session is authoring-time, single-threaded state. Automatic identifiers follow declaration order;
/// explicit identifiers are recommended for stable identity across source reordering.
/// </remarks>
public sealed class ExpressionAuthoring
{
    readonly HashSet<ValueBindingId> identities = [];
    int nextInput;

    /// <summary>Creates a session with explicit or default CLR metadata policy.</summary>
    /// <param name="context">Metadata context, or null for an isolated default context.</param>
    public ExpressionAuthoring(ClrAuthoringContext? context = null) => Clr = context ?? new();

    /// <summary>Shape metadata authority, including explicit and imported shape registrations.</summary>
    public ClrAuthoringContext Clr { get; }

    /// <summary>Declares an input using the registered or convention-derived shape.</summary>
    /// <typeparam name="T">CLR projection of the input.</typeparam>
    /// <param name="id">Explicit binding identity, or null for declaration-order identity.</param>
    /// <returns>A typed input owned by this session.</returns>
    /// <exception cref="ArgumentException">The identity is empty or already declared.</exception>
    /// <exception cref="InvalidOperationException">The CLR shape cannot be inferred consistently.</exception>
    public ExpressionInput<T> Input<T>(string? id = null) where T : notnull => Input(Clr.Shape<T>(), id);

    /// <summary>Declares an input using an exact registered or imported shape.</summary>
    /// <typeparam name="T">CLR projection of the input.</typeparam>
    /// <param name="shape">Authoritative shape and its field mapping.</param>
    /// <param name="id">Explicit binding identity, or null for declaration-order identity.</param>
    /// <returns>A typed input retaining the shape's exact mapping.</returns>
    /// <exception cref="ArgumentNullException">The shape is null.</exception>
    /// <exception cref="ArgumentException">The identity is empty or already declared.</exception>
    public ExpressionInput<T> Input<T>(ClrAuthoringShape<T> shape, string? id = null) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(shape);
        ValueBindingId identity;
        if (id is null)
        {
            do { identity = new("input/" + nextInput++.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            while (identities.Contains(identity));
        }
        else identity = new(Guard.RequireNotNullOrWhiteSpace(id));
        if (!identities.Add(identity)) throw new ArgumentException("Input identity is already declared in this session.", nameof(id));
        return new(this, identity, shape);
    }
}
