using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostics emitted while validating canonical Channel definitions.</summary>
public static class ChannelDefinitionDiagnosticCodes
{
    /// <summary>The exchange has no exact topology requirement.</summary>
    public const string TopologyMissing = "channels.definition.topology.missing";

    /// <summary>A requirement uses an unsupported scope for its semantic family.</summary>
    public const string ScopeInvalid = "channels.definition.scope.invalid";

    /// <summary>A singleton semantic facet or one exact repeatable mode is declared more than once in a scope.</summary>
    public const string RequirementDuplicate = "channels.definition.requirement.duplicate";

    /// <summary>The interaction shape conflicts with the one-way or Request/Reply exchange.</summary>
    public const string ExchangeInvalid = "channels.definition.exchange.invalid";

    /// <summary>A logical direction omits a required semantic facet.</summary>
    public const string DirectionIncomplete = "channels.definition.direction.incomplete";

    /// <summary>Routing cannot prove the requested acquisition isolation.</summary>
    public const string RoutingUnsafe = "channels.definition.routing.unsafe";

    /// <summary>Framing and application-boundary semantics conflict.</summary>
    public const string FramingInvalid = "channels.definition.framing.invalid";

    /// <summary>Retention and replay semantics conflict.</summary>
    public const string PersistenceInvalid = "channels.definition.persistence.invalid";

    /// <summary>Durable progress is unavailable under the declared persistence semantics.</summary>
    public const string ProgressInvalid = "channels.definition.progress.invalid";

    /// <summary>Delivery guarantee, reliability, and persistence semantics conflict.</summary>
    public const string DeliveryInvalid = "channels.definition.delivery.invalid";

    /// <summary>Settlement operations lack compatible durable progress or exchange semantics.</summary>
    public const string SettlementInvalid = "channels.definition.settlement.invalid";

    /// <summary>Streaming interaction semantics lack compatible flow or completion behavior.</summary>
    public const string FlowInvalid = "channels.definition.flow.invalid";

    /// <summary>An atomic coupling references semantics not declared by the Channel.</summary>
    public const string AtomicityInvalid = "channels.definition.atomicity.invalid";

    /// <summary>An operating-limit dimension is declared more than once in one scope.</summary>
    public const string LimitDuplicate = "channels.definition.limit.duplicate";
}

/// <summary>Semantic validator for canonical provider-neutral Channel definitions.</summary>
public static class ChannelDefinitionValidator
{
    /// <summary>Validates one canonical Channel definition independently of any target.</summary>
    /// <param name="definition">Definition to validate.</param>
    /// <returns>Deterministically ordered portable diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(ChannelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<DocumentValidationDiagnostic> diagnostics = [];
        var directions = definition.Exchange.GetDirections();

