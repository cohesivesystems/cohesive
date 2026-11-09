using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Contracts;

/// <summary>Participation of a property in contract propagation, as defined by the System Model.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ContractRole
{
    /// <summary>A capability that the context must supply.</summary>
    Requires,
    /// <summary>A capability offered under declared assumptions.</summary>
    Provides,
    /// <summary>Changes, emissions, failures or resource use propagated forward.</summary>
    Effects,
    /// <summary>Contextual requirements propagated backward.</summary>
    Coeffects,
    /// <summary>A property promised within the declared operating envelope.</summary>
    Guarantees,
    /// <summary>A premise on which a claim depends.</summary>
    Assumptions,
    /// <summary>Support for trusting a capability or realization.</summary>
    Evidence
}

/// <summary>Overlapping predicate classifications. Quantitative bounds are separate annotations.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ContractPredicateClassification
{
    /// <summary>Excludes bad behavior.</summary>
    Safety,
    /// <summary>Requires eventual progress under explicit environmental assumptions.</summary>
    Liveness,
    /// <summary>A state predicate holding in every reachable state; also requires Safety.</summary>
    Invariant,
    /// <summary>A claim about a population or distribution.</summary>
    Aggregate,
    /// <summary>A claim relating multiple runs.</summary>
    Hyperproperty
}

/// <summary>Open property-kind vocabulary from the System Model. Domain extensions use stable qualified identities.</summary>
public static class ContractPropertyKinds
{
    /// <summary>Time and progress properties.</summary>
    public const string Temporal = "temporal";
    /// <summary>Resource requirements and effects.</summary>
    public const string Resource = "resource";
    /// <summary>Authority and permission properties.</summary>
    public const string Authority = "authority";
    /// <summary>Knowledge, freshness and evidence admissibility properties.</summary>
    public const string Epistemic = "epistemic";
    /// <summary>Integrity of domain data and its relationships.</summary>
    public const string DataIntegrity = "data-integrity";
}

/// <summary>Structured applicability of a property. Interval semantics belong to its exact context document.</summary>
/// <param name="Subjects">Stable subject identities, in canonical ordinal order.</param>
/// <param name="KeyDomain">Identity or ordering domain within which the property applies.</param>
/// <param name="Boundary">Declared boundary, such as publication commitment or an observation.</param>
/// <param name="Interval">Exact application context defining interval bounds and clock interpretation.</param>
/// <param name="TimeBasis">Meaning of time in the interval, such as exchange-session valid time.</param>
public sealed record ContractPropertyScope(ImmutableArray<string> Subjects, string KeyDomain, string Boundary,
    ExecutionDefinitionReference Interval, string TimeBasis);

/// <summary>One authoritative scoped assertion, reused by requirements, offers and other roles.
/// Predicate and context documents are portable application-authored source projections. A domain
/// interpreter must understand their semantics before claiming discharge or execution conformance.</summary>
/// <param name="Kind">Shared property-kind identity, independent of its contract role.</param>
/// <param name="Classification">Distinct overlapping classes in enum order; invariants include Safety.</param>
/// <param name="Predicate">Exact domain predicate declaration.</param>
/// <param name="Scope">Structured subject, ordering, boundary and temporal applicability.</param>
/// <param name="Parameters">Exact predicate parameter context.</param>
/// <param name="Assumptions">Exact premises, including the applicable failure model.</param>
/// <param name="QuantitativeBound">Optional exact annotation declaring limit, unit, measurement interval and time basis.</param>
public sealed record ContractPropertyDefinition(string Kind, ImmutableArray<ContractPredicateClassification> Classification,
    ExecutionDefinitionReference Predicate, ContractPropertyScope Scope, ExecutionDefinitionReference Parameters,
    ExecutionDefinitionReference Assumptions, ExecutionDefinitionReference? QuantitativeBound = null);

/// <summary>A role-specific use of the same exact property assertion.</summary>
/// <param name="Role">How this use participates in contract propagation.</param>
/// <param name="Property">Exact property identity, revision and fingerprint.</param>
public sealed record ContractProperty(ContractRole Role, ExecutionDefinitionReference Property);

/// <summary>Inspection of one property use after its dependency links have been validated.</summary>
/// <param name="Use">Role and exact assertion identity.</param>
/// <param name="Definition">Validated assertion with its structured scope and dependencies.</param>
public sealed record ResolvedContractProperty(ContractProperty Use, ContractPropertyDefinition Definition);
