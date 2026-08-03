using Cohesive.Prelude;

namespace Cohesive.Examples.Transportation;

/// <summary>
/// Effect handler that calculates route mileage for a load.
/// </summary>
/// <remarks>Compatibility fixture retained for the flat Transition effect surface through ARI-218.</remarks>
public sealed class RouteDistanceResolver
    : IEffectHandler<Load.CalculateDistanceRequest, Load.DistanceCalculatedResult>
{
    readonly IReadOnlyDictionary<RouteLeg, Distance> staticDistanceByLeg;
    readonly Func<string, string, CancellationToken, Task<Distance>> externalDistanceProvider;

    /// <summary>
    /// Creates a route-distance effect handler.
    /// </summary>
    public RouteDistanceResolver(
        IReadOnlyDictionary<RouteLeg, Distance>? staticDistanceByLeg = null,
        Func<string, string, CancellationToken, Task<Distance>>? externalDistanceProvider = null
        )
    {
        this.staticDistanceByLeg = staticDistanceByLeg ?? new Dictionary<RouteLeg, Distance>();
        this.externalDistanceProvider = externalDistanceProvider ?? ((_, _, _) => Task.FromResult(Distance.AdditiveIdentity));
    }

    /// <summary>
    /// Executes mileage calculation for a typed request.
    /// </summary>
    public async Task<Load.DistanceCalculatedResult> HandleAsync(
        OperationContext context,
        Load.CalculateDistanceRequest request
        )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();

        var stopCodes = ExtractStopCodes(request.Stops);
        var total = Distance.Zero;
        for (var i = 0; i < stopCodes.Count - 1; i++)
        {
            var from = stopCodes[i];
            var to = stopCodes[i + 1];
            if (TryGetStaticDistance(from, to, out var knownDistance))
            {
                total += knownDistance;
                continue;
            }
            total += await externalDistanceProvider(from, to, context.CancellationToken).ConfigureAwait(false);
        }

        return new Load.DistanceCalculatedResult(
            TotalDistance: total);
    }

    bool TryGetStaticDistance(string from, string to, out Distance distance)
    {
        var forward = new RouteLeg(from, to);
        if (staticDistanceByLeg.TryGetValue(forward, out distance))
            return true;

        var reverse = new RouteLeg(to, from);
        return staticDistanceByLeg.TryGetValue(reverse, out distance);
    }

    static IReadOnlyList<string> ExtractStopCodes(IReadOnlyList<Stop> stops)
    {
        List<string> codes = [];
        foreach (var stop in stops)
        {
            if (string.IsNullOrWhiteSpace(stop.Code))
            {
                throw new SemanticRuleViolationException(
                    $"Effect request '{Load.CalculateDistanceRequestName}' contains a stop with an invalid '{nameof(Stop.Code)}'.");
            }

            codes.Add(stop.Code);
        }

        return codes;
    }

    /// <summary>
    /// Represents a route leg between two stop codes.
    /// </summary>
    public readonly record struct RouteLeg(string From, string To);
}
