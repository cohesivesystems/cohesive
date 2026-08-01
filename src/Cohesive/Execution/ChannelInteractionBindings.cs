using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostics emitted while linking canonical Request/Reply semantics to Channels.</summary>
public static class ChannelInteractionBindingDiagnosticCodes
{
    /// <summary>An exact Channel document reference cannot be resolved.</summary>
    public const string ChannelUnknown = "channels.interaction.channel.unknown";

    /// <summary>A direction does not exist in the referenced Channel exchange.</summary>
    public const string DirectionUnknown = "channels.interaction.direction.unknown";

    /// <summary>The Request contract reference cannot be resolved as an exact Request.</summary>
    public const string RequestInvalid = "channels.interaction.request.invalid";

    /// <summary>The Reply contract does not discharge the bound Request and declared terminal outcome.</summary>
    public const string ReplyInvalid = "channels.interaction.reply.invalid";

    /// <summary>A coupled binding does not use the exact Request and Reply directions of one exchange.</summary>
    public const string CoupledExchangeInvalid = "channels.interaction.coupled.invalid";

    /// <summary>A paired binding does not use two independent one-way Channels.</summary>
    public const string PairedExchangeInvalid = "channels.interaction.paired.invalid";

    /// <summary>An exact Channel direction has no authoritative realizable plan.</summary>
    public const string RealizationUnavailable = "channels.interaction.realization.unavailable";

    /// <summary>Several authoritative plans claim the same exact Channel definition.</summary>
    public const string RealizationAmbiguous = "channels.interaction.realization.ambiguous";
}

/// <summary>Exact logical direction inside one exact canonical Channel definition revision.</summary>
public sealed record ChannelDirectionBinding
{
    /// <summary>Creates an exact Channel direction binding.</summary>
    /// <param name="channel">Exact Channel definition identity, revision, and fingerprint.</param>
    /// <param name="direction">Stable logical direction inside that definition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="direction"/> is default.</exception>
    public ChannelDirectionBinding(
        ExecutionDefinitionReference channel,
        ChannelDirectionId direction)
    {
        Channel = Guard.RequireNotNull(channel);
        OneWayChannelExchange.RequireDirection(direction, nameof(direction));
        Direction = direction;
    }

    /// <summary>Exact Channel definition identity, revision, and fingerprint.</summary>
    public ExecutionDefinitionReference Channel { get; }

    /// <summary>Stable logical direction inside the referenced Channel.</summary>
    public ChannelDirectionId Direction { get; }
}

/// <summary>Physical composition selected for two canonical Request and Reply directions.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ChannelRequestReplyBindingKind
{
    /// <summary>Two independent one-way Channel definitions carry Request and Reply.</summary>
    PairedChannels = 0,

    /// <summary>One invocation or session Channel definition carries two logical directions.</summary>
    CoupledExchange = 1
}

/// <summary>Canonical Request/Reply contracts bound to two independent logical Channel directions.</summary>
/// <remarks>
/// The binding never changes envelope or contract identity. A coupled HTTP, RPC, or session realization still
/// retains two logical directions and two independent emission identities.
/// </remarks>
public sealed record ChannelRequestReplyBinding
{
    /// <summary>Creates a Request/Reply Channel binding.</summary>
    /// <param name="kind">Paired one-way Channels or one coupled two-direction exchange.</param>
    /// <param name="request">Exact canonical Request contract.</param>
    /// <param name="reply">Exact canonical Reply contract.</param>
    /// <param name="requestDirection">Logical direction carrying Request envelopes.</param>
    /// <param name="replyDirection">Logical direction carrying Reply envelopes.</param>
    /// <exception cref="ArgumentNullException">
    /// Any contract reference or direction binding is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// A coupled binding cites different Channel revisions or a paired binding cites the same Channel revision.
    /// </exception>
    public ChannelRequestReplyBinding(
        ChannelRequestReplyBindingKind kind,
        RequestContractReference request,
        ReplyContractReference reply,
        ChannelDirectionBinding requestDirection,
        ChannelDirectionBinding replyDirection)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Request/Reply Channel binding kind.");

