namespace Cohesive.Relations.Queries;

/// <summary>
/// Base join specification over observed records.
/// </summary>
/// <param name="Alias">The alias used to reference the joined observation or collection.</param>
/// <param name="Cardinality">The number of joined records produced for each input row.</param>
/// <param name="FromAlias">The previously hydrated alias that provides the source key for nested joins, if any.</param>
/// <param name="Source">The source from which joined observations are loaded via <see cref="IReadRepository"/>.</param>
/// <param name="Options">Optional field selection applied when loading joined observations.</param>
/// <param name="SourcePredicate">Optional predicate applied to joined observations before they are attached to the join result.</param>
public abstract record JoinSpec(
    string Alias,
    JoinCardinality Cardinality,
    string? FromAlias,
    QuerySource Source,
    FieldSelection? Options,
    EntityPredicate? SourcePredicate
);

/// <summary>
/// Join cardinality used by projection plans.
/// </summary>
public enum JoinCardinality
{
    /// <summary>Represents a one-to-one join.</summary>
    One = 0,
    
    /// <summary>Represents a one-to-many join.</summary>
    Many = 1
}

/// <summary>
/// One-to-one join resolved directly from a root observation field.
/// </summary>
/// <param name="Alias">The alias used to reference the joined observation.</param>
/// <param name="Source">The source from which joined observations are loaded.</param>
/// <param name="RootKeyField">The root observation field containing the joined observation id.</param>
/// <param name="Options">Optional field selection applied when loading joined observations.</param>
/// <param name="SourcePredicate">Optional predicate applied to joined observations before they are attached to the join result.</param>
public sealed record OneJoinSpec(
    string Alias,
    QuerySource Source,
    string RootKeyField,
    FieldSelection? Options = null,
    EntityPredicate? SourcePredicate = null
    ) : JoinSpec(Alias, JoinCardinality.One, FromAlias: null, Source, Options, SourcePredicate);

/// <summary>
/// One-to-one join resolved from a previously hydrated alias.
/// </summary>
/// <param name="Alias">The alias used to reference the joined observation.</param>
/// <param name="FromAlias">The previously hydrated alias whose joined observation provides the source key.</param>
/// <param name="Source">The source from which joined observations are loaded.</param>
/// <param name="SourceKeyField">The field on the previously joined observation containing the next joined observation id.</param>
/// <param name="Options">Optional field selection applied when loading joined observations.</param>
/// <param name="SourcePredicate">Optional predicate applied to joined observations before they are attached to the join result.</param>
public sealed record OneJoinFromSpec(
    string Alias,
    string FromAlias,
    QuerySource Source,
    string SourceKeyField,
    FieldSelection? Options = null,
    EntityPredicate? SourcePredicate = null
    ) : JoinSpec(Alias, JoinCardinality.One, FromAlias, Source, Options, SourcePredicate);

/// <summary>
/// One-to-many join resolved from a root observation field and a foreign-key field.
/// </summary>
/// <param name="Alias">The alias used to reference the joined observation collection.</param>
/// <param name="Source">The source from which joined observations are loaded.</param>
/// <param name="RootKeyPath">The root observation field path providing the join key or keys.</param>
/// <param name="ForeignKeyField">The joined observation field matched against the root key.</param>
/// <param name="Options">Optional field selection applied when loading joined observations.</param>
/// <param name="SourcePredicate">Optional predicate applied to joined observations before they are attached to the join result.</param>
public sealed record ManyJoinSpec(
    string Alias,
    QuerySource Source,
    FieldPath RootKeyPath,
    string ForeignKeyField,
    FieldSelection? Options = null,
    EntityPredicate? SourcePredicate = null
) : JoinSpec(Alias, JoinCardinality.Many, FromAlias: null, Source, Options, SourcePredicate)
{
    /// <summary>Initializes a new instance of the many join spec type.</summary>
    public ManyJoinSpec(
        string Alias,
        QuerySource Source,
        string RootKeyField,
        string ForeignKeyField,
        FieldSelection? Options = null,
        EntityPredicate? SourcePredicate = null
        )
        : this(Alias, Source, FieldPath.Parse(RootKeyField), ForeignKeyField, Options, SourcePredicate)
    {
    }
}
