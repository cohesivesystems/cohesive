namespace Cohesive.Api.Execution.Services;

/// <summary>Stable polling token and local address issued for a durable or host-owned admission.</summary>
public sealed record AcceptedProcessLocation(string ResultToken, string ResultLocation);

/// <summary>HTTP contracts for application-specific outcomes of an accepted process interaction.</summary>
/// <remarks>Each projection supplies a body only. Its declared type and status drive both HTTP and API metadata.</remarks>
public sealed class AcceptedProcessResponses<TReceipt>
{
    internal sealed record Rule(Type Body, ApiHttpResultBinding Http, Func<AcceptedProcessLocation?, object?> Project);
    readonly Dictionary<(bool Start, ApiResultKind Kind), Rule> rules = [];

    internal AcceptedProcessResponses() { }

    internal AcceptedProcessResponses(Func<AcceptedProcessLocation, TReceipt> receipt) =>
        Add(true, ApiResultKind.Accepted, 202, typeof(TReceipt), location => receipt(location!));

    string? duplicate;

    internal void Validate(ApiOperation start, ApiOperation result)
    {
        if (duplicate is not null)
            throw ServiceImplementationPlan.Error("services.api.duplicateOutcome", $"Duplicate accepted-process mapping: {duplicate}.");
        foreach (var key in rules.Keys)
        {
            var operation = key.Start ? start : result;
            if (!operation.Results.Any(item => item.Kind == key.Kind))
                throw ServiceImplementationPlan.Error("services.api.undeclaredOutcome",
                    $"Operation '{operation.Id}' has no declared {key.Kind} outcome.");
        }
    }

    /// <summary>Use one typed body for malformed input and rejected submission input.</summary>
    public AcceptedProcessResponses<TReceipt> InvalidRequest<T>(int status, T body) =>
        Add(true, ApiResultKind.ValidationFailed, status, typeof(T), _ => body);

    /// <summary>Project a reused identity with a conflicting payload.</summary>
    public AcceptedProcessResponses<TReceipt> IdentityConflict<T>(int status, T body) =>
        Add(true, ApiResultKind.Conflict, status, typeof(T), _ => body);

    /// <summary>Return a bodyless response when admission capacity is exhausted.</summary>
    public AcceptedProcessResponses<TReceipt> AtCapacity(int status) => Empty(true, ApiResultKind.CapacityExceeded, status);

    /// <summary>Return a bodyless response while a polled instance is running.</summary>
    public AcceptedProcessResponses<TReceipt> Pending(int status) => Empty(false, ApiResultKind.Accepted, status);

    /// <summary>Return a bodyless response for unknown instances and invalid polling tokens.</summary>
    public AcceptedProcessResponses<TReceipt> Missing(int status) => Empty(false, ApiResultKind.NotFound, status);

    /// <summary>Project verified terminal failure. Storage and decoding failures retain their own handling.</summary>
    public AcceptedProcessResponses<TReceipt> ExecutionFailed<T>(int status, T body)
    {
        if (Failure is not null) duplicate = nameof(ExecutionFailed);
        Failure = new(typeof(T), new(status), _ => body);
        return this;
    }

    internal Rule? Failure { get; private set; }
    internal Rule? Find(bool start, ApiResultKind kind) => rules.GetValueOrDefault((start, kind));

    internal AcceptedProcessResponses<TReceipt> Snapshot()
    {
        var copy = new AcceptedProcessResponses<TReceipt> { Failure = Failure, duplicate = duplicate };
        foreach (var entry in rules) copy.rules.Add(entry.Key, entry.Value);
        return copy;
    }

    AcceptedProcessResponses<TReceipt> Empty(bool start, ApiResultKind kind, int status) =>
        Add(start, kind, status, typeof(void), _ => null);

    AcceptedProcessResponses<TReceipt> Add(bool start, ApiResultKind kind, int status, Type body, Func<AcceptedProcessLocation?, object?> project)
    {
        if (!rules.TryAdd((start, kind), new(body, new(status, body == typeof(void) ? null : "application/json"), project)))
            duplicate = kind.ToString();
        return this;
    }

    internal ApiResultDefinition Apply(bool start, ApiResultDefinition result) =>
        Find(start, result.Kind) is { } rule
            ? new(result.Kind, rule.Body, result.IsPrimary, result.Id, result.Description, rule.Http)
            : result;

    /// <summary>Render a declared alternative. Returns false when the default response applies.</summary>
    internal bool TryProject(bool submission, ApiResultKind kind, out AcceptedProcessResponse response,
        AcceptedProcessLocation? receipt = null, bool terminalFailure = false)
    {
        var rule = terminalFailure ? Failure : Find(submission, kind);
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
