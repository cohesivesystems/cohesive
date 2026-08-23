using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;
using Cohesive.Relations.Authoring;

namespace Cohesive.Processes.Execution;

/// <summary>Stable diagnostics emitted by exact Process Relation/Query runtime dispatch.</summary>
public static class ProcessRelationHandlerDiagnosticCodes
{
    /// <summary>No handler is deployed for the requested exact definition.</summary>
    public const string DefinitionNotRegistered = "processes.relationHandler.definition.notRegistered";

    /// <summary>The requested definition identity/revision is deployed with another semantic fingerprint.</summary>
    public const string DefinitionFingerprintMismatch = "processes.relationHandler.definition.fingerprintMismatch";

    /// <summary>The invocation value does not carry the canonical Relation/Query input contract.</summary>
    public const string InputContractMismatch = "processes.relationHandler.input.contractMismatch";

    /// <summary>The invocation value is not one concrete portable input.</summary>
    public const string InputValueInvalid = "processes.relationHandler.input.invalid";

    /// <summary>A portable value could not be converted to or from its registered CLR projection.</summary>
    public const string ValueConversionFailed = "processes.relationHandler.value.conversionFailed";

    /// <summary>The typed handler result does not satisfy the canonical Relation/Query result contract.</summary>
    public const string ResultValueInvalid = "processes.relationHandler.result.invalid";
}

/// <summary>Executes one typed exact canonical Relation or Query evaluation.</summary>
/// <typeparam name="TInput">CLR projection of the canonical invocation contract.</typeparam>
/// <typeparam name="TResult">CLR projection of the canonical singular result contract.</typeparam>
/// <param name="context">Physical operation context carrying cancellation and infrastructure attribution.</param>
/// <param name="evaluation">Complete canonical Process evaluation context, unchanged from the Process node.</param>
/// <param name="input">Typed concrete invocation value decoded from <paramref name="evaluation"/>.</param>
/// <returns>A typed concrete result to encode against the registered canonical result contract.</returns>
/// <remarks>
/// Throwing represents a physical execution failure and is not converted into semantic Process failure evidence.
/// Model expected domain outcomes in <typeparamref name="TResult"/>. Observe cancellation through
/// <paramref name="context"/>.
/// </remarks>
public delegate ValueTask<TResult> ProcessRelationHandler<TInput, TResult>(
    OperationContext context,
    ProcessRelationEvaluation evaluation,
    TInput input)
    where TInput : notnull
    where TResult : notnull;

/// <summary>Closed typed success or structured semantic failure from a Relation/Query handler.</summary>
/// <typeparam name="TResult">CLR projection of the canonical singular result contract.</typeparam>
/// <remarks>
/// This outcome is runtime evidence rather than canonical Query content. Use failure for an expected inability to
/// produce the declared result after typed input admission. Throwing remains reserved for physical execution
/// failure, and cancellation remains observable through the operation context.
/// </remarks>
public sealed class ProcessRelationHandlerOutcome<TResult>
    where TResult : notnull
{
    readonly TResult? value;

    ProcessRelationHandlerOutcome(bool isSuccessful, TResult? value, DocumentValidationDiagnostic? failure)
    {
        IsSuccessful = isSuccessful;
        this.value = value;
        Failure = failure;
    }

    /// <summary>Typed singular value from a successful handler outcome.</summary>
    /// <exception cref="InvalidOperationException">This is a failed outcome.</exception>
    public TResult Value => IsSuccessful
        ? value!
        : throw new InvalidOperationException("A failed Relation/Query outcome has no result value.");

    /// <summary>Structured error evidence when the handler cannot produce its declared value.</summary>
    public DocumentValidationDiagnostic? Failure { get; }

    /// <summary>Whether this outcome contains one typed singular value.</summary>
    public bool IsSuccessful { get; }

    /// <summary>Creates a successful typed outcome.</summary>
    /// <param name="value">Non-null typed value to validate against the canonical result contract.</param>
    /// <returns>A closed successful outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static ProcessRelationHandlerOutcome<TResult> Completed(TResult value) =>
        new(
            isSuccessful: true,
            value ?? throw new ArgumentNullException(nameof(value)),
            failure: null);

    /// <summary>Creates a structured semantic failure with no result value.</summary>
    /// <param name="failure">One error diagnostic describing why no declared result can be produced.</param>
    /// <returns>A closed failed outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="failure"/> is not an error diagnostic.</exception>
    public static ProcessRelationHandlerOutcome<TResult> Failed(DocumentValidationDiagnostic failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Severity != DiagnosticSeverity.Error)
            throw new ArgumentException("A failed Relation/Query outcome requires an error diagnostic.", nameof(failure));
        return new(isSuccessful: false, value: default, failure);
    }
}

