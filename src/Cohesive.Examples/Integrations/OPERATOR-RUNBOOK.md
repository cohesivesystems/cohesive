# Local historical integration operator example

[COH-164](https://linear.app/cohesive-ari/issue/COH-164) continues the local operator loop after
[COH-163](https://linear.app/cohesive-ari/issue/COH-163). This is an executable example using real
compiled historical Processes and in-memory provider/destination fixtures. It is not a production CLI,
endpoint, durable control service or Ito/ARI integration.

Run from the repository root to inspect the JSON report in test output:

```sh
dotnet test src/Cohesive.Examples/Cohesive.Examples.csproj \
  --filter FullyQualifiedName~OperatorViewSeparatesForwardProgressFromHealthyButIncompleteBackfill \
  --logger 'console;verbosity=detailed'
```

The example admits September 2 forward ingestion and September 1 backfill under one group. Forward
completes with an accepted publication and ledger revision 1. Backfill receives an empty successful
source response and fails coverage validation, leaving its admission pending. The group is then paused.
The test inspects once with current health evidence, then exports at its exact expiry boundary.
Consequently, the exported current provider health is Unknown while the original Healthy observation
remains visible. Forward progress remains accepted; backfill remains pending.

## Reading the report

| Field | Meaning |
| --- | --- |
| `groupAtStart`, `groupAtEnd` | Group policy and revision observed around the asynchronous reads. |
| `groupChangedDuringRead` | A group transition or membership change occurred during the read. False does **not** establish a consistent snapshot across members, evidence and ledgers. |
| `consumers[].requestedScope` | Original bounded historical scope from the binding's canonical property parameters, including expected sessions. |
| `consumers[].integration` | Exact binding, consumer lifecycle/revision, group observation, consumer-wide and binding-specific pending work, and expiring provider evidence. |
| `consumers[].sourceCoverage` | Domain-owned acquisition evidence, including missing sessions. Healthy transport does not imply complete coverage. |
| `consumers[].acceptedSessions`, `publication` | Sessions decoded from bytes whose digest matches the retained publication receipt. This is that session's publication, not all destination history. |
| `consumers[].sourceProgress` | The independently addressed ledger entry. Forward and backfill entries must not be combined into one checkpoint. |

Consumers are explicitly supplied by the example; this is not an enumeration of every group member.
The report does not infer provider fault from pending work, automatically retry, resolve a failure,
change admission policy or authorize dispatch. Evidence-reader errors propagate rather than appearing
as missing evidence. Sequential reads are observations, not a transactional snapshot. The separate
`OperatorViewRetainsGroupChangesDuringRead` test demonstrates a policy transition during retrieval.

## Operator actions and their authorities

1. **Pause new work:** authorize `group.Change` using its current revision. A group pause covers forward
   and backfill admission. It does not cancel admitted Processes; local desired state may remain Active.
2. **Inspect incomplete data:** compare requested scope with source coverage and retained publication,
   then read the corresponding ledger. An empty successful HTTP response cannot advance progress.
3. **Reconcile an ambiguous publication:** use exact retained Request and sink evidence, as demonstrated
   in `PartitionPausePreservesBothFlowsPublicationReconciliation`. Reconciliation remains available while
   paused. Missing evidence is inconclusive and does not grant permission to publish or fetch again.
4. **Close a known failed session:** use `ResolveFailure` only with an authorized command and a trusted
   verifier establishing complete effect accounting. A Failed Process disposition or Healthy observation
   alone is insufficient. See `HistoricalFailureResolutionExamples.cs` for the verifier boundary.
5. **Resume:** reconcile operator intent against a fresh group revision and issue an authorized Active
   command. This preserves local pauses. Admission still requires current local revision, and dispatch
   still requires its own policy, qualification and progress checks.
6. **Backfill a gap:** author a bounded scope under a separate flow identity. Shared group control does
   not merge its ledger with forward progress or waive coverage requirements.

These examples retain evidence in memory. A production host still needs authenticated inspection,
trusted evidence readers/verifiers, durable storage and dispatch synchronization. Exact binding, session
and evidence identifiers belong in inspection/traces rather than unbounded metric labels. Real-host
adoption and durable coordination remain separate follow-up work; this example adds no public API.
