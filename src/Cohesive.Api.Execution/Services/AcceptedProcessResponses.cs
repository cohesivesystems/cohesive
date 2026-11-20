namespace Cohesive.Api.Execution.Services;

/// <summary>Stable polling token and local address issued for a durable or host-owned admission.</summary>
public sealed record AcceptedProcessLocation(string ResultToken, string ResultLocation);

/// <summary>HTTP contracts for application-specific outcomes of an accepted process interaction.</summary>
/// <remarks>Each projection supplies a body only. Its declared type and status drive both HTTP and API metadata.</remarks>
public sealed class AcceptedProcessResponses
{
    internal sealed record Rule(Type Body, ApiHttpResultBinding Http, Func<AcceptedProcessLocation?, object?> Project);
    readonly Dictionary<(bool Start, ApiResultKind Kind), Rule> rules = [];

    /// <summary>Project an admitted or replayed submission to a typed polling receipt.</summary>
    public AcceptedProcessResponses Receipt<T>(Func<AcceptedProcessLocation, T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Add(true, ApiResultKind.Accepted, 202, typeof(T), location => body(location!));
    }

    /// <summary>Use one typed body for malformed input and rejected submission input.</summary>
    public AcceptedProcessResponses InvalidRequest<T>(int status, T body) =>
        Add(true, ApiResultKind.ValidationFailed, status, typeof(T), _ => body);

    /// <summary>Project a reused identity with a conflicting payload.</summary>
    public AcceptedProcessResponses IdentityConflict<T>(int status, T body) =>
        Add(true, ApiResultKind.Conflict, status, typeof(T), _ => body);

    /// <summary>Return a bodyless response when admission capacity is exhausted.</summary>
    public AcceptedProcessResponses AtCapacity(int status) => Empty(true, ApiResultKind.CapacityExceeded, status);

    /// <summary>Return a bodyless response while a polled instance is running.</summary>
    public AcceptedProcessResponses Pending(int status) => Empty(false, ApiResultKind.Accepted, status);

    /// <summary>Return a bodyless response for unknown instances and invalid polling tokens.</summary>
    public AcceptedProcessResponses Missing(int status) => Empty(false, ApiResultKind.NotFound, status);

    /// <summary>Project verified terminal failure. Storage and decoding failures retain their own handling.</summary>
    public AcceptedProcessResponses ExecutionFailed<T>(int status, T body)
    {
        Failure = new(typeof(T), new(status), _ => body);
        return this;
    }

    internal Rule? Failure { get; private set; }
    internal Rule? Find(bool start, ApiResultKind kind) => rules.GetValueOrDefault((start, kind));

    internal AcceptedProcessResponses Snapshot()
    {
        var copy = new AcceptedProcessResponses { Failure = Failure };
        foreach (var entry in rules) copy.rules.Add(entry.Key, entry.Value);
        return copy;
    }

    AcceptedProcessResponses Empty(bool start, ApiResultKind kind, int status) =>
        Add(start, kind, status, typeof(void), _ => null);

    AcceptedProcessResponses Add(bool start, ApiResultKind kind, int status, Type body, Func<AcceptedProcessLocation?, object?> project)
    {
        rules.Add((start, kind), new(body, new(status, body == typeof(void) ? null : "application/json"), project));
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
