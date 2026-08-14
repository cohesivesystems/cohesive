using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;
using Cohesive.Relations.Authoring;

namespace Cohesive.Processes.Execution;

/// <summary>Stable diagnostics emitted by exact hosted-Query runtime dispatch.</summary>
public static class HostedQueryHandlerDiagnosticCodes
{
    /// <summary>No handler is deployed for the requested exact definition.</summary>
    public const string DefinitionNotRegistered = "processes.hostedQuery.definition.notRegistered";

    /// <summary>The requested definition identity/revision is deployed with another semantic fingerprint.</summary>
    public const string DefinitionFingerprintMismatch = "processes.hostedQuery.definition.fingerprintMismatch";

    /// <summary>The invocation value does not carry the canonical hosted-Query input contract.</summary>
    public const string InputContractMismatch = "processes.hostedQuery.input.contractMismatch";

    /// <summary>The invocation value is not one concrete portable input.</summary>
    public const string InputValueInvalid = "processes.hostedQuery.input.invalid";

    /// <summary>A portable value could not be converted to or from its registered CLR projection.</summary>
    public const string ValueConversionFailed = "processes.hostedQuery.value.conversionFailed";

    /// <summary>The typed handler result does not satisfy the canonical hosted-Query result contract.</summary>
    public const string ResultValueInvalid = "processes.hostedQuery.result.invalid";
}

/// <summary>Executes one typed exact hosted-Query evaluation.</summary>
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
public delegate ValueTask<TResult> HostedQueryHandler<TInput, TResult>(
    OperationContext context,
    ProcessRelationEvaluation evaluation,
    TInput input)
    where TInput : notnull
    where TResult : notnull;

/// <summary>Closed typed success or structured semantic failure from a hosted-Query handler.</summary>
/// <typeparam name="TResult">CLR projection of the canonical singular result contract.</typeparam>
/// <remarks>
/// This outcome is runtime evidence rather than canonical Query content. Use failure for an expected inability to
/// produce the declared result after typed input admission. Throwing remains reserved for physical execution
/// failure, and cancellation remains observable through the operation context.
/// </remarks>
public sealed class HostedQueryHandlerOutcome<TResult>
    where TResult : notnull
{
    readonly TResult? value;

    HostedQueryHandlerOutcome(bool isSuccessful, TResult? value, DocumentValidationDiagnostic? failure)
    {
        IsSuccessful = isSuccessful;
        this.value = value;
        Failure = failure;
    }

    /// <summary>Typed singular value from a successful handler outcome.</summary>
    /// <exception cref="InvalidOperationException">This is a failed outcome.</exception>
    public TResult Value => IsSuccessful
        ? value!
        : throw new InvalidOperationException("A failed hosted-Query outcome has no result value.");

    /// <summary>Structured error evidence when the handler cannot produce its declared value.</summary>
    public DocumentValidationDiagnostic? Failure { get; }

    /// <summary>Whether this outcome contains one typed singular value.</summary>
    public bool IsSuccessful { get; }

    /// <summary>Creates a successful typed outcome.</summary>
    /// <param name="value">Non-null typed value to validate against the canonical result contract.</param>
    /// <returns>A closed successful outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static HostedQueryHandlerOutcome<TResult> Completed(TResult value) =>
        new(
            isSuccessful: true,
            value ?? throw new ArgumentNullException(nameof(value)),
            failure: null);

    /// <summary>Creates a structured semantic failure with no result value.</summary>
    /// <param name="failure">One error diagnostic describing why no declared result can be produced.</param>
    /// <returns>A closed failed outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="failure"/> is not an error diagnostic.</exception>
    public static HostedQueryHandlerOutcome<TResult> Failed(DocumentValidationDiagnostic failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Severity != DiagnosticSeverity.Error)
            throw new ArgumentException("A failed hosted-Query outcome requires an error diagnostic.", nameof(failure));
        return new(isSuccessful: false, value: default, failure);
    }
}

/// <summary>Executes one typed exact hosted-Query evaluation with an explicit semantic outcome.</summary>
/// <typeparam name="TInput">CLR projection of the canonical invocation contract.</typeparam>
/// <typeparam name="TResult">CLR projection of the canonical singular result contract.</typeparam>
/// <param name="context">Physical operation context carrying cancellation and infrastructure attribution.</param>
/// <param name="evaluation">Complete canonical Process evaluation context, unchanged from the Process node.</param>
/// <param name="input">Typed concrete invocation value decoded from <paramref name="evaluation"/>.</param>
/// <returns>One typed success or structured semantic failure.</returns>
/// <remarks>
/// Throwing represents physical execution failure. Expected inability to produce the declared result is returned as
/// <see cref="HostedQueryHandlerOutcome{TResult}.Failed"/>.
/// </remarks>
public delegate ValueTask<HostedQueryHandlerOutcome<TResult>> HostedQueryOutcomeHandler<TInput, TResult>(
    OperationContext context,
    ProcessRelationEvaluation evaluation,
    TInput input)
    where TInput : notnull
    where TResult : notnull;

