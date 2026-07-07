using System.CommandLine;
using System.Runtime.ExceptionServices;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Host.Cli;

static class CliCommandHandlerBinding
{
    public static CliCommandExecutionDelegate Bind(Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var method = handler.Method;
        var parameters = method.GetParameters();
        return async context =>
        {
            ArgumentNullException.ThrowIfNull(context);
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                args[i] = ResolveArgument(parameters[i], context);
            object? result;
            try
            {
                result = method.Invoke(handler.Target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            return await NormalizeReturnValueAsync(result).ConfigureAwait(false);
        };
    }

    public static CliCommandValidationDelegate BindValidator(Delegate validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        var method = validator.Method;
        var parameters = method.GetParameters();
        return async context =>
        {
            ArgumentNullException.ThrowIfNull(context);
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                args[i] = ResolveArgument(parameters[i], context);
            object? result;
            try
            {
                result = method.Invoke(validator.Target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            return await NormalizeValidationReturnValueAsync(result).ConfigureAwait(false);
        };
    }

    static object? ResolveArgument(ParameterInfo parameter, CliCommandContext context)
    {
        var parameterType = parameter.ParameterType;

        if (parameterType.IsInstanceOfType(context))
            return context;
        
        if (parameterType == typeof(CancellationToken))
            return context.CancellationToken;

        if (parameterType == typeof(ParseResult))
            return context.ParseResult;

        if (parameterType == typeof(CliOutput))
            return context.Output;

        if (parameterType.IsAssignableFrom(typeof(IConfigurationRoot)))
            return context.ConfigurationRoot;

        if (parameterType == typeof(IConfiguration))
            return context.ConfigurationRoot;

        if (context is ICliTypedCommandContext typedContext && parameterType.IsAssignableFrom(typedContext.ConfigurationType))
            return typedContext.Configuration;
        
        if (context.TryResolveInvocationDependency(parameterType, out var dependency))
            return dependency;

        throw new InvalidOperationException($"Unable to resolve CLI handler parameter '{parameter.Name}' of type '{parameterType.FullName}'.");
    }

    static async Task<int> NormalizeReturnValueAsync(object? result)
    {
        switch (result)
        {
            case null:
                return 0;
            case int exitCode:
                return exitCode;
            case Task<int> exitCodeTask:
                return await exitCodeTask.ConfigureAwait(false);
            case Task task:
                await task.ConfigureAwait(false);
                return 0;
            case ValueTask<int> exitCodeValueTask:
                return await exitCodeValueTask.ConfigureAwait(false);
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                return 0;
            default:
                throw new InvalidOperationException(
                    $"CLI handler return type '{result.GetType().FullName}' is not supported. " +
                    "Handlers must return void, int, Task, Task<int>, ValueTask, or ValueTask<int>."
                    );
        }
    }

    static async Task<CliValidationResult> NormalizeValidationReturnValueAsync(object? result)
    {
        switch (result)
        {
            case null:
                return CliValidationResult.Success;
            case CliValidationResult validationResult:
                return validationResult;
            case string error:
                return string.IsNullOrWhiteSpace(error) ? CliValidationResult.Success : CliValidationResult.Failure(error);
            case IReadOnlyList<string> errors:
                return CliValidationResult.Failure(errors);
            case IEnumerable<string> errors:
                return CliValidationResult.Failure(errors);
            case Task<CliValidationResult> validationTask:
                return await validationTask.ConfigureAwait(false);
            case Task<string> errorTask:
                return await NormalizeValidationReturnValueAsync(await errorTask.ConfigureAwait(false)).ConfigureAwait(false);
            case Task<IReadOnlyList<string>> errorsTask:
                return await NormalizeValidationReturnValueAsync(await errorsTask.ConfigureAwait(false)).ConfigureAwait(false);
            case Task<IEnumerable<string>> enumerableErrorsTask:
                return await NormalizeValidationReturnValueAsync(await enumerableErrorsTask.ConfigureAwait(false)).ConfigureAwait(false);
            case Task task:
                await task.ConfigureAwait(false);
                return CliValidationResult.Success;
            case ValueTask<CliValidationResult> validationValueTask:
                return await validationValueTask.ConfigureAwait(false);
            case ValueTask<string> errorValueTask:
                return await NormalizeValidationReturnValueAsync(await errorValueTask.ConfigureAwait(false)).ConfigureAwait(false);
            case ValueTask<IReadOnlyList<string>> errorsValueTask:
                return await NormalizeValidationReturnValueAsync(await errorsValueTask.ConfigureAwait(false)).ConfigureAwait(false);
            case ValueTask<IEnumerable<string>> enumerableErrorsValueTask:
                return await NormalizeValidationReturnValueAsync(await enumerableErrorsValueTask.ConfigureAwait(false)).ConfigureAwait(false);
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                return CliValidationResult.Success;
            default:
                throw new InvalidOperationException(
                    $"CLI validator return type '{result.GetType().FullName}' is not supported. " +
                    "Validators must return void, Task, ValueTask, string, IEnumerable<string>, CliValidationResult, or async equivalents.");
        }
    }
}
