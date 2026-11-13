using Cohesive.Execution;
using Cohesive.Processes.Machines;

namespace Cohesive.Storage.Processes;

// Preparation belongs to the storage interpretation, not the public authoring surface.
// Weak ownership follows immutable policies/inputs; failures are never cached.
internal sealed class MachineReactionInputs
{
    static readonly WeakPreparationCache<MachineReaction, MachineReactionInputs> Policies = new();
    readonly WeakPreparationCache<PortableValue, PortableValue> episodes = new();
    readonly Func<PortableValue, PortableValue> select;
    readonly MachineReactionIdentity identity;
    readonly ValueContract? episodeContract;

    MachineReactionInputs(MachineReaction policy)
    {
        SourceContract = ValueContract.FromShape(policy.Payload.Source);
        identity = policy.Payload.Identity;
        if (identity is MachineReactionIdentity.Episode episode)
            episodeContract = ValueContract.FromField(policy.Payload.Source.GetField(episode.Key.Segments[0].Segment!));
        select = SelectCore;
    }

    internal static MachineReactionInputs For(MachineReaction policy) => Policies.Get(policy, static value => new(value));
    internal ValueContract SourceContract { get; }
    internal PortableValue SelectEpisode(PortableValue source) => episodes.Get(source, select);

    PortableValue SelectCore(PortableValue source)
    {
        if (identity is not MachineReactionIdentity.Episode episode)
            throw new InvalidOperationException("This policy does not select episodes.");
        if (source.Contract != SourceContract || source.State != PortableValueState.Concrete
            || source.Value is not { } observation || !observation.TryGetField(episode.Key, out var selected))
            throw new InvalidOperationException("Episode identity requires the exact concrete source snapshot and episode field.");
        // Reuse the scalar semantics used by portable validation, without validating an allocated
        // PortableValue only to replace it. JSON canonicalization alone does not normalize typed Guid strings.
        if (ValueContractSemantics.Evaluate(episodeContract!.Type, selected) != ValueConstantCompatibility.Compatible
            || selected.Kind == ObservationValueKind.String && string.IsNullOrWhiteSpace(selected.GetString()))
            throw new InvalidOperationException("Episode identity must be a valid non-null scalar; string keys cannot be blank.");
        var canonical = ((ScalarTypeRef)episodeContract.Type!).Kind switch
        {
            ScalarTypeKind.Guid => ObservationValue.FromObject(Guid.Parse(selected.GetString()!)),
            ScalarTypeKind.Int32 or ScalarTypeKind.Int64 => ObservationValue.FromInt64(selected.GetInt64()),
            _ => selected
        };
        return PortableValue.Concrete(episodeContract, canonical);
    }
}
