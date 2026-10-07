using System.Diagnostics;
using Cohesive.Api;
using Cohesive.Storage;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.AspNet;

/// <summary>Registers explicit Cohesive exception-to-HTTP mappings in the native ASP.NET exception pipeline.</summary>
public static class CohesiveExceptionHandlingExtensions
{
    /// <summary>Registers sanitized concurrency-conflict Problem Details handling and the native Problem Details service.</summary>
    /// <param name="services">Application service collection.</param>
    /// <returns>The same collection.</returns>
    /// <exception cref="ArgumentNullException">Services is null.</exception>
    /// <remarks>Call UseExceptionHandler before request middleware/endpoints. Only optimistic concurrency
    /// conflicts are handled; unknown exceptions fall through to other registered handlers. Handler registration
    /// order follows ASP.NET conventions. Domain rejection remains an ordinary endpoint result. No retry is performed.</remarks>
    public static IServiceCollection AddCohesiveExceptionHandling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddProblemDetails();
        services.AddExceptionHandler<CohesiveConcurrencyExceptionHandler>();
        return services;
    }
}

sealed class CohesiveConcurrencyExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ObservationConcurrencyConflictException || context.Response.HasStarted)
            return false;

        context.Response.StatusCode = StatusCodes.Status409Conflict;
        var details = CohesiveHttpProblems.ConcurrencyConflict(context);
        if (!await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = details }).ConfigureAwait(false))
            await context.Response.WriteAsJsonAsync(details, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Sanitized HTTP projections of shared execution failures.</summary>
static class CohesiveHttpProblems
{
    internal static ProblemDetails StatePreparationFailure(HttpContext context,
        Cohesive.Transitions.Execution.TransitionStatePreparationException failure) => new()
    {
        Status = StatusCodes.Status500InternalServerError,
        Title = "Entity state preparation failed",
        Detail = "The operation could not prepare a valid entity state. No change was committed.",
        Extensions =
        {
            ["code"] = failure.Code,
            ["location"] = failure.Location,
            ["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier
        }
    };

    internal static ProblemDetails ConcurrencyConflict(HttpContext context) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Title = "Concurrent modification",
        Detail = "The resource changed concurrently. Reload its current state before trying again.",
        Extensions =
        {
            ["code"] = ApiProblemCodes.ConcurrencyConflict,
            ["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier
        }
    };
}