/// <summary>Runtime-only binding between one exact canonical hosted Query and its typed executable handler.</summary>
/// <remarks>
/// The retained canonical document is the definition authority. The handler is deployment state and never enters
/// the document, its fingerprint, or Process IR.
/// </remarks>
public abstract class HostedQueryHandlerRegistration
{
    private protected HostedQueryHandlerRegistration(
        ExecutionDefinitionDocument document,
        ValueContract inputContract,
        ValueContract resultContract)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        InputContract = inputContract ?? throw new ArgumentNullException(nameof(inputContract));
        ResultContract = resultContract ?? throw new ArgumentNullException(nameof(resultContract));
    }

    /// <summary>Canonical hosted-Query document and sole durable semantic authority for this binding.</summary>
    public ExecutionDefinitionDocument Document { get; }

    /// <summary>Exact identity, revision, and semantic fingerprint required for dispatch.</summary>
    public ExecutionDefinitionReference Reference => new(
        Document.Metadata.DefinitionId,
        Document.Metadata.RevisionId,
        Document.Metadata.Fingerprint);

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
    public static HostedQueryHandlerRegistration Create<TInput, TResult>(
        HostedQuery<TInput, TResult> query,
        HostedQueryHandler<TInput, TResult> handler)
        where TInput : notnull
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(handler);
        RequireValid(query);
        return new TypedHostedQueryHandlerRegistration<TInput, TResult>(
            query,
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
    public static HostedQueryHandlerRegistration CreateOutcome<TInput, TResult>(
        HostedQuery<TInput, TResult> query,
        HostedQueryOutcomeHandler<TInput, TResult> handler)
        where TInput : notnull
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(handler);
        RequireValid(query);
        return new TypedHostedQueryHandlerRegistration<TInput, TResult>(query, handler);
    }

    static void RequireValid<TInput, TResult>(HostedQuery<TInput, TResult> query)
        where TInput : notnull
        where TResult : notnull
    {
        if (!query.IsValid)
        {
            throw new ArgumentException(
                "A hosted-Query handler requires a canonically valid exact definition.",
                nameof(query));
        }
    }

    static async ValueTask<HostedQueryHandlerOutcome<TResult>> AsOutcome<TResult>(ValueTask<TResult> pending)
        where TResult : notnull =>
        HostedQueryHandlerOutcome<TResult>.Completed(await pending.ConfigureAwait(false));

    internal abstract ValueTask<ProcessOperationResult> EvaluateAsync(
        OperationContext context,
        ProcessRelationEvaluation evaluation);

    sealed class TypedHostedQueryHandlerRegistration<TInput, TResult> : HostedQueryHandlerRegistration
        where TInput : notnull
        where TResult : notnull
    {
        static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
        readonly HostedQueryOutcomeHandler<TInput, TResult> handler;

        internal TypedHostedQueryHandlerRegistration(
            HostedQuery<TInput, TResult> query,
            HostedQueryOutcomeHandler<TInput, TResult> handler)
            : base(query.Document, query.InputContract, query.ResultContract) =>
            this.handler = handler;

        internal override async ValueTask<ProcessOperationResult> EvaluateAsync(
            OperationContext context,
            ProcessRelationEvaluation evaluation)
        {
            if (evaluation.Input.Contract != InputContract)
            {
                return Failed(
                    HostedQueryHandlerDiagnosticCodes.InputContractMismatch,
                    "The Process evaluation input contract does not match the exact hosted-Query definition.",
                    "/input/contract");
            }
            if (evaluation.Input.State != PortableValueState.Concrete || evaluation.Input.Value is null)
            {
                return Failed(
                    HostedQueryHandlerDiagnosticCodes.InputValueInvalid,
                    "A typed hosted-Query handler requires one concrete non-null invocation value.",
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
                    HostedQueryHandlerDiagnosticCodes.ValueConversionFailed,
                    $"The hosted-Query invocation could not be decoded as '{typeof(TInput).FullName}': "
                    + exception.Message,
                    "/input/value");
            }

            var outcome = await handler(context, evaluation, input).ConfigureAwait(false);
            if (outcome is null)
            {
                return Failed(
                    HostedQueryHandlerDiagnosticCodes.ResultValueInvalid,
                    "The typed hosted-Query handler returned a null outcome.",
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
                    HostedQueryHandlerDiagnosticCodes.ValueConversionFailed,
                    $"The hosted-Query result could not be encoded from '{typeof(TResult).FullName}': "
                    + exception.Message,
                    "/result");
            }

            var resultValidation = PortableExecutionValidator.Validate(portable);
            var resultError = resultValidation.Diagnostics.FirstOrDefault(
                static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            return resultError is null
                ? ProcessOperationResult.Completed(portable)
                : Failed(
                    HostedQueryHandlerDiagnosticCodes.ResultValueInvalid,
                    $"The typed hosted-Query result violates its canonical contract: {resultError.Message}",
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

/// <summary>Immutable exact-reference deployment catalog of typed hosted-Query handlers.</summary>
/// <remarks>
/// The catalog is a runtime deployment projection, not a definition authority. Lookup requires the complete
/// canonical definition identity, revision, and fingerprint. A changed contract, implementation version,
/// dependency, or configuration changes that fingerprint and cannot reach the retained handler accidentally.
/// The catalog is immutable and safe for concurrent evaluation; handler thread safety remains the application's
/// responsibility.
/// </remarks>
public sealed class HostedQueryHandlerCatalog
{
    readonly ImmutableDictionary<ExecutionDefinitionReference, HostedQueryHandlerRegistration> registrations;
    readonly ImmutableDictionary<DefinitionRevision, ExecutionDefinitionReference> revisions;

    /// <summary>Creates an immutable catalog from exact typed handler registrations.</summary>
    /// <param name="registrations">Complete hosted-Query handlers deployed to one runtime.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registrations"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An entry is null, repeats an exact reference, or deploys conflicting fingerprints for one definition
    /// identity and revision.
    /// </exception>
    public HostedQueryHandlerCatalog(IEnumerable<HostedQueryHandlerRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var exact = ImmutableDictionary.CreateBuilder<ExecutionDefinitionReference, HostedQueryHandlerRegistration>();
        var byRevision = ImmutableDictionary.CreateBuilder<DefinitionRevision, ExecutionDefinitionReference>();
        foreach (var registration in registrations)
        {
            if (registration is null)
                throw new ArgumentException("A hosted-Query handler catalog cannot contain null entries.", nameof(registrations));

            var reference = registration.Reference;
            var revision = new DefinitionRevision(reference.DefinitionId, reference.RevisionId);
            if (byRevision.TryGetValue(revision, out var retained) && retained.Fingerprint != reference.Fingerprint)
            {
                throw new ArgumentException(
                    $"Hosted Query '{reference.DefinitionId.Value}' revision '{reference.RevisionId.Value}' "
                    + "is deployed with conflicting semantic fingerprints.",
                    nameof(registrations));
            }
            if (!exact.TryAdd(reference, registration))
            {
                throw new ArgumentException(
                    $"Hosted Query '{reference.DefinitionId.Value}' revision '{reference.RevisionId.Value}' "
                    + "is deployed more than once with the same fingerprint.",
                    nameof(registrations));
            }
            byRevision[revision] = reference;
        }

        this.registrations = exact.ToImmutable();
        revisions = byRevision.ToImmutable();
    }

    /// <summary>Number of exact hosted-Query handlers deployed to this catalog.</summary>
    public int Count => registrations.Count;

    /// <summary>Evaluates one exact hosted Query through its typed deployed handler.</summary>
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
            return ValueTask.FromResult(HostedQueryHandlerRegistration.Failed(
                HostedQueryHandlerDiagnosticCodes.DefinitionFingerprintMismatch,
                $"Hosted Query '{evaluation.Definition.DefinitionId.Value}' revision "
                + $"'{evaluation.Definition.RevisionId.Value}' requested fingerprint "
                + $"'{evaluation.Definition.Fingerprint.Value}', but this runtime deploys "
                + $"'{deployed.Fingerprint.Value}'.",
                "/definition/fingerprint"));
        }

        return ValueTask.FromResult(HostedQueryHandlerRegistration.Failed(
            HostedQueryHandlerDiagnosticCodes.DefinitionNotRegistered,
            $"No handler is deployed for hosted Query '{evaluation.Definition.DefinitionId.Value}' revision "
            + $"'{evaluation.Definition.RevisionId.Value}' fingerprint "
            + $"'{evaluation.Definition.Fingerprint.Value}'.",
            "/definition"));
    }

    readonly record struct DefinitionRevision(
        ExecutionDefinitionId Definition,
        ExecutionRevisionId Revision);
}
