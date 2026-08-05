using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Transitions.Execution;

/// <summary>Stable diagnostics emitted while lowering Transition intents to canonical interaction envelopes.</summary>
public static class TransitionEmissionLoweringDiagnosticCodes
{
    /// <summary>The resolved interaction family cannot be emitted by a canonical Transition.</summary>
    public const string ContractKindUnsupported = "transitions.emissionLowering.contractKind.unsupported";

    /// <summary>The coordinator supplied origin evidence inconsistent with the Transition decision.</summary>
    public const string OriginIncompatible = "transitions.emissionLowering.origin.incompatible";

    /// <summary>A Request intent has no explicit response target.</summary>
    public const string RequestTargetUnavailable = "transitions.emissionLowering.requestTarget.unavailable";

    /// <summary>An intent payload is still unknown or failed at the interaction boundary.</summary>
    public const string PayloadUnavailable = "transitions.emissionLowering.payload.unavailable";

    /// <summary>More than one intent claimed the same logical emission identity.</summary>
    public const string EmissionIdentityDuplicate = "transitions.emissionLowering.emissionIdentity.duplicate";
}

/// <summary>Coordinator-owned context policy for realizing pure Transition emission intents.</summary>
/// <remarks>
/// The policy supplies occurrence identity, authority, correlation, delivery, and publication-coordinator evidence;
/// the lowerer remains the sole authority for resolving the exact interaction family and constructing its envelope.
/// </remarks>
public sealed class TransitionEmissionLoweringPolicy
{
    readonly Func<TransitionEmissionIntent, int, InteractionEnvelopeContext> createContext;
    readonly Func<TransitionEmissionIntent, int, InteractionTarget?>? createRequestTarget;

    /// <summary>Creates a coordinator-owned lowering policy.</summary>
    /// <param name="createContext">
    /// Creates complete canonical interaction context for an intent and its zero-based decision-order index.
    /// </param>
    /// <param name="createRequestTarget">
    /// Optionally creates the explicit response target for Request intents. It is not called for Domain Events.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="createContext"/> is <see langword="null"/>.</exception>
    public TransitionEmissionLoweringPolicy(
        Func<TransitionEmissionIntent, int, InteractionEnvelopeContext> createContext,
        Func<TransitionEmissionIntent, int, InteractionTarget?>? createRequestTarget = null)
    {
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        this.createRequestTarget = createRequestTarget;
    }

    internal InteractionEnvelopeContext CreateContext(TransitionEmissionIntent intent, int index) =>
        createContext(intent, index)
        ?? throw new InvalidOperationException("A Transition emission context policy returned null.");

    internal InteractionTarget? CreateRequestTarget(TransitionEmissionIntent intent, int index) =>
        createRequestTarget?.Invoke(intent, index);
}

/// <summary>Lowers pure Transition emission intents through exact canonical interaction contracts.</summary>
public static class TransitionEmissionEnvelopeLowerer
{
    /// <summary>Attempts to lower every committed Transition emission intent as one fail-closed operation.</summary>
    /// <param name="decision">Complete deterministic Transition decision whose intents are lowered.</param>
    /// <param name="contracts">Exact interaction-contract catalog used for linking and payload validation.</param>
    /// <param name="policy">Coordinator-owned identity, context, and Request-target policy.</param>
    /// <param name="envelopes">
    /// Receives the complete ordered canonical envelope sequence on success, or an empty array on failure.
    /// </param>
    /// <returns>Structured exact-link, provenance, identity, target, and payload diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="decision"/>, <paramref name="contracts"/>, or <paramref name="policy"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The coordinator policy returns a null interaction context.
    /// </exception>
    public static DocumentValidationResult TryLower(
        TransitionDecision decision,
        InteractionContractCatalog contracts,
        TransitionEmissionLoweringPolicy policy,
        out ImmutableArray<InteractionEnvelope> envelopes)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(policy);

        if (decision.Emissions.IsEmpty)
        {
            envelopes = [];
            return DocumentValidationResult.Valid;
        }

        List<DocumentValidationDiagnostic> diagnostics = [];
        var lowered = ImmutableArray.CreateBuilder<InteractionEnvelope>(decision.Emissions.Length);
        HashSet<EmissionId> identities = [];
        var outcomeNode = decision.Evidence.Trace.LastOrDefault(static trace =>
            trace.Kind == TransitionTraceEventKind.OutcomeReturned)?.Node;

