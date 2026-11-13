# Explicit acceptance episodes

`TenderEpisodeModel.cs` extends the tender sketch with repeated acceptance cycles. It is a proposed
domain model, not a claim that the legacy application already has an episode identifier. The source
entity owns `AcceptanceEpisode`; an explicit guarded resubmission transition advances it. Delivery
retries never advance it.

```csharp
Acceptance = MachineReaction.On<EpisodeTender>(
        Tender, Accepted, id: "tender-to-load.episode.acceptance", revision: "1")
    .OncePerEpisode(state => state.AcceptanceEpisode)
    .Request(MarkPending,
        subject: state => state.LoadId,
        input: state => new EpisodeCommand(state.AcceptanceEpisode));

Confirmation = MachineReaction.On<EpisodeTender>(
        Tender, Confirmed, id: "tender-to-load.episode.confirmation", revision: "1")
    .After(Acceptance)
    .Request(ConfirmLoad,
        subject: state => state.LoadId,
        input: state => new EpisodeCommand(state.AcceptanceEpisode));
```

`.After` inherits the prerequisite's exact episode selector. An explicitly supplied different selector
is rejected at authoring and when restoring the portable declaration. Both reactions must still use the
same exact source entity shape and authority. A prerequisite in episode 1 cannot release episode 2.

## Identity and source authority

The episode selector lowers to a canonical `FieldPath` in the reaction document. The source shape owns
its type. No CLR expression, delegate, closure, clock or source repository lookup survives into the IR.
This initial profile supports a direct, required, non-null property of type `string`, `Guid`, `int` or
`long`. Computations, nested paths, optional fields and compound keys are rejected explicitly. A domain
needing a compound identity can store a deliberately assigned scalar episode identifier.

The key is selected from the immutable committed source receipt snapshot. Recovery selects it from
the retained Process start input, never from the current entity. The instance fingerprint includes:

```text
exact reaction revision + authority/tenant + source entity type/key + typed episode value
```

The typed value retains its source contract. Integer representations are normalized to integral values;
Guid representations use canonical formatting. String keys are ordinal and case-sensitive, with no
trimming or case folding; blank strings are rejected. Zero integer keys and empty Guid values are not
special: the domain must impose any stronger validity rules through its entity transitions.

Duplicate delivery of the same source receipt reuses its instance and operation identity. A distinct
source receipt attempting to occupy the same entity/episode/policy slot fails admission, even if its
projected command is identical. A new episode creates new work without changing policy revision. An
episode key must not be reused for later business intent. Changing the retained key while preserving
the old instance identity fails restoration.

`MachineReactionInvocation.Episode` and `MachineReactionInspection.Episode` expose the selected typed
key. Dependency inspection, durable release proofs and recovery use the same episode-qualified identity.
Existing occurrence and once-per-entity policies retain their previous identity calculation and omit
the optional episode field from their canonical documents.

## Two cycles and delayed delivery

The test source executes real entity transitions in order:

```text
Offered(1) → Accepted(1) → Confirmed(1) → Resubmit(2) → Accepted(2) → Confirmed(2)
```

The reaction deliveries deliberately arrive in a different order:

1. Confirmation 2 arrives first and waits durably for acceptance 2.
2. Acceptance 1 completes. Confirmation 2 remains waiting with zero dispatch attempts.
3. The load explicitly advances to episode 2 through its guarded revision transition.
4. Acceptance 2 completes and can release confirmation 2.
5. Confirmation 1 arrives late. Its own prerequisite succeeded, but its load command is stale.
6. Confirmation 2 succeeds; a lost acknowledgement replays the same target operation without another mutation.

Episode correlation is not admission permission. The load machine still owns this guard:

```csharp
var confirm = load.Edge("confirm").From(loadPending).To(loadConfirmed)
    .When((EpisodeLoad state, EpisodeCommand command) =>
        state.AcceptanceEpisode == command.Episode);
```

The test submits the stale command while the load is pending in episode 2, so the guard—not merely an
incompatible status—rejects it. Source confirmation also checks its command's episode before emitting
an occurrence, preventing an old external acknowledgement from being relabelled as current work.
The load example is a bounded acknowledgement slice: revision is shown from pending acknowledgement,
and confirmation ends that slice. It does not claim to model the complete load lifecycle.

Another test abandons acceptance 1 and confirms that episode 2 proceeds independently. Abandonment is
still explicit and does not cascade; old dependent work remains inspectable under episode 1. A new
episode does not compensate effects from a prior episode or reset its audit history.

## Runtime and boundaries

Hosting remains the same acceptance/confirmation host pair and generic recovery worker. Coordination
still lowers to an ordinary Process wait and durable success signal; there is no episode scheduler,
new store schema or transport-specific correlation rule. Exact prerequisite revision, store and
authority checks remain in force. Tests cover in-memory serialization, a new PostgreSQL connection
pool, cross-episode release rejection, canonical scalar keys, conflicting receipts and retained-key
tampering, in addition to out-of-order delivery and acknowledgement loss.

The new example uses new declaration identities. Do not reinterpret existing once-per-entity admitted
history as episode work: retain the exact old policy until it drains or is explicitly abandoned. This
change does not provide migration of already admitted instances, cross-entity episode mapping,
multiple-prerequisite joins or a force-retry mechanism for conclusively rejected work.
