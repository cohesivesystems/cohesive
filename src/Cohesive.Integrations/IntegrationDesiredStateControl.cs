namespace Cohesive.Integrations;

// Shared desired-state command authority. Owners validate/authorize before Apply and hold their own lock.
internal sealed class IntegrationDesiredStateControl
{
    readonly Dictionary<string, IntegrationLifecycleChangeReceipt> changes = new(StringComparer.Ordinal);
    internal long Revision;
    internal IntegrationDesiredState Desired = IntegrationDesiredState.Paused;
    internal IntegrationLifecycleChange? LastChange;

    internal static void Validate(IntegrationLifecycleChange request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentNullException.ThrowIfNull(request.Authorization);
        if (request.ExpectedRevision < 0 || !Enum.IsDefined(request.Desired) || request.AtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid lifecycle command.", nameof(request));
    }

    internal IntegrationLifecycleResult<IntegrationChangeOutcome, IntegrationLifecycleChangeReceipt> Apply(IntegrationLifecycleChange request)
    {
        if (changes.TryGetValue(request.Id, out var prior))
            return prior.Command == request ? new(IntegrationChangeOutcome.Replayed, prior) : new(IntegrationChangeOutcome.IdentityConflict);
        if (request.ExpectedRevision != Revision) return new(IntegrationChangeOutcome.RevisionConflict);
        if (Revision == long.MaxValue) return new(IntegrationChangeOutcome.RevisionExhausted);
        Desired = request.Desired; Revision++; LastChange = request;
        var receipt = new IntegrationLifecycleChangeReceipt(request, Revision);
        changes.Add(request.Id, receipt);
        return new(IntegrationChangeOutcome.Applied, receipt);
    }
}