        Request = Guard.RequireNotNull(request);
        Reply = Guard.RequireNotNull(reply);
        RequestDirection = Guard.RequireNotNull(requestDirection);
        ReplyDirection = Guard.RequireNotNull(replyDirection);
        var sameRevision = requestDirection.Channel == replyDirection.Channel;
        var sameDefinition = requestDirection.Channel.DefinitionId == replyDirection.Channel.DefinitionId;
        if ((kind == ChannelRequestReplyBindingKind.CoupledExchange && !sameRevision)
            || (kind == ChannelRequestReplyBindingKind.PairedChannels && sameDefinition))
        {
            throw new ArgumentException(
                "A coupled exchange uses one exact Channel revision and paired Channels use two distinct Channel definitions.",
                nameof(replyDirection));
        }

        Kind = kind;
    }

    /// <summary>Paired one-way Channels or one coupled two-direction exchange.</summary>
    public ChannelRequestReplyBindingKind Kind { get; }

    /// <summary>Exact canonical Request contract.</summary>
    public RequestContractReference Request { get; }

    /// <summary>Exact canonical Reply contract.</summary>
    public ReplyContractReference Reply { get; }

    /// <summary>Logical direction carrying Request envelopes.</summary>
    public ChannelDirectionBinding RequestDirection { get; }

    /// <summary>Logical direction carrying Reply envelopes.</summary>
    public ChannelDirectionBinding ReplyDirection { get; }
}

/// <summary>
/// Request/Reply binding whose two logical directions are backed by exact, authoritatively resolved Channel plans.
/// </summary>
/// <remarks>
/// A coupled invocation may use the same plan for both directions. Paired broker Channels require two independently
/// resolved plans. The canonical Request and Reply contracts remain the payload authority in either case.
/// </remarks>
public sealed class ChannelRequestReplyRealization
{
    internal ChannelRequestReplyRealization(
        ChannelRequestReplyBinding binding,
        ResolvedChannelRealizationPlan request,
        ResolvedChannelRealizationPlan reply)
    {
        Binding = binding;
        Request = request;
        Reply = reply;
    }

    /// <summary>Exact canonical interaction and logical-direction binding.</summary>
    public ChannelRequestReplyBinding Binding { get; }

    /// <summary>Authoritative realization of the Request direction's exact Channel definition.</summary>
    public ResolvedChannelRealizationPlan Request { get; }

    /// <summary>Authoritative realization of the Reply direction's exact Channel definition.</summary>
    public ResolvedChannelRealizationPlan Reply { get; }
}

