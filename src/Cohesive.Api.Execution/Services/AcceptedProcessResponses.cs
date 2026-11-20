namespace Cohesive.Api.Execution.Services;

internal enum AcceptedProcessRoute { Submit, Poll }

/// <summary>Stable polling token and local address issued for a durable or host-owned admission.</summary>
public sealed record AcceptedProcessLocation(string ResultToken, string ResultLocation);

/// <summary>HTTP contracts for application-specific outcomes of an accepted process interaction.</summary>
/// <remarks>Each projection supplies a body only. Its declared type and status drive both HTTP and API metadata.</remarks>
public sealed class AcceptedProcessResponses<TReceipt>
{
    internal sealed record Rule(Type Body, ApiHttpResultBinding Http, Func<AcceptedProcessLocation?, object?> Project);
    readonly Dictionary<(AcceptedProcessRoute Route, ApiResultKind Kind), Rule> rules = [];

    internal AcceptedProcessResponses() { }

    internal AcceptedProcessResponses(Func<AcceptedProcessLocation, TReceipt> receipt) =>
        Add(AcceptedProcessRoute.Submit, ApiResultKind.Accepted, 202, typeof(TReceipt), location => receipt(location!));

    readonly SortedSet<string> duplicates = new(StringComparer.Ordinal);

    internal void Validate(ApiOperation start, ApiOperation result)
    {
        if (duplicates.Count > 0)
            throw ServiceImplementationPlan.Error("services.api.duplicateOutcome", $"Duplicate accepted-process mappings: {string.Join(", ", duplicates)}.");
        foreach (var key in rules.Keys)
        {
            var operation = key.Route == AcceptedProcessRoute.Submit ? start : result;
            if (!operation.Results.Any(item => item.Kind == key.Kind))
                throw ServiceImplementationPlan.Error("services.api.undeclaredOutcome",
                    $"Operation '{operation.Id}' has no declared {key.Kind} outcome.");
        }
    }

    /// <summary>Use one typed body for malformed input and rejected submission input.</summary>
    public AcceptedProcessResponses<TReceipt> InvalidRequest<T>(int status, T body) =>
        Add(AcceptedProcessRoute.Submit, ApiResultKind.ValidationFailed, status, typeof(T), _ => body);

    /// <summary>Project a reused identity with a conflicting payload.</summary>
    public AcceptedProcessResponses<TReceipt> IdentityConflict<T>(int status, T body) =>
        Add(AcceptedProcessRoute.Submit, ApiResultKind.Conflict, status, typeof(T), _ => body);

    /// <summary>Return a bodyless response when admission capacity is exhausted.</summary>
    public AcceptedProcessResponses<TReceipt> AtCapacity(int status) => Empty(AcceptedProcessRoute.Submit, ApiResultKind.CapacityExceeded, status);

    /// <summary>Return a bodyless response while a polled instance is running.</summary>
    public AcceptedProcessResponses<TReceipt> Pending(int status) => Empty(AcceptedProcessRoute.Poll, ApiResultKind.Accepted, status);

    /// <summary>Return a bodyless response for unknown instances and invalid polling tokens.</summary>
    public AcceptedProcessResponses<TReceipt> Missing(int status) => Empty(AcceptedProcessRoute.Poll, ApiResultKind.NotFound, status);

    /// <summary>Project verified terminal failure. Storage and decoding failures retain their own handling.</summary>
    public AcceptedProcessResponses<TReceipt> ExecutionFailed<T>(int status, T body)
        => Add(AcceptedProcessRoute.Poll, ApiResultKind.ExecutionFailed, status, typeof(T), _ => body);

    internal Rule? Find(AcceptedProcessRoute route, ApiResultKind kind) => rules.GetValueOrDefault((route, kind));

    internal AcceptedProcessResponses<TReceipt> Snapshot()
    {
        var copy = new AcceptedProcessResponses<TReceipt>();
        copy.duplicates.UnionWith(duplicates);
        foreach (var entry in rules) copy.rules.Add(entry.Key, entry.Value);
        return copy;
    }

    AcceptedProcessResponses<TReceipt> Empty(AcceptedProcessRoute route, ApiResultKind kind, int status) =>
        Add(route, kind, status, typeof(void), _ => null);

    AcceptedProcessResponses<TReceipt> Add(AcceptedProcessRoute route, ApiResultKind kind, int status, Type body, Func<AcceptedProcessLocation?, object?> project)
    {
        if (!rules.TryAdd((route, kind), new(body, new(status, body == typeof(void) ? null : "application/json"), project)))
            duplicates.Add($"{route}.{kind}");
        return this;
    }

    internal ApiResultDefinition Apply(AcceptedProcessRoute route, ApiResultDefinition result) =>
        Find(route, result.Kind) is { } rule
            ? new(result.Kind, rule.Body, result.IsPrimary, result.Id, result.Description, rule.Http)
            : result;

    /// <summary>Render a declared alternative. Returns false when the default response applies.</summary>
    internal bool TryProject(AcceptedProcessRoute route, ApiResultKind kind, out AcceptedProcessResponse response,
        AcceptedProcessLocation? receipt = null)
    {
        var rule = Find(route, kind);
        if (rule is null) { response = default; return false; }
        response = new(rule.Http.StatusCode, rule.Body, rule.Project(receipt));
        return true;
    }
}

/// <summary>Materialized HTTP response according to the declared outcome contract.</summary>
public readonly record struct AcceptedProcessResponse(int Status, Type BodyType, object? Body);


/// <summary>Starts a typed accepted-process HTTP contract.</summary>
public static class AcceptedProcessResponses
{
    /// <summary>Selects the public admission receipt type and its projection.</summary>
    public static AcceptedProcessResponses<T> Receipt<T>(Func<AcceptedProcessLocation, T> body) =>
        new(body ?? throw new ArgumentNullException(nameof(body)));
}
