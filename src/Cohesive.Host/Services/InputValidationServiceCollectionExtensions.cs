using System.Diagnostics.CodeAnalysis;
using Cohesive.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cohesive.Host.Services;

/// <summary>Registers shared and typed input validation using one configuration-scoped cache.</summary>
public static class InputValidationServiceCollectionExtensions
{
    /// <summary>Registers a singleton cache and typed adapters; explicitly registered contracts are prepared now.</summary>
    /// <remarks>Call once per service collection. Configuration errors fail during registration, not request handling.</remarks>
    /// <exception cref="ArgumentNullException">Services is null.</exception>
    /// <exception cref="InvalidOperationException">A shared input validator is already registered.</exception>
    /// <exception cref="Exception">Authoring or configuration callback errors propagate.</exception>
    [RequiresUnreferencedCode("CLR input authoring requires preserved members and validation attributes.")]
    public static IServiceCollection AddCohesiveInputValidation(this IServiceCollection services,
        Action<InputValidationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(IInputValidator)))
            throw new InvalidOperationException("Input validation has already been registered. Configure it in one registration.");
        var validator = InputValidator.Create(configure);
        services.AddSingleton<IInputValidator>(validator);
        services.TryAddSingleton(typeof(IInputValidator<>), typeof(TypedInputValidator<>));
        return services;
    }

    sealed class TypedInputValidator<T> : IInputValidator<T> where T : notnull
    {
        readonly IInputValidator<T> validator;
        [RequiresUnreferencedCode("CLR input authoring requires preserved members and validation attributes.")]
        public TypedInputValidator(IInputValidator shared) => validator = shared.For<T>();
        [RequiresUnreferencedCode("Typed input projection requires preserved CLR members.")]
        [RequiresDynamicCode("Typed input projection may compile CLR projection delegates.")]
        public DocumentValidationResult Validate(T input) => validator.Validate(input);
        public DocumentValidationResult ValidatePortable(ObservationValue input) => validator.ValidatePortable(input);
    }
}
