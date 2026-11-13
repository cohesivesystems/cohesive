using System.Collections.Immutable;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.IR;

namespace Cohesive.Integrations.Authoring;

/// <summary>Fluent source for a scoped Contract property. Selectors are captured as field paths;
/// they are never compiled to delegates or retained in canonical documents.</summary>
/// <typeparam name="TSelection">Structural selection type.</typeparam>
public sealed class PropertyHandle<TSelection> where TSelection : class
{
    internal IntegrationAuthoringContext Owner { get; }
    readonly string name;
    string? kind;
    ImmutableArray<ContractPredicateClassification> classifications = [];
    ExecutionDefinitionDocument? predicate;
    ExecutionDefinitionDocument? assumptions;
    readonly List<FieldPath> subjects = [];
    FieldPath? start;
    FieldPath? end;
    TemporalBoundaryInclusion endInclusion;
    string? keyDomain;
    string? timeBasis;
    string? boundaryName;
    IntegrationRequest? boundaryRequest;
    internal PropertyHandle(IntegrationAuthoringContext owner, string name) { Owner = owner; this.name = name; }

    /// <summary>Declares the shared property-kind identity.</summary>
    /// <param name="value">Built-in or domain-owned kind identity.</param>
    /// <returns>This property handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    public PropertyHandle<TSelection> Kind(string value) { Owner.Mutable(); kind = value; return this; }

    /// <summary>Classifies the assertion as both invariant and safety.</summary>
    /// <returns>This property handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    public PropertyHandle<TSelection> Invariant() => ClassifiedAs(ContractPredicateClassification.Safety, ContractPredicateClassification.Invariant);

    /// <summary>Declares overlapping predicate classifications.</summary>
    /// <param name="values">Classes normalized by the shared property lowering boundary.</param>
    /// <returns>This property handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    public PropertyHandle<TSelection> ClassifiedAs(params ContractPredicateClassification[] values)
    { Owner.Mutable(); classifications = [.. values]; return this; }

    /// <summary>Snapshots a domain-owned predicate declaration.</summary>
    /// <typeparam name="TPredicate">Portable structural predicate source.</typeparam>
    /// <param name="value">Predicate declaration whose mutable source is copied into canonical bytes immediately.</param>
    /// <returns>This property handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    /// <exception cref="ArgumentNullException">Value is null.</exception>
    /// <exception cref="NotSupportedException">The source contains unsupported executable values.</exception>
    public PropertyHandle<TSelection> Predicate<TPredicate>(TPredicate value) where TPredicate : class
    {
        Owner.Mutable();
        predicate = ContractPropertyDocuments.DeclarePredicate(Owner.Identity("properties/" + name + "/predicate"),
            Owner.Revision, value, Owner.Origin("properties/" + name + "/predicate"));
        return this;
    }

    /// <summary>Snapshots the assumptions, including the domain's failure model.</summary>
    /// <typeparam name="TAssumptions">Portable structural assumption source.</typeparam>
    /// <param name="value">Assumptions copied into canonical bytes immediately.</param>
    /// <returns>This property handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    /// <exception cref="ArgumentNullException">Value is null.</exception>
    public PropertyHandle<TSelection> Assume<TAssumptions>(TAssumptions value) where TAssumptions : class
    {
        Owner.Mutable();
        assumptions = ContractPropertyDocuments.DeclareContext(Owner.Identity("properties/" + name + "/assumptions"),
            Owner.Revision, value, Owner.Origin("properties/" + name + "/assumptions"));
        return this;
    }

    /// <summary>Groups scope authoring calls. The callback runs immediately during authoring.</summary>
    /// <param name="configure">Scope declaration callback; it is never retained.</param>
    /// <returns>This property handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    /// <exception cref="ArgumentNullException">Configure is null.</exception>
    public PropertyHandle<TSelection> Scope(Action<PropertyHandle<TSelection>> configure)
    { Owner.Mutable(); ArgumentNullException.ThrowIfNull(configure); configure(this); return this; }

    /// <summary>Captures one subject identity from the selection.</summary>
    /// <typeparam name="TSubject">Subject identity type whose canonical JSON representation is a string.</typeparam>
    /// <param name="selector">Direct or nested readable property selector; custom scalar identities are selected as a whole.</param>
    /// <returns>This property handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    /// <exception cref="ArgumentException">The selector contains executable expressions or captured values.</exception>
    public PropertyHandle<TSelection> Subject<TSubject>(Expression<Func<TSelection, TSubject>> selector)
    { Owner.Mutable(); subjects.Add(Capture(selector)); return this; }

    /// <summary>Declares the identity or ordering domain.</summary>
    /// <param name="value">Application-owned key-domain identity.</param>
    /// <returns>This property handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    public PropertyHandle<TSelection> KeyDomain(string value) { Owner.Mutable(); keyDomain = value; return this; }