        ValidateScopesAndMultiplicity(definition, directions, diagnostics);
        var topology = Find<ChannelTopologyRequirement>(definition, ChannelRequirementScope.Exchange);
        if (topology is null)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.TopologyMissing,
                "A Channel requires one exchange-wide topology requirement.",
                "/requirements"));
        }
        else
        {
            ValidateExchange(definition.Exchange, topology, diagnostics);
        }

        foreach (var direction in directions)
            ValidateDirection(definition, direction, topology, diagnostics);

        ValidateFlow(definition, topology, diagnostics);
        ValidateAtomicity(definition, diagnostics);
        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return diagnostics.Count == 0
            ? DocumentValidationResult.Valid
            : new([.. diagnostics]);
    }

    static void ValidateScopesAndMultiplicity(
        ChannelDefinition definition,
        ImmutableArray<ChannelDirectionId> directions,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        HashSet<(ChannelRequirementScopeKind ScopeKind, string? Direction, Type Type)> facets = [];
        HashSet<(ChannelRequirementScopeKind ScopeKind, string? Direction, ChannelLimitKind Kind)> limits = [];
        HashSet<(
            ChannelRequirementScopeKind ScopeKind,
            string? Direction,
            ChannelSettlementKind Operation,
            ChannelSettlementCouplingKind Coupling)> settlements = [];
        HashSet<ChannelAtomicScopeId> atomicScopes = [];
        foreach (var requirement in definition.Requirements)
        {
            var location = Location(definition, requirement);
            if (requirement.Scope.Direction is { } direction && !directions.Contains(direction))
            {
                diagnostics.Add(Error(
                    ChannelDefinitionDiagnosticCodes.ScopeInvalid,
                    $"Requirement '{requirement.Id.Value}' addresses unknown direction '{direction.Value}'.",
                    location + "/scope/direction"));
            }

            var exchangeScoped = requirement is ChannelTopologyRequirement
                or ChannelAtomicityRequirement
                or ChannelSecurityRequirement;
            var directionScoped = requirement is ChannelRoutingRequirement
                or ChannelFramingRequirement
                or ChannelPersistenceRequirement
                or ChannelProgressRequirement
                or ChannelDeliveryRequirement
                or ChannelReliabilityRequirement
                or ChannelSettlementRequirement
                or ChannelLimitRequirement;
            if (exchangeScoped && requirement.Scope.Kind != ChannelRequirementScopeKind.Exchange
                || directionScoped && requirement.Scope.Kind != ChannelRequirementScopeKind.Direction)
            {
                diagnostics.Add(Error(
                    ChannelDefinitionDiagnosticCodes.ScopeInvalid,
                    $"Requirement family '{requirement.WireName}' has an invalid '{requirement.Scope.Kind}' scope.",
                    location + "/scope"));
            }

            if (requirement is ChannelLimitRequirement limit)
            {
                if (!limits.Add((requirement.Scope.Kind, requirement.Scope.Direction?.Value, limit.Kind)))
                {
                    diagnostics.Add(Error(
                        ChannelDefinitionDiagnosticCodes.LimitDuplicate,
                        $"Operating limit '{limit.Kind}' is declared more than once in the same scope.",
                        location));
                }
            }
            else if (requirement is ChannelSettlementRequirement settlement)
            {
                if (!settlements.Add((
                        requirement.Scope.Kind,
                        requirement.Scope.Direction?.Value,
                        settlement.Operation,
                        settlement.Coupling)))
                {
                    diagnostics.Add(Error(
                        ChannelDefinitionDiagnosticCodes.RequirementDuplicate,
                        $"Settlement mode '{settlement.Operation}/{settlement.Coupling}' is declared more than once in the same scope.",
                        location));
                }
            }
            else if (requirement is ChannelAtomicityRequirement atomicity)
            {
                if (!atomicScopes.Add(atomicity.AtomicScope))
                {
                    diagnostics.Add(Error(
                        ChannelDefinitionDiagnosticCodes.RequirementDuplicate,
                        $"Atomic scope '{atomicity.AtomicScope.Value}' is declared more than once; declare one requirement containing the complete operation set.",
                        location));
                }
            }
            else if (!facets.Add((requirement.Scope.Kind, requirement.Scope.Direction?.Value, requirement.GetType())))
            {
                diagnostics.Add(Error(
                    ChannelDefinitionDiagnosticCodes.RequirementDuplicate,
                    $"Requirement family '{requirement.WireName}' is declared more than once in the same scope.",
                    location));
            }
        }
    }

    static void ValidateExchange(
        ChannelExchangeDefinition exchange,
        ChannelTopologyRequirement topology,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var expectsRequestReply = topology.Interaction is ChannelInteractionShape.UnaryInvocation
            or ChannelInteractionShape.RequestStream
            or ChannelInteractionShape.ResponseStream
            or ChannelInteractionShape.BidirectionalStream
            or ChannelInteractionShape.CorrelatedRequestReply;
        if (expectsRequestReply != (exchange is RequestReplyChannelExchange))
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.ExchangeInvalid,
                expectsRequestReply
                    ? $"Interaction shape '{topology.Interaction}' requires distinct Request and Reply directions."
                    : $"Interaction shape '{topology.Interaction}' requires one one-way direction.",
                "/exchange"));
        }
    }

    static void ValidateDirection(
        ChannelDefinition definition,
        ChannelDirectionId direction,
        ChannelTopologyRequirement? topology,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var scope = ChannelRequirementScope.ForDirection(direction);
        var routing = Find<ChannelRoutingRequirement>(definition, scope);
        var framing = Find<ChannelFramingRequirement>(definition, scope);
        var persistence = Find<ChannelPersistenceRequirement>(definition, scope);
        var progress = Find<ChannelProgressRequirement>(definition, scope);
        var delivery = Find<ChannelDeliveryRequirement>(definition, scope);
        var reliability = Find<ChannelReliabilityRequirement>(definition, scope);

        RequireDirectionFacet(routing, direction, ChannelWireNames.RoutingRequirement, diagnostics);
        RequireDirectionFacet(framing, direction, ChannelWireNames.FramingRequirement, diagnostics);
        RequireDirectionFacet(persistence, direction, ChannelWireNames.PersistenceRequirement, diagnostics);
        RequireDirectionFacet(delivery, direction, ChannelWireNames.DeliveryRequirement, diagnostics);
        RequireDirectionFacet(reliability, direction, ChannelWireNames.ReliabilityRequirement, diagnostics);

        if (definition.Exchange is RequestReplyChannelExchange requestReply
            && direction == requestReply.ReplyDirection
            && routing is { Isolation: ChannelRoutingIsolationKind.None })
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.RoutingUnsafe,
                "A Reply direction requires invocation-scoped, dedicated, selective, or dispatcher routing isolation; correlation alone is insufficient.",
                Location(definition, routing) + "/isolation"));
        }

        if (topology is not null && framing is not null)
            ValidateFraming(topology, framing, definition, diagnostics);
        if (persistence is not null)
            ValidatePersistence(persistence, definition, diagnostics);
        if (persistence is not null && progress is not null
            && persistence.Retention == ChannelRetentionKind.ActivationLocal)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.ProgressInvalid,
                "Activation-local delivery cannot claim durable application or acknowledgement progress.",
                Location(definition, progress)));
        }
        if (persistence is not null && delivery is not null && reliability is not null)
            ValidateDelivery(persistence, delivery, reliability, definition, diagnostics);
        if (progress is not null && delivery is not null)
            ValidateProgress(progress, delivery, definition, diagnostics);
        foreach (var settlement in FindAll<ChannelSettlementRequirement>(definition, scope))
            ValidateSettlement(definition, topology, persistence, progress, settlement, diagnostics);
    }

    static void ValidateFraming(
        ChannelTopologyRequirement topology,
        ChannelFramingRequirement framing,
        ChannelDefinition definition,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (topology.Interaction == ChannelInteractionShape.Datagram
            && framing.Framing != ChannelFramingKind.Datagram)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.FramingInvalid,
                "A datagram interaction requires datagram framing.",
                Location(definition, framing) + "/framing"));
        }
        if (framing.Framing == ChannelFramingKind.ByteStream
            && framing.Boundaries == ChannelBoundarySemantics.Preserved)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.FramingInvalid,
                "A byte stream cannot natively preserve application message boundaries; declare a codec or unpreserved boundaries.",
                Location(definition, framing) + "/boundaries"));
        }
        if (framing.Framing is ChannelFramingKind.TypedMessage or ChannelFramingKind.FramedMessage
            && framing.Boundaries == ChannelBoundarySemantics.Unpreserved)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.FramingInvalid,
                "Typed-message and framed-message delivery require preserved or codec-reconstructed boundaries.",
                Location(definition, framing) + "/boundaries"));
        }
    }

    static void ValidatePersistence(
        ChannelPersistenceRequirement persistence,
        ChannelDefinition definition,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var invalid = persistence.Retention switch
        {
            ChannelRetentionKind.ActivationLocal => persistence.Replay != ChannelReplayKind.None
                || persistence.MinimumRetention is not null,
            ChannelRetentionKind.DurableUntilSettled => persistence.Replay != ChannelReplayKind.None
                || persistence.MinimumRetention is not null,
            ChannelRetentionKind.RetainedHistory => false,
            ChannelRetentionKind.RetainedLatest => persistence.Replay != ChannelReplayKind.None
                || persistence.MinimumRetention is not null,
            _ => true
        };
        if (invalid)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.PersistenceInvalid,
                "Activation-local, durable-until-settled, and retained-latest delivery do not imply replayable history; only retained history may declare replay operations or a history window.",
                Location(definition, persistence)));
        }
    }

    static void ValidateDelivery(
        ChannelPersistenceRequirement persistence,
        ChannelDeliveryRequirement delivery,
        ChannelReliabilityRequirement reliability,
        ChannelDefinition definition,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (delivery.Guarantee is ChannelDeliveryGuaranteeKind.AtLeastOnce
                or ChannelDeliveryGuaranteeKind.ProtocolExactlyOnce
            && reliability.Reliability != ChannelReliabilityKind.Reliable)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.DeliveryInvalid,
                "At-least-once and protocol-scoped exactly-once delivery require reliable transport semantics inside the declared operating boundary.",
                Location(definition, delivery) + "/guarantee"));
        }
        if (persistence.Retention != ChannelRetentionKind.ActivationLocal
            && reliability.Reliability != ChannelReliabilityKind.Reliable)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.DeliveryInvalid,
                "Unreliable and partially reliable transport cannot satisfy durable or retained delivery without an explicit composed reliable layer.",
                Location(definition, reliability) + "/reliability"));
        }
        if (delivery.Guarantee == ChannelDeliveryGuaranteeKind.InvocationAttempt
            && persistence.Retention != ChannelRetentionKind.ActivationLocal)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.DeliveryInvalid,
                "Invocation-attempt semantics cannot claim durable or retained delivery.",
                Location(definition, delivery) + "/guarantee"));
        }
        if (persistence.Replay == ChannelReplayKind.OrderedPosition
            && delivery.Ordering == ChannelOrderingScopeKind.None)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.DeliveryInvalid,
                "Ordered-position replay requires a declared delivery ordering domain.",
                Location(definition, delivery) + "/ordering"));
        }
    }

    static void ValidateProgress(
        ChannelProgressRequirement progress,
        ChannelDeliveryRequirement delivery,
        ChannelDefinition definition,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var requiresOrdering = progress.Floor == ChannelProgressFloorKind.CumulativePrefix
            || progress.Pending == ChannelPendingProgressKind.PrefixWithUnresolvedGaps;
        if (requiresOrdering && delivery.Ordering == ChannelOrderingScopeKind.None)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.ProgressInvalid,
                "Cumulative-prefix and unresolved-gap progress require a declared delivery ordering domain.",
                Location(definition, progress)));
        }
    }

    static void ValidateSettlement(
        ChannelDefinition definition,
        ChannelTopologyRequirement? topology,
        ChannelPersistenceRequirement? persistence,
        ChannelProgressRequirement? progress,
        ChannelSettlementRequirement settlement,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (persistence is null
            || (persistence.Retention == ChannelRetentionKind.ActivationLocal
                && settlement.Operation != ChannelSettlementKind.InvocationCoupled))
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.SettlementInvalid,
                "Provider settlement requires durable delivery; activation-local completion must be invocation-coupled.",
                Location(definition, settlement)));
        }
        if (settlement.Operation == ChannelSettlementKind.CumulativePrefix
            && progress?.Floor is not (ChannelProgressFloorKind.CumulativePrefix or ChannelProgressFloorKind.TargetManaged))
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.SettlementInvalid,
                "Cumulative settlement requires durable cumulative or target-managed floor evidence.",
                Location(definition, settlement) + "/operation"));
        }
        var identityAddressed = settlement.Operation is ChannelSettlementKind.Individual
            or ChannelSettlementKind.Batch
            or ChannelSettlementKind.Negative
            or ChannelSettlementKind.Defer
            or ChannelSettlementKind.Quarantine;
        if (identityAddressed && (progress is null || progress.Pending == ChannelPendingProgressKind.None))
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.SettlementInvalid,
                "Identity-addressed settlement requires exact, gap-aware, or target-managed delivery identity evidence.",
                Location(definition, settlement) + "/operation"));
        }
        if (settlement.Operation == ChannelSettlementKind.InvocationCoupled
            && topology?.Interaction is not (ChannelInteractionShape.UnaryInvocation
                or ChannelInteractionShape.RequestStream
                or ChannelInteractionShape.ResponseStream
                or ChannelInteractionShape.BidirectionalStream))
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.SettlementInvalid,
                "Invocation-coupled completion requires an invocation or streaming Request/Reply interaction.",
                Location(definition, settlement) + "/operation"));
        }
    }

    static void ValidateFlow(
        ChannelDefinition definition,
        ChannelTopologyRequirement? topology,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (topology is null)
            return;

        var flows = definition.Requirements.OfType<ChannelFlowRequirement>().ToArray();
        var streaming = topology.Interaction is ChannelInteractionShape.RequestStream
            or ChannelInteractionShape.ResponseStream
            or ChannelInteractionShape.BidirectionalStream;
        if (streaming && flows.Length == 0)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.FlowInvalid,
                "A streaming Channel requires explicit flow-control, completion, and continuity semantics.",
                "/requirements"));
        }
        if (!streaming && flows.Any(static flow => flow.Completion != ChannelStreamCompletionKind.Terminal))
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.FlowInvalid,
                "Half-close and independent-direction completion are meaningful only for streaming interactions.",
                "/requirements"));
        }
        if (topology.Interaction is ChannelInteractionShape.Publication or ChannelInteractionShape.Datagram
            && flows.Any(static flow => flow.InitiationLease is not null))
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.FlowInvalid,
                "An interaction-initiation lease requires fire-and-forget, invocation, or streaming-session semantics.",
                "/requirements"));
        }
    }

    static void ValidateAtomicity(
        ChannelDefinition definition,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var hasSettlement = definition.Requirements.Any(static requirement => requirement is ChannelSettlementRequirement);
        var hasProgress = definition.Requirements.Any(static requirement => requirement is ChannelProgressRequirement);
        var requestReply = definition.Exchange is RequestReplyChannelExchange;
        foreach (var atomicity in definition.Requirements.OfType<ChannelAtomicityRequirement>())
        {
            if (atomicity.Operations.Contains(ChannelAtomicOperationKind.Settlement) && !hasSettlement
                || atomicity.Operations.Contains(ChannelAtomicOperationKind.ApplicationCheckpoint) && !hasProgress)
            {
                diagnostics.Add(Error(
                    ChannelDefinitionDiagnosticCodes.AtomicityInvalid,
                    "Atomic settlement or checkpoint coupling requires the corresponding settlement and durable-progress semantics.",
                    Location(definition, atomicity) + "/operations"));
            }
            if (!requestReply
                && (atomicity.Operations.Contains(ChannelAtomicOperationKind.RequestAdmission)
                    || atomicity.Operations.Contains(ChannelAtomicOperationKind.ReplyObligation)))
            {
                diagnostics.Add(Error(
                    ChannelDefinitionDiagnosticCodes.AtomicityInvalid,
                    "Atomic Request admission and Reply obligation operations require Request/Reply Channel semantics.",
                    Location(definition, atomicity) + "/operations"));
            }
        }
    }

    static void RequireDirectionFacet<TRequirement>(
        TRequirement? requirement,
        ChannelDirectionId direction,
        string wireName,
        ICollection<DocumentValidationDiagnostic> diagnostics)
        where TRequirement : ChannelRequirement
    {
        if (requirement is null)
        {
            diagnostics.Add(Error(
                ChannelDefinitionDiagnosticCodes.DirectionIncomplete,
                $"Direction '{direction.Value}' requires one '{wireName}' requirement.",
                "/requirements"));
        }
    }

    static TRequirement? Find<TRequirement>(
        ChannelDefinition definition,
        ChannelRequirementScope scope)
        where TRequirement : ChannelRequirement
    {
        foreach (var requirement in definition.Requirements)
        {
            if (requirement is TRequirement typed && typed.Scope == scope)
                return typed;
        }

        return null;
    }

    static IEnumerable<TRequirement> FindAll<TRequirement>(
        ChannelDefinition definition,
        ChannelRequirementScope scope)
        where TRequirement : ChannelRequirement
    {
        foreach (var requirement in definition.Requirements)
        {
            if (requirement is TRequirement typed && typed.Scope == scope)
                yield return typed;
        }
    }

    static string Location(ChannelDefinition definition, ChannelRequirement requirement)
    {
        for (var index = 0; index < definition.Requirements.Length; index++)
        {
            if (ReferenceEquals(definition.Requirements[index], requirement)
                || definition.Requirements[index].Id == requirement.Id)
            {
                return $"/requirements/{index}";
            }
        }

        return "/requirements";
    }

    static DocumentValidationDiagnostic Error(string code, string message, string location) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new DocumentDiagnosticEvidence(
                stage: "channel-definition-validation",
                resolutionOptions: ["Change the Channel requirement or select a compatible explicit composition."]));
}