        for (var index = 0; index < decision.Emissions.Length; index++)
        {
            var intent = decision.Emissions[index];
            var location = $"/emissions/{index}";
            var referenceValidation = contracts.ValidateReference(
                intent.Contract,
                location + "/contract",
                out var definition);
            AddDiagnostics(referenceValidation, diagnostics);
            if (definition is null)
                continue;
            if (intent.Payload.State is PortableValueState.Unknown or PortableValueState.Failed)
            {
                diagnostics.Add(Error(
                    TransitionEmissionLoweringDiagnosticCodes.PayloadUnavailable,
                    "A Transition emission payload must be materially known before it crosses the interaction boundary.",
                    location + "/payload"));
                continue;
            }

            var context = policy.CreateContext(intent, index);
            if (!OriginMatches(decision, intent, context.Origin, outcomeNode))
            {
                diagnostics.Add(Error(
                    TransitionEmissionLoweringDiagnosticCodes.OriginIncompatible,
                    "Interaction origin must identify the exact Transition, emission node, aggregate subject, and terminal outcome.",
                    location + "/context/origin"));
                continue;
            }
            if (!identities.Add(context.EmissionId))
            {
                diagnostics.Add(Error(
                    TransitionEmissionLoweringDiagnosticCodes.EmissionIdentityDuplicate,
                    $"Logical emission identity '{context.EmissionId.Value}' is claimed by more than one Transition intent.",
                    location + "/context/emissionId"));
                continue;
            }

            InteractionEnvelope? envelope = definition switch
            {
                DomainEventContractDefinition => new DomainEventEnvelope(
                    InteractionEnvelope.CurrentSchemaVersion,
                    context,
                    new DomainEventContractReference(intent.Contract),
                    intent.Payload),
                RequestContractDefinition => LowerRequest(intent, index, context, policy, location, diagnostics),
                _ => null
            };
            if (envelope is null)
            {
                if (definition is not RequestContractDefinition)
                {
                    diagnostics.Add(Error(
                        TransitionEmissionLoweringDiagnosticCodes.ContractKindUnsupported,
                        $"Canonical Transition emissions support Domain Event and Request contracts, not '{definition.GetType().Name}'.",
                        location + "/contract"));
                }
                continue;
            }

            var envelopeValidation = InteractionEnvelopeValidator.Validate(
                envelope,
                contracts,
                contracts.ShapeGraph);
            foreach (var diagnostic in envelopeValidation.Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    Location = location + "/envelope" + (diagnostic.Location ?? string.Empty)
                });
            }
            lowered.Add(envelope);
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        if (diagnostics.Count > 0)
        {
            envelopes = [];
            return DocumentValidationResult.FromDiagnostics(diagnostics);
        }

        envelopes = lowered.MoveToImmutable();
        return DocumentValidationResult.Valid;
    }

    static RequestEnvelope? LowerRequest(
        TransitionEmissionIntent intent,
        int index,
        InteractionEnvelopeContext context,
        TransitionEmissionLoweringPolicy policy,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var target = policy.CreateRequestTarget(intent, index);
        if (target is null)
        {
            diagnostics.Add(Error(
                TransitionEmissionLoweringDiagnosticCodes.RequestTargetUnavailable,
                "A canonical Request emission requires an explicit response target.",
                location + "/responseTarget"));
            return null;
        }

        return new(
            InteractionEnvelope.CurrentSchemaVersion,
            context,
            new RequestContractReference(intent.Contract),
            intent.Payload,
            target);
    }

    static bool OriginMatches(
        TransitionDecision decision,
        TransitionEmissionIntent intent,
        InteractionOrigin origin,
        ExecutionNodeId? outcomeNode) => origin switch
        {
            TransitionInteractionOrigin direct =>
                direct.Definition == decision.Evidence.Definition
                && direct.Node == intent.Node
                && direct.Outcome == outcomeNode,
            ProcessInteractionOrigin process =>
                process.Transition == decision.Evidence.Definition
                && process.TransitionNode == intent.Node
                && process.Entity is not null
                && process.Outcome == outcomeNode,
            _ => false
        };

    static void AddDiagnostics(
        DocumentValidationResult validation,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        foreach (var diagnostic in validation.Diagnostics)
            diagnostics.Add(diagnostic);
    }

    static DocumentValidationDiagnostic Error(string code, string message, string location) =>
        new(code, DiagnosticSeverity.Error, message, location);
}