    /// <summary>Scopes the assertion to an exact Request's successful result.</summary>
    /// <param name="request">Request declared in this model.</param>
    /// <returns>This property handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    /// <exception cref="ArgumentException">The Request belongs to another model.</exception>
    /// <exception cref="ArgumentNullException">Request is null.</exception>
    public PropertyHandle<TSelection> Boundary(IntegrationRequest request)
    {
        Owner.Mutable(); ArgumentNullException.ThrowIfNull(request); Owner.Owns(request.Owner);
        boundaryRequest = request; boundaryName = null; return this;
    }

    /// <summary>Scopes the assertion to an application-owned boundary.</summary>
    /// <param name="value">Stable boundary identity.</param>
    /// <returns>This property handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    public PropertyHandle<TSelection> Boundary(string value)
    { Owner.Mutable(); boundaryName = value; boundaryRequest = null; return this; }

    /// <summary>Declares an inclusive date start and explicitly bounded date end.</summary>
    /// <param name="from">Selection property containing the first date.</param>
    /// <param name="to">Selection property containing the final bound.</param>
    /// <param name="end">Whether the final bound belongs to the interval.</param>
    /// <returns>This property handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    /// <exception cref="ArgumentException">A selector is unsupported or the inclusion value is unknown.</exception>
    public PropertyHandle<TSelection> Interval(Expression<Func<TSelection, DateOnly>> from,
        Expression<Func<TSelection, DateOnly>> to, TemporalBoundaryInclusion end)
    {
        Owner.Mutable();
        if (!Enum.IsDefined(end)) throw new ArgumentOutOfRangeException(nameof(end));
        start = Capture(from); this.end = Capture(to); endInclusion = end; return this;
    }

    /// <summary>Declares the semantic clock for the interval.</summary>
    /// <param name="value">Application-owned time-basis identity.</param>
    /// <returns>This property handle.</returns>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    public PropertyHandle<TSelection> TimeBasis(string value) { Owner.Mutable(); timeBasis = value; return this; }

    internal ExecutionDefinitionDocument Lower(ExecutionDefinitionDocument selection,
        IReadOnlyDictionary<IntegrationRequest, RequestContractReference> requests, List<ExecutionDefinitionDocument> documents)
    {
        if (predicate is null || assumptions is null || subjects.Count == 0 || start is null || end is null
            || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(keyDomain) || string.IsNullOrWhiteSpace(timeBasis)
            || boundaryRequest is null && string.IsNullOrWhiteSpace(boundaryName))
            throw new InvalidOperationException($"Property '{name}' needs kind, predicate, assumptions, subjects, key domain, boundary, interval and time basis.");
        var first = DateOnly.Parse(Read(selection.Definition, start.Value).GetString()!, CultureInfo.InvariantCulture);
        var last = DateOnly.Parse(Read(selection.Definition, end.Value).GetString()!, CultureInfo.InvariantCulture);
        if (last < first || last == first && endInclusion == TemporalBoundaryInclusion.Exclusive)
            throw new InvalidOperationException($"Property '{name}' has an empty or reversed interval.");
        var prefix = "selections/" + selection.Metadata.Fingerprint.Value + "/properties/" + name;
        var interval = ContractPropertyDocuments.DeclareContext(Owner.Identity(prefix + "/interval"), Owner.Revision,
            new IntegrationDateInterval(first, last, endInclusion), Owner.Origin("properties/" + name + "/interval"));
        var boundary = boundaryRequest is null ? boundaryName! :
            requests[boundaryRequest].Definition.DefinitionId.Value + "/" + requests[boundaryRequest].Definition.RevisionId.Value
                + "/" + requests[boundaryRequest].Definition.Fingerprint.Value + "/results/" + boundaryRequest.ResultName;
        var property = ContractPropertyDocuments.Declare(Owner.Identity(prefix), Owner.Revision,
            new(kind, classifications, predicate.Reference,
                new([.. subjects.Select(path => Read(selection.Definition, path).GetString()!)], keyDomain, boundary, interval.Reference, timeBasis),
                selection.Reference, assumptions.Reference), Owner.Origin("properties/" + name));
        documents.Add(predicate); documents.Add(assumptions); documents.Add(interval); documents.Add(property);
        return property;
    }

    // Reuse the core path capture and its explicit rejection of arbitrary expressions.
    static FieldPath Capture<TValue>(Expression<Func<TSelection, TValue>> selector) => FieldPath.Capture(selector,
        static (_, members) => new([.. members.Select(member => FieldPathSegment.ForField(
            member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(member.Name)))]));

    static JsonElement Read(JsonElement value, FieldPath path)
    {
        foreach (var segment in path.Segments)
        {
            if (!segment.TryGetFieldIdentity(out var field) || value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(field, out value))
                throw new InvalidOperationException($"Selection does not contain field '{path}'.");
        }
        return value;
    }

}

/// <summary>Portable date interval emitted by property authoring and inspected by domain interpreters.</summary>
/// <param name="StartInclusive">Inclusive first date.</param>
/// <param name="End">Last boundary date, interpreted by EndInclusion.</param>
/// <param name="EndInclusion">Whether the end date belongs to the interval.</param>
public sealed record IntegrationDateInterval(DateOnly StartInclusive, DateOnly End, TemporalBoundaryInclusion EndInclusion);
