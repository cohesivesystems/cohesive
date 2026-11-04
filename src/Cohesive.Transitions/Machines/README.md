# Machine bindings in Cohesive.Transitions

Entity bindings in the existing `Cohesive.Transitions` assembly for [Cohesive.Machines](../../Cohesive/Machines/README.md).
Typed discriminator and predicate authoring project exact machine references into the existing
TransitionMachineLinkCatalog. Both bindings enforce edge guards. Overlapping enabled edges produce InvalidDefinition with
`transitions.execution.machine.admissionAmbiguous`; false guards produce ordinary AdmissionRejected decisions.
Discriminator movements own their status assignments; predicate movements rely on ordinary entity patches.
Guard facts bind to entity observation and guard input binds to the transition command. The integration introduces no storage, runtime or commit protocol.
See the Machines README for examples, ownership rules, supported predicate expressions and proof limits.