/// <summary>Exact linker for canonical Request/Reply contracts and Channel directions.</summary>
public static class ChannelInteractionBindingValidator
{
    /// <summary>Validates one Request/Reply binding against exact interaction and Channel documents.</summary>
    /// <param name="binding">Binding to validate.</param>
    /// <param name="interactions">Validated exact interaction-contract catalog.</param>
    /// <param name="channels">Canonical Channel documents available to the binding.</param>
    /// <returns>Deterministically ordered exact-link and topology diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="binding"/>, <paramref name="interactions"/>, or <paramref name="channels"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult Validate(
        ChannelRequestReplyBinding binding,
        InteractionContractCatalog interactions,
        IEnumerable<ExecutionDefinitionDocument> channels)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(interactions);
        ArgumentNullException.ThrowIfNull(channels);
        List<DocumentValidationDiagnostic> diagnostics = [];

        RequestContractDefinition? request = null;
        if (!interactions.TryResolve(binding.Request, out var requestDefinition)
            || (request = requestDefinition as RequestContractDefinition) is null)
        {
            diagnostics.Add(Error(
                ChannelInteractionBindingDiagnosticCodes.RequestInvalid,
                "The bound Request reference does not resolve to its exact canonical Request contract.",
                "/request"));
        }

        if (!interactions.TryResolve(binding.Reply, out var replyDefinition)
            || replyDefinition is not ReplyContractDefinition reply
            || reply.Request != binding.Request
            || request?.Response.Find(reply.Outcome) is null)
        {
            diagnostics.Add(Error(
                ChannelInteractionBindingDiagnosticCodes.ReplyInvalid,
                "The bound Reply must resolve exactly, discharge the bound Request, and select one declared terminal outcome.",
                "/reply"));
        }

        var documents = NormalizeChannels(channels, diagnostics);
        var requestChannel = Resolve(
            binding.RequestDirection,
            documents,
            "/requestDirection",
            diagnostics);
        var replyChannel = Resolve(
            binding.ReplyDirection,
            documents,
            "/replyDirection",
            diagnostics);
        if (requestChannel is not null && replyChannel is not null)
        {
            if (binding.Kind == ChannelRequestReplyBindingKind.CoupledExchange)
                ValidateCoupled(binding, requestChannel, diagnostics);
            else
                ValidatePaired(binding, requestChannel, replyChannel, diagnostics);
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return diagnostics.Count == 0
            ? DocumentValidationResult.Valid
            : new([.. diagnostics]);
    }

    static ImmutableArray<(ExecutionDefinitionReference Reference, ChannelDefinition Definition)> NormalizeChannels(
        IEnumerable<ExecutionDefinitionDocument> channels,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        List<(ExecutionDefinitionReference Reference, ChannelDefinition Definition)> values = [];
        foreach (var document in channels)
        {
            if (document is null)
                continue;
            var validation = ChannelDefinitionDocuments.Validate(document);
            if (!validation.IsValid)
            {
                foreach (var diagnostic in validation.Diagnostics)
                {
                    diagnostics.Add(diagnostic with
                    {
                        Code = ChannelInteractionBindingDiagnosticCodes.ChannelUnknown,
                        Location = "/channels" + diagnostic.Location
                    });
                }
                continue;
            }

            values.Add((
                new ExecutionDefinitionReference(
                    document.Metadata.DefinitionId,
                    document.Metadata.RevisionId,
                    document.Metadata.Fingerprint),
                document.GetDefinition<ChannelDefinition>()));
        }

        values.Sort(static (left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(
                left.Reference.DefinitionId.Value,
                right.Reference.DefinitionId.Value);
            if (comparison != 0)
                return comparison;
            return StringComparer.Ordinal.Compare(left.Reference.RevisionId.Value, right.Reference.RevisionId.Value);
        });
        return [.. values];
    }

    static ChannelDefinition? Resolve(
        ChannelDirectionBinding binding,
        ImmutableArray<(ExecutionDefinitionReference Reference, ChannelDefinition Definition)> channels,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        foreach (var candidate in channels)
        {
            if (candidate.Reference != binding.Channel)
                continue;
            if (!candidate.Definition.Exchange.GetDirections().Contains(binding.Direction))
            {
                diagnostics.Add(Error(
                    ChannelInteractionBindingDiagnosticCodes.DirectionUnknown,
                    $"Direction '{binding.Direction.Value}' is absent from the exact Channel revision.",
                    location + "/direction"));
                return null;
            }

            return candidate.Definition;
        }

        diagnostics.Add(Error(
            ChannelInteractionBindingDiagnosticCodes.ChannelUnknown,
            $"Channel '{binding.Channel.DefinitionId.Value}' at revision '{binding.Channel.RevisionId.Value}' and the supplied fingerprint is unavailable.",
            location + "/channel"));
        return null;
    }

    static void ValidateCoupled(
        ChannelRequestReplyBinding binding,
        ChannelDefinition channel,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (channel.Exchange is not RequestReplyChannelExchange exchange
            || exchange.RequestDirection != binding.RequestDirection.Direction
            || exchange.ReplyDirection != binding.ReplyDirection.Direction)
        {
            diagnostics.Add(Error(
                ChannelInteractionBindingDiagnosticCodes.CoupledExchangeInvalid,
                "A coupled binding must use the exact Request and Reply directions of one canonical Request/Reply exchange.",
                "/kind"));
        }
    }

    static void ValidatePaired(
        ChannelRequestReplyBinding binding,
        ChannelDefinition requestChannel,
        ChannelDefinition replyChannel,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (requestChannel.Exchange is not OneWayChannelExchange
            || replyChannel.Exchange is not OneWayChannelExchange)
        {
            diagnostics.Add(Error(
                ChannelInteractionBindingDiagnosticCodes.PairedExchangeInvalid,
                "A paired binding composes exactly two independent one-way Channel definitions.",
                "/kind"));
            return;
        }

        var requestTopology = requestChannel.Requirements.OfType<ChannelTopologyRequirement>().SingleOrDefault();
        var replyTopology = replyChannel.Requirements.OfType<ChannelTopologyRequirement>().SingleOrDefault();
        if (requestTopology?.Distribution != ChannelDistributionKind.PointToPoint
            || replyTopology?.Distribution != ChannelDistributionKind.PointToPoint)
        {
            diagnostics.Add(Error(
                ChannelInteractionBindingDiagnosticCodes.PairedExchangeInvalid,
                "Paired Request/Reply requires point-to-point delivery in both logical directions.",
                "/kind"));
        }

        var replyRouting = replyChannel.Requirements
            .OfType<ChannelRoutingRequirement>()
            .SingleOrDefault(requirement => requirement.Scope
                == ChannelRequirementScope.ForDirection(binding.ReplyDirection.Direction));
        if (replyRouting is null || replyRouting.Isolation == ChannelRoutingIsolationKind.None)
        {
            diagnostics.Add(Error(
                ChannelInteractionBindingDiagnosticCodes.PairedExchangeInvalid,
                "A paired Reply Channel requires dedicated, selective, or durable-dispatcher routing isolation; correlation alone is insufficient.",
                "/replyDirection"));
        }
    }

    static DocumentValidationDiagnostic Error(string code, string message, string location) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new DocumentDiagnosticEvidence(
                stage: "channel-interaction-linking",
                resolutionOptions: ["Bind exact canonical contracts and Channel directions with proven response routing isolation."]));
}

