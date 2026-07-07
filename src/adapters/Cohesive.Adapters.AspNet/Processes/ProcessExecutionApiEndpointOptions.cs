using Cohesive.Api;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Processes;

/// <summary>
/// Configuration for binding a declared API operation to a process execution repository query.
/// </summary>
public sealed class ProcessExecutionApiEndpointOptions<TRequest>
{
    /// <summary>
    /// Creates the process execution repository query from the bound API request.
    /// </summary>
    public required Func<ProcessExecutionApiQueryRequestContext<TRequest>, ProcessExecutionQuery> CreateQuery { get; init; }

    /// <summary>
    /// Maps repository query results to an HTTP response.
    /// </summary>
    public required Func<ProcessExecutionApiQueryResultContext<TRequest>, IResult> CreateResult { get; init; }
}

/// <summary>
/// Request context supplied to process execution query factories.
/// </summary>
public sealed record ProcessExecutionApiQueryRequestContext<TRequest>(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    TRequest Request
);

/// <summary>
/// Result context supplied to process execution query response mappers.
/// </summary>
public sealed record ProcessExecutionApiQueryResultContext<TRequest>(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    TRequest Request,
    ProcessExecutionQuery Query,
    ProcessExecutionQueryResult Result
);
