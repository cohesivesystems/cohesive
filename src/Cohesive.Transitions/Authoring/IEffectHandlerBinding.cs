using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Prelude;
using Cohesive.Domain;
using Cohesive.Model;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Untyped bridge for effect-handler registrations.
/// </summary>
public interface IEffectHandlerBinding
{
    /// <summary>
    /// Effect request name handled by this binding.
    /// </summary>
    string RequestName { get; }

    /// <summary>
    /// Request CLR type.
    /// </summary>
    Type RequestType { get; }

    /// <summary>
    /// Response CLR type.
    /// </summary>
    Type ResultType { get; }

    /// <summary>
    /// Executes the handler for a raw effect request.
    /// </summary>
    Task<object?> HandleAsync(OperationContext context, EffectRequest request);
}

/// <summary>
/// Strongly typed effect-handler registration.
/// </summary>
public sealed class EffectHandlerBinding<TRequest, TResult> : IEffectHandlerBinding
    where TRequest : IEffectRequest<TResult>
{
    readonly IEffectHandler<TRequest, TResult> handler;
    readonly Func<ObservationValue, TRequest> requestDeserializer;

    EffectHandlerBinding(
        IEffectHandler<TRequest, TResult> handler,
        Func<ObservationValue, TRequest> requestDeserializer
        )
    {
        this.handler = Guard.RequireNotNull(handler);
        this.requestDeserializer = Guard.RequireNotNull(requestDeserializer);
    }

    /// <summary>
    /// Creates a binding that deserializes request payload JSON to <typeparamref name="TRequest"/>.
    /// </summary>
    public static EffectHandlerBinding<TRequest, TResult> FromJson(
        IEffectHandler<TRequest, TResult> handler,
        JsonSerializerOptions? jsonOptions = null
        )
    {
        var options = jsonOptions ?? CreateDefaultJsonOptions();
        return new(
            handler: handler,
            requestDeserializer: payload =>
            {
                var request = payload.Deserialize<TRequest>(options);
                if (request is null)
                {
                    throw new SemanticRuleViolationException(
                        $"Effect request '{TRequest.RequestName}' payload cannot be deserialized as '{typeof(TRequest).FullName}'.");
                }

                return request;
            });
    }

    /// <inheritdoc />
    public string RequestName => TRequest.RequestName;

    /// <inheritdoc />
    public Type RequestType => typeof(TRequest);

    /// <inheritdoc />
    public Type ResultType => typeof(TResult);

    /// <inheritdoc />
    public async Task<object?> HandleAsync(OperationContext context, EffectRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(request.Name, RequestName, StringComparison.Ordinal))
        {
            throw new SemanticRuleViolationException(
                $"Handler binding for '{RequestName}' cannot process effect request '{request.Name}'.");
        }

        var typedRequest = requestDeserializer(request.Payload);
        var typedResult = await handler
            .HandleAsync(context: context, request: typedRequest)
            .ConfigureAwait(false);
        return typedResult;
    }

    static JsonSerializerOptions CreateDefaultJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new StructuredQuantityJsonConverterFactory());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
