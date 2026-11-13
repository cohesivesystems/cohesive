using Cohesive.Execution;

namespace Cohesive.Integrations;

// Shared desired-state command authority. Owners validate/authorize before Apply and hold their own lock.
internal sealed class IntegrationDesiredStateControl
{
    readonly Dictionary<string, IntegrationLifecycleChangeReceipt> changes = new(StringComparer.Ordinal);
    internal long Revision { get; private set; }
    internal bool CanAdvance => Revision < long.MaxValue;
    internal IntegrationDesiredState Desired { get; private set; } = IntegrationDesiredState.Paused;
    internal IntegrationLifecycleChange? LastChange { get; private set; }

    internal static void Validate(IntegrationLifecycleChange request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ValidateEnvelope(request.Authorization, request.ExpectedRevision, request.AtUtc);
        if (!Enum.IsDefined(request.Desired)) throw new ArgumentException("Invalid desired state.", nameof(request));
    }

    internal static void ValidateEnvelope(ProcessControlAuthorizationContext authorization, long expectedRevision, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (expectedRevision < 0 || atUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Control commands require a nonnegative revision and UTC observation time.");
    }

    internal long AdvanceRevision()
    {
        if (!CanAdvance) throw new InvalidOperationException("Control revision is exhausted.");
        return ++Revision;
    }

    internal IntegrationLifecycleResult<IntegrationChangeOutcome, IntegrationLifecycleChangeReceipt> Apply(IntegrationLifecycleChange request)
    {
        if (changes.TryGetValue(request.Id, out var prior))
            return prior.Command == request ? new(IntegrationChangeOutcome.Replayed, prior) : new(IntegrationChangeOutcome.IdentityConflict);
        if (request.ExpectedRevision != Revision) return new(IntegrationChangeOutcome.RevisionConflict);
        if (!CanAdvance) return new(IntegrationChangeOutcome.RevisionExhausted);
        Desired = request.Desired; AdvanceRevision(); LastChange = request;
        var receipt = new IntegrationLifecycleChangeReceipt(request, Revision);
        changes.Add(request.Id, receipt);
        return new(IntegrationChangeOutcome.Applied, receipt);
    }
}
