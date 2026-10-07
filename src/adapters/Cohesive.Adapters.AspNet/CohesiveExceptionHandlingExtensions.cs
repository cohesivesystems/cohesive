using System.Diagnostics;
using Cohesive.Api;
using Cohesive.Storage;
using Cohesive.Transitions.Execution;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.AspNet;

/// <summary>Registers explicit Cohesive exception-to-HTTP mappings in the native ASP.NET exception pipeline.</summary>
public static class CohesiveExceptionHandlingExtensions
{
    /// <summary>Registers sanitized concurrency-conflict and state-preparation Problem Details handling and the native Problem Details service.</summary>
    /// <param name="services">Application service collection.</param>
    /// <returns>The same collection.</returns>
    /// <exception cref="ArgumentNullException">Services is null.</exception>
    /// <remarks>Call UseExceptionHandler before request middleware/endpoints. Only optimistic concurrency
    /// conflicts and state-preparation failures are handled; unknown exceptions fall through to other registered handlers. Handler registration
    /// order follows ASP.NET conventions. Domain rejection remains an ordinary endpoint result. No retry is performed.</remarks>
    public static IServiceCollection AddCohesiveExceptionHandling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddProblemDetails();
        services.AddExceptionHandler<CohesiveExceptionHandler>();
        return services;
    }
}

sealed class CohesiveExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
            return false;

        var details = exception switch
        {
            ObservationConcurrencyConflictException => CohesiveHttpProblems.ConcurrencyConflict(context),
            TransitionStatePreparationException failure => CohesiveHttpProblems.StatePreparationFailure(context, failure),
            _ => null
        };
        if (details is null)
            return false;
        context.Response.StatusCode = details.Status!.Value;
        if (!await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = details }).ConfigureAwait(false))
            await context.Response.WriteAsJsonAsync(details, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Sanitized HTTP projections of shared execution failures.</summary>
static class CohesiveHttpProblems
{
    internal static ProblemDetails StatePreparationFailure(HttpContext context,
        TransitionStatePreparationException failure) => new()
    {
        Status = StatusCodes.Status500InternalServerError,
        Title = "Entity state preparation failed",
        Detail = TransitionStatePreparationException.SafeMessage,
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
