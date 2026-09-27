using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;

namespace Cohesive.Relations.Execution;

/// <summary>Stable value-admission diagnostics shared by hosted Query invocation adapters.</summary>
/// <remarks>Historical Process-prefixed codes are retained for diagnostic compatibility.</remarks>
public static class HostedQueryValueDiagnosticCodes
{
    /// <summary>Identifies InputContractMismatch at the portable invocation boundary.</summary>
    public const string InputContractMismatch = "processes.relationHandler.input.contractMismatch";
    /// <summary>Identifies InputValueInvalid at the portable invocation boundary.</summary>
    public const string InputValueInvalid = "processes.relationHandler.input.invalid";
    /// <summary>Identifies ValueConversionFailed at the portable invocation boundary.</summary>
    public const string ValueConversionFailed = "processes.relationHandler.value.conversionFailed";
    /// <summary>Identifies ResultValueInvalid at the portable invocation boundary.</summary>
    public const string ResultValueInvalid = "processes.relationHandler.result.invalid";
}

/// <summary>Shared portable value admission for typed hosted Query implementations.</summary>
/// <remarks>Pure conversion and validation only; it does not dispatch handlers or establish authorization.
/// Frozen serializer metadata is shared. Values and diagnostics are invocation-scoped.</remarks>
public static class HostedQueryValueAdapter
{
    static readonly JsonSerializerOptions JsonOptions = ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions();

    /// <summary>Validates an exact concrete invocation contract and decodes its CLR projection.</summary>
    /// <returns>The typed input or a structured admission diagnostic.</returns>
    /// <exception cref="ArgumentNullException">The value or contract is null.</exception>
    public static Result<T, DocumentValidationDiagnostic> Decode<T>(PortableValue inputValue, ValueContract contract) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(inputValue);
        ArgumentNullException.ThrowIfNull(contract);
        if (inputValue.Contract != contract)
        {
            return Failed(
                HostedQueryValueDiagnosticCodes.InputContractMismatch,
                "The evaluation input contract does not match the exact Relation/Query definition.",
                "/input/contract");
        }
        if (inputValue.State != PortableValueState.Concrete || inputValue.Value is null)
        {
            return Failed(
                HostedQueryValueDiagnosticCodes.InputValueInvalid,
                "A typed Relation/Query handler requires one concrete non-null invocation value.",
                "/input/state");
        }

        var inputValidation = PortableExecutionValidator.Validate(inputValue);
        var inputError = inputValidation.Diagnostics.FirstOrDefault(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (inputError is not null)
            return Result<T, DocumentValidationDiagnostic>.FromFailure(inputError);

        T input;
        try
        {
            var element = JsonSerializer.SerializeToElement(inputValue.Value.Value, JsonOptions);
            input = element.Deserialize<T>(JsonOptions)
                ?? throw new JsonException("The concrete invocation decoded as null.");
        }
        catch (Exception exception) when (IsConversionFailure(exception))
        {
            return Failed(
                HostedQueryValueDiagnosticCodes.ValueConversionFailed,
                $"The Relation/Query invocation could not be decoded as '{typeof(T).FullName}': "
                + exception.Message,
                "/input/value");
        }

        return Result<T, DocumentValidationDiagnostic>.FromSuccess(input);

        static Result<T, DocumentValidationDiagnostic> Failed(string code, string message, string location) =>
            Result<T, DocumentValidationDiagnostic>.FromFailure(new(code, DiagnosticSeverity.Error, message, location));
    }

    /// <summary>Encodes a typed result and validates it against the exact canonical result contract.</summary>
    /// <returns>A concrete portable result or a structured conversion/contract diagnostic.</returns>
    /// <exception cref="ArgumentNullException">The contract is null.</exception>
    public static Result<PortableValue, DocumentValidationDiagnostic> Encode<T>(T result, ValueContract contract) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(contract);

        PortableValue portable;
        try
        {
            var observed = ObservationValue.FromObject(result);
            if (observed.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
                throw new JsonException("The typed result encoded as an undefined or null root value.");
            portable = PortableValue.Concrete(contract, observed);
        }
        catch (Exception exception) when (IsConversionFailure(exception))
        {
            return Failed(
                HostedQueryValueDiagnosticCodes.ValueConversionFailed,
                $"The Relation/Query result could not be encoded from '{typeof(T).FullName}': "
                + exception.Message,
                "/result");
        }

        var resultValidation = PortableExecutionValidator.Validate(portable);
        var resultError = resultValidation.Diagnostics.FirstOrDefault(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return resultError is null
            ? Result<PortableValue, DocumentValidationDiagnostic>.FromSuccess(portable)
            : Failed(
                HostedQueryValueDiagnosticCodes.ResultValueInvalid,
                $"The typed Relation/Query result violates its canonical contract: {resultError.Message}",
                "/result");

        static Result<PortableValue, DocumentValidationDiagnostic> Failed(string code, string message, string location) =>
            Result<PortableValue, DocumentValidationDiagnostic>.FromFailure(new(code, DiagnosticSeverity.Error, message, location));
    }

    static bool IsConversionFailure(Exception exception) => exception is
        JsonException or NotSupportedException or InvalidOperationException or ArgumentException;
}
