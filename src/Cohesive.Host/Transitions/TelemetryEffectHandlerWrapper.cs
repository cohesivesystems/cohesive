using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cohesive.Transitions.Authoring;
using Microsoft.Extensions.Logging;

namespace Cohesive.Host.Transitions;

/// <summary>
/// Configuration for <see cref="TelemetryEffectHandlerWrapper{TRequest,TResult}"/>.
/// </summary>
/// <typeparam name="TRequest"></typeparam>
/// <typeparam name="TResult"></typeparam>
public sealed class TelemetryEffectHandlerWrapperOptions<TRequest, TResult>
    where TRequest : IEffectRequest<TResult>
{
    /// <summary>
    /// The activity source to use for telemetry.
    /// If not specified, the activity is not tracked.
    /// </summary>
    public ActivitySource? ActivitySource { get; set; }
    
    /// <summary>
    /// The activity name to use for telemetry.
    /// If not specified, defaults to "effect_handler_{TRequest}".
    /// </summary>
    public string? ActivityName { get; set; }
    
    /// <summary>
    /// The request counter instrument.
    /// If not specified, no request counter is emitted.
    /// </summary>
    public Counter<long>? RequestCounter { get; set; }
    
    /// <summary>
    /// The request duration histogram instrument.
    /// If not specified, no request duration histogram is emitted.
    /// </summary>
    public Histogram<double>? DurationHistogram { get; set; }
    
    /// <summary>
    /// The error counter instrument.
    /// If not specified, no error counter is emitted.
    /// </summary>
    public Counter<long>? ErrorCounter { get; set; }
    
    /// <summary>
    /// The tags to apply to the activity.
    /// </summary>
    public Func<TRequest, TagList>? ActivityTags { get; set; }
    
    /// <summary>
    /// The tags to apply to the request counter.
    /// </summary>
    public Func<TRequest, TResult?, TagList>? CounterTags { get; set; }
    
    /// <summary>
    /// The tags to apply to the error counter.
    /// </summary>
    public Func<TRequest, Exception?, TagList>? ErrorTags { get; set; }
    
    /// <summary>
    /// The tags to apply to the request duration histogram.
    /// </summary>
    public Func<TRequest, TResult?, TagList>? DurationTags { get; set; }
    
    /// <summary>
    /// The logger to use for logging.
    /// If not specified, gets a <see cref="ILogger{THandler}"/> for the handler type from <see cref="IServiceProvider"/>.
    /// </summary>
    public ILogger? Logger { get; set; }
    
    /// <summary>
    /// The logging action to invoke when the request completes.
    /// If not specified, omits logging the request.
    /// </summary>
    public Action<ILogger?, TRequest, TResult?, Exception?>? LogRequest { get; set; }
}

/// <summary>
/// An effect handler wrapper that emits telemetry.
/// </summary>
/// <typeparam name="TRequest"></typeparam>
/// <typeparam name="TResult"></typeparam>
public class TelemetryEffectHandlerWrapper<TRequest, TResult>(
    IEffectHandler<TRequest, TResult> handler,
    TelemetryEffectHandlerWrapperOptions<TRequest, TResult> options
    ) : IEffectHandler<TRequest, TResult> where TRequest : IEffectRequest<TResult>
{
    readonly TelemetryEffectHandlerWrapperOptions<TRequest, TResult> options = options ?? throw new ArgumentNullException(nameof(options));
    readonly Func<TRequest, TagList> activityTags = options.ActivityTags ?? (_ => default);
    readonly Func<TRequest, TResult?, TagList> counterTags = options.CounterTags ?? ((_, _) => default);
    readonly Func<TRequest, TResult?, TagList> durationTags = options.DurationTags ?? options.CounterTags ?? ((_, _) => default);
    readonly Func<TRequest, Exception?, TagList> errorTags = options.ErrorTags ?? ((request, _) => (options.CounterTags ?? ((_, _) => default))(request, default));
    readonly Action<ILogger?, TRequest, TResult?, Exception?> logRequest = options.LogRequest ?? ((log, request, result, ex) => 
    {
        if (log?.IsEnabled(LogLevel.Information) is true)
            log.LogInformation("effect_handler_completed request={Request} result={Result}", request.ToString(), result?.ToString());
    });
    
    /// <summary>Handles an effect request while recording telemetry.</summary>
    public async Task<TResult> HandleAsync(OperationContext context, TRequest request)
    {
        TResult? result = default;
        var startTimestamp = Stopwatch.GetTimestamp();
        using var activity = options.ActivitySource?.StartActivity(options.ActivityName ?? $"effect_handler_{typeof(TRequest).Name}");
        if (activity is not null)
        {
            var tags = activityTags(request);
            foreach (var tag in tags)
                activity.SetTag(tag.Key, tag.Value);
        }
        try
        {
            result = await handler.HandleAsync(context, request);
            logRequest(options.Logger, request, result, null);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            options.ErrorCounter?.Add(1, errorTags(request, ex));
            throw;
        }
        finally
        {
            options.RequestCounter?.Add(1, counterTags(request, result));
            options.DurationHistogram?.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, durationTags(request, result));
        }
    }
}