/// <summary>Executes one typed exact Relation/Query evaluation with an explicit semantic outcome.</summary>
/// <typeparam name="TInput">CLR projection of the canonical invocation contract.</typeparam>
/// <typeparam name="TResult">CLR projection of the canonical singular result contract.</typeparam>
/// <param name="context">Physical operation context carrying cancellation and infrastructure attribution.</param>
/// <param name="evaluation">Complete canonical Process evaluation context, unchanged from the Process node.</param>
/// <param name="input">Typed concrete invocation value decoded from <paramref name="evaluation"/>.</param>
/// <returns>One typed success or structured semantic failure.</returns>
/// <remarks>
/// Throwing represents physical execution failure. Expected inability to produce the declared result is returned as
/// <see cref="ProcessRelationHandlerOutcome{TResult}.Failed"/>.
/// </remarks>
public delegate ValueTask<ProcessRelationHandlerOutcome<TResult>> ProcessRelationOutcomeHandler<TInput, TResult>(
    OperationContext context,
    ProcessRelationEvaluation evaluation,
    TInput input)
    where TInput : notnull
    where TResult : notnull;

/// <summary>Runtime-only binding between one exact canonical Relation or Query and its typed executable handler.</summary>
/// <remarks>
/// The document retained by the source typed handle remains the definition authority. This registration projects
/// only its exact reference and portable contracts. The handler is deployment state and never enters canonical
/// content or its fingerprint.
/// </remarks>
public abstract class ProcessRelationHandlerRegistration
{
    private protected ProcessRelationHandlerRegistration(
        ExecutionDefinitionReference reference,
        ValueContract inputContract,
        ValueContract resultContract)
    {
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        InputContract = inputContract ?? throw new ArgumentNullException(nameof(inputContract));
        ResultContract = resultContract ?? throw new ArgumentNullException(nameof(resultContract));
    }

    /// <summary>Exact identity, revision, and semantic fingerprint required for dispatch.</summary>
    public ExecutionDefinitionReference Reference { get; }

    /// <summary>Canonical invocation contract enforced before typed handler execution.</summary>
    public ValueContract InputContract { get; }

    /// <summary>Canonical singular result contract enforced after typed handler execution.</summary>
    public ValueContract ResultContract { get; }