/// <summary>
/// Compiler that admits canonical Request/Reply only when both logical directions have authoritative realizations.
/// </summary>
public static class ChannelRequestReplyRealizationCompiler
{
    /// <summary>
    /// Compiles a canonical Request/Reply binding from paired one-way Channels or one coupled invocation/session.
    /// </summary>
    /// <param name="binding">Exact canonical Request/Reply and Channel-direction binding.</param>
    /// <param name="interactions">Exact canonical interaction-contract catalog.</param>
    /// <param name="channels">Exact canonical Channel definition documents.</param>
    /// <param name="realizations">Authoritatively resolved Channel plans available to the interpretation.</param>
    /// <param name="realization">Compiled Request/Reply realization when every invariant is satisfied.</param>
    /// <returns>Deterministically ordered binding and realization diagnostics.</returns>
    /// <exception cref="ArgumentNullException">Any required argument is <see langword="null"/>.</exception>
    public static DocumentValidationResult TryCompile(
        ChannelRequestReplyBinding binding,
        InteractionContractCatalog interactions,
        IEnumerable<ExecutionDefinitionDocument> channels,
        IEnumerable<ResolvedChannelRealizationPlan> realizations,
        out ChannelRequestReplyRealization? realization)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(interactions);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(realizations);

        var channelDocuments = channels.Where(static document => document is not null).ToArray();
        List<DocumentValidationDiagnostic> diagnostics =
            [.. ChannelInteractionBindingValidator.Validate(binding, interactions, channelDocuments).Diagnostics];
        var plans = Index(realizations, diagnostics);
        var request = Resolve(
            binding.RequestDirection,
            plans,
            location: "/requestDirection/channel",
            diagnostics);
        var reply = Resolve(
            binding.ReplyDirection,
            plans,
            location: "/replyDirection/channel",
            diagnostics);

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        if (diagnostics.Count != 0 || request is null || reply is null)
        {
            realization = null;
            return new([.. diagnostics]);
        }

        realization = new(binding, request, reply);
        return DocumentValidationResult.Valid;
    }

    static Dictionary<ExecutionDefinitionReference, ResolvedChannelRealizationPlan> Index(
        IEnumerable<ResolvedChannelRealizationPlan> realizations,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        Dictionary<ExecutionDefinitionReference, ResolvedChannelRealizationPlan> result = [];
        foreach (var resolved in realizations)
        {
            if (resolved is null)
                continue;
            var definition = resolved.Plan.Definition;
            if (result.TryAdd(definition, resolved))
                continue;

            diagnostics.Add(Error(
                ChannelInteractionBindingDiagnosticCodes.RealizationAmbiguous,
                $"Several authoritative plans claim Channel '{definition.DefinitionId.Value}' at revision '{definition.RevisionId.Value}'.",
                "/realizations"));
        }
        return result;
    }

    static ResolvedChannelRealizationPlan? Resolve(
        ChannelDirectionBinding direction,
        IReadOnlyDictionary<ExecutionDefinitionReference, ResolvedChannelRealizationPlan> realizations,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (realizations.TryGetValue(direction.Channel, out var resolved) && resolved.IsRealizable)
            return resolved;

        diagnostics.Add(Error(
            ChannelInteractionBindingDiagnosticCodes.RealizationUnavailable,
            $"Direction '{direction.Direction.Value}' requires one authoritative realizable plan for the exact Channel revision.",
            location));
        return null;
    }

    static DocumentValidationDiagnostic Error(string code, string message, string location) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new DocumentDiagnosticEvidence(
                stage: "channel-interaction-realization",
                resolutionOptions: ["Resolve both exact Channel plans against their trusted definitions, profiles, and compiler provenance."]));
}