    /// <summary>Creates one runtime-only typed handler registration.</summary>
    /// <typeparam name="TInput">CLR projection of the canonical invocation contract.</typeparam>
    /// <typeparam name="TResult">CLR projection of the canonical singular result contract.</typeparam>
    /// <param name="query">Valid canonical hosted Query whose exact document governs dispatch.</param>
    /// <param name="handler">Naturally asynchronous typed handler deployed for that exact Query.</param>
    /// <returns>An immutable runtime registration suitable for an exact handler catalog.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="query"/> or <paramref name="handler"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="query"/> is not canonically valid.</exception>
    public static ProcessRelationHandlerRegistration Create<TInput, TResult>(
        HostedQuery<TInput, TResult> query,
        ProcessRelationHandler<TInput, TResult> handler)
        where TInput : notnull
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(handler);
        RequireValid(query.IsValid, nameof(query));
        return new TypedProcessRelationHandlerRegistration<TInput, TResult>(
            query.Reference,
            query.InputContract,
            query.ResultContract,
            (context, evaluation, input) => AsOutcome(handler(context, evaluation, input)));
    }

    /// <summary>Creates one runtime-only typed handler registration for an authored Relation.</summary>
    /// <typeparam name="TInput">CLR projection of the canonical invocation contract.</typeparam>
    /// <typeparam name="TResult">CLR projection of the canonical singular result contract.</typeparam>
    /// <param name="relation">Valid canonical Relation whose exact reference governs dispatch.</param>
    /// <param name="handler">Naturally asynchronous typed handler deployed for that exact Relation.</param>
    /// <returns>An immutable runtime registration suitable for an exact handler catalog.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relation"/> or <paramref name="handler"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="relation"/> is not canonically valid.</exception>
    public static ProcessRelationHandlerRegistration Create<TInput, TResult>(
        Relation<TInput, TResult> relation,
        ProcessRelationHandler<TInput, TResult> handler)
        where TInput : notnull
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(handler);
        RequireValid(relation.IsValid, nameof(relation));
        return new TypedProcessRelationHandlerRegistration<TInput, TResult>(
            relation.Reference,
            relation.InputContract,
            relation.ResultContract,
            (context, evaluation, input) => AsOutcome(handler(context, evaluation, input)));
    }

    /// <summary>Creates one runtime-only typed handler registration with explicit semantic failure outcomes.</summary>
    /// <typeparam name="TInput">CLR projection of the canonical invocation contract.</typeparam>
    /// <typeparam name="TResult">CLR projection of the canonical singular result contract.</typeparam>
    /// <param name="query">Valid canonical hosted Query whose exact document governs dispatch.</param>
    /// <param name="handler">
    /// Naturally asynchronous typed handler returning either the declared result or structured Process failure
    /// evidence.
    /// </param>
    /// <returns>An immutable runtime registration suitable for an exact handler catalog.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="query"/> or <paramref name="handler"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="query"/> is not canonically valid.</exception>
    public static ProcessRelationHandlerRegistration CreateOutcome<TInput, TResult>(
        HostedQuery<TInput, TResult> query,
        ProcessRelationOutcomeHandler<TInput, TResult> handler)
        where TInput : notnull
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(handler);
        RequireValid(query.IsValid, nameof(query));
        return new TypedProcessRelationHandlerRegistration<TInput, TResult>(
            query.Reference,
            query.InputContract,
            query.ResultContract,
            handler);
    }

    /// <summary>Creates one runtime-only authored-Relation handler with explicit semantic failure outcomes.</summary>
    /// <typeparam name="TInput">CLR projection of the canonical invocation contract.</typeparam>
    /// <typeparam name="TResult">CLR projection of the canonical singular result contract.</typeparam>
    /// <param name="relation">Valid canonical Relation whose exact reference governs dispatch.</param>
    /// <param name="handler">Naturally asynchronous typed handler returning success or structured failure.</param>
    /// <returns>An immutable runtime registration suitable for an exact handler catalog.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relation"/> or <paramref name="handler"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="relation"/> is not canonically valid.</exception>
    public static ProcessRelationHandlerRegistration CreateOutcome<TInput, TResult>(
        Relation<TInput, TResult> relation,
        ProcessRelationOutcomeHandler<TInput, TResult> handler)
        where TInput : notnull
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(handler);
        RequireValid(relation.IsValid, nameof(relation));
        return new TypedProcessRelationHandlerRegistration<TInput, TResult>(
            relation.Reference,
            relation.InputContract,
            relation.ResultContract,
            handler);
    }

    static void RequireValid(bool isValid, string parameterName)
    {
        if (!isValid)
        {
            throw new ArgumentException(
                "A Process Relation/Query handler requires a canonically valid exact definition.",
                parameterName);
        }
    }

    static async ValueTask<ProcessRelationHandlerOutcome<TResult>> AsOutcome<TResult>(ValueTask<TResult> pending)
        where TResult : notnull =>
        ProcessRelationHandlerOutcome<TResult>.Completed(await pending.ConfigureAwait(false));

    internal abstract ValueTask<ProcessOperationResult> EvaluateAsync(
        OperationContext context,
        ProcessRelationEvaluation evaluation);

    sealed class TypedProcessRelationHandlerRegistration<TInput, TResult> : ProcessRelationHandlerRegistration
        where TInput : notnull
        where TResult : notnull
    {
        static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
        readonly ProcessRelationOutcomeHandler<TInput, TResult> handler;

        internal TypedProcessRelationHandlerRegistration(
            ExecutionDefinitionReference reference,
            ValueContract inputContract,
            ValueContract resultContract,
            ProcessRelationOutcomeHandler<TInput, TResult> handler)
            : base(reference, inputContract, resultContract) =>
            this.handler = handler;

        internal override async ValueTask<ProcessOperationResult> EvaluateAsync(
            OperationContext context,
            ProcessRelationEvaluation evaluation)
        {
            if (evaluation.Input.Contract != InputContract)
            {
                return Failed(
                    ProcessRelationHandlerDiagnosticCodes.InputContractMismatch,
                    "The Process evaluation input contract does not match the exact Relation/Query definition.",
                    "/input/contract");
            }
            if (evaluation.Input.State != PortableValueState.Concrete || evaluation.Input.Value is null)
            {
                return Failed(
                    ProcessRelationHandlerDiagnosticCodes.InputValueInvalid,
                    "A typed Relation/Query handler requires one concrete non-null invocation value.",
                    "/input/state");
            }

            var inputValidation = PortableExecutionValidator.Validate(evaluation.Input);
            var inputError = inputValidation.Diagnostics.FirstOrDefault(
                static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            if (inputError is not null)
                return ProcessOperationResult.Failed(inputError);

            TInput input;
            try
            {
                var element = JsonSerializer.SerializeToElement(evaluation.Input.Value.Value, JsonOptions);
                input = element.Deserialize<TInput>(JsonOptions)
                    ?? throw new JsonException("The concrete invocation decoded as null.");
            }
            catch (Exception exception) when (IsConversionFailure(exception))
            {
                return Failed(
                    ProcessRelationHandlerDiagnosticCodes.ValueConversionFailed,
                    $"The Relation/Query invocation could not be decoded as '{typeof(TInput).FullName}': "
                    + exception.Message,
                    "/input/value");
            }

            var outcome = await handler(context, evaluation, input).ConfigureAwait(false);
            if (outcome is null)
            {
                return Failed(
                    ProcessRelationHandlerDiagnosticCodes.ResultValueInvalid,
                    "The typed Relation/Query handler returned a null outcome.",
                    "/result");
            }
            if (!outcome.IsSuccessful)
                return ProcessOperationResult.Failed(outcome.Failure!);

            var result = outcome.Value;

            PortableValue portable;
            try
            {
                var element = JsonSerializer.SerializeToElement(result, JsonOptions);
                var observed = ObservationValue.FromJsonElement(element);
                if (observed.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
                    throw new JsonException("The typed result encoded as an undefined or null root value.");
                portable = PortableValue.Concrete(ResultContract, observed);
            }
            catch (Exception exception) when (IsConversionFailure(exception))
            {
                return Failed(
                    ProcessRelationHandlerDiagnosticCodes.ValueConversionFailed,
                    $"The Relation/Query result could not be encoded from '{typeof(TResult).FullName}': "
                    + exception.Message,
                    "/result");
            }

            var resultValidation = PortableExecutionValidator.Validate(portable);
            var resultError = resultValidation.Diagnostics.FirstOrDefault(
                static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            return resultError is null
                ? ProcessOperationResult.Completed(portable)
                : Failed(
                    ProcessRelationHandlerDiagnosticCodes.ResultValueInvalid,
                    $"The typed Relation/Query result violates its canonical contract: {resultError.Message}",
                    "/result");
        }

        static JsonSerializerOptions CreateJsonOptions()
        {
            var options = ExecutionDefinitionJsonSerializer.CreateOptions();
            options.PropertyNamingPolicy = null;
            return options;
        }

        static bool IsConversionFailure(Exception exception) => exception is
            JsonException or NotSupportedException or InvalidOperationException or ArgumentException;
    }

    internal static ProcessOperationResult Failed(string code, string message, string location) =>
        ProcessOperationResult.Failed(new(
            code,
            DiagnosticSeverity.Error,
            message,
            location));
}

/// <summary>Immutable exact-reference deployment catalog of typed Relation/Query handlers.</summary>
/// <remarks>
/// The catalog is a runtime deployment projection, not a definition authority. Lookup requires the complete
/// canonical definition identity, revision, and fingerprint. A changed contract, implementation version,
/// dependency, or configuration changes that fingerprint and cannot reach the retained handler accidentally.
/// The catalog is immutable and safe for concurrent evaluation; handler thread safety remains the application's
/// responsibility.
/// </remarks>
public sealed class ProcessRelationHandlerCatalog
{
    readonly ImmutableDictionary<ExecutionDefinitionReference, ProcessRelationHandlerRegistration> registrations;
    readonly ImmutableDictionary<DefinitionRevision, ExecutionDefinitionReference> revisions;

    /// <summary>Creates an immutable catalog from exact typed handler registrations.</summary>
    /// <param name="registrations">Complete Relation/Query handlers deployed to one runtime.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registrations"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An entry is null, repeats an exact reference, or deploys conflicting fingerprints for one definition
    /// identity and revision.
    /// </exception>
    public ProcessRelationHandlerCatalog(IEnumerable<ProcessRelationHandlerRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var exact = ImmutableDictionary.CreateBuilder<ExecutionDefinitionReference, ProcessRelationHandlerRegistration>();
        var byRevision = ImmutableDictionary.CreateBuilder<DefinitionRevision, ExecutionDefinitionReference>();
        foreach (var registration in registrations)
        {
            if (registration is null)
                throw new ArgumentException("A Relation/Query handler catalog cannot contain null entries.", nameof(registrations));

            var reference = registration.Reference;
            var revision = new DefinitionRevision(reference.DefinitionId, reference.RevisionId);
            if (byRevision.TryGetValue(revision, out var retained) && retained.Fingerprint != reference.Fingerprint)
            {
                throw new ArgumentException(
                    $"Relation/Query '{reference.DefinitionId.Value}' revision '{reference.RevisionId.Value}' "
                    + "is deployed with conflicting semantic fingerprints.",
                    nameof(registrations));
            }
            if (!exact.TryAdd(reference, registration))
            {
                throw new ArgumentException(
                    $"Relation/Query '{reference.DefinitionId.Value}' revision '{reference.RevisionId.Value}' "
                    + "is deployed more than once with the same fingerprint.",
                    nameof(registrations));
            }
            byRevision[revision] = reference;
        }

        this.registrations = exact.ToImmutable();
        revisions = byRevision.ToImmutable();
    }

    /// <summary>Number of exact Relation/Query handlers deployed to this catalog.</summary>
    public int Count => registrations.Count;

    /// <summary>Evaluates one exact Relation or Query through its typed deployed handler.</summary>
    /// <param name="context">Physical operation context carrying cancellation and infrastructure attribution.</param>
    /// <param name="evaluation">Complete semantic Process evaluation context.</param>
    /// <returns>A typed portable result or structured exact-resolution/contract failure evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="evaluation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="context"/> is cancelled or the resolved handler cancels physical execution.
    /// </exception>
    public ValueTask<ProcessOperationResult> EvaluateAsync(
        OperationContext context,
        ProcessRelationEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evaluation);
        context.ThrowIfCancellationRequested();

        if (registrations.TryGetValue(evaluation.Definition, out var registration))
            return registration.EvaluateAsync(context, evaluation);

        var revision = new DefinitionRevision(
            evaluation.Definition.DefinitionId,
            evaluation.Definition.RevisionId);
        if (revisions.TryGetValue(revision, out var deployed))
        {
            return ValueTask.FromResult(ProcessRelationHandlerRegistration.Failed(
                ProcessRelationHandlerDiagnosticCodes.DefinitionFingerprintMismatch,
                $"Relation/Query '{evaluation.Definition.DefinitionId.Value}' revision "
                + $"'{evaluation.Definition.RevisionId.Value}' requested fingerprint "
                + $"'{evaluation.Definition.Fingerprint.Value}', but this runtime deploys "
                + $"'{deployed.Fingerprint.Value}'.",
                "/definition/fingerprint"));
        }

        return ValueTask.FromResult(ProcessRelationHandlerRegistration.Failed(
            ProcessRelationHandlerDiagnosticCodes.DefinitionNotRegistered,
            $"No handler is deployed for Relation/Query '{evaluation.Definition.DefinitionId.Value}' revision "
            + $"'{evaluation.Definition.RevisionId.Value}' fingerprint "
            + $"'{evaluation.Definition.Fingerprint.Value}'.",
            "/definition"));
    }

    readonly record struct DefinitionRevision(
        ExecutionDefinitionId Definition,
        ExecutionRevisionId Revision);
}
