# XState v5 machine adapter

`compileMachine(document)` takes a trusted canonical Cohesive machine document, such as the parsed `MachineDefinition.CanonicalJson` of the tender example. Verify documents from external sources through `MachineDefinition.Deserialize` before export.

```js
import { createActor } from 'xstate';
import { compileMachine } from './machine.mjs';

const actor = createActor(compileMachine(tenderDocument)).start();
actor.send({ type: 'command', action: 'attach-load', facts: {}, input: {} });
const { decision } = actor.getSnapshot().context;
// { kind: 'Accepted', movement: 'attach-load' }
```

Supply JSON facts and action input for each request. The adapter supports flat extension-free machines and boolean field reads, conjunction, disjunction and negation on required, non-null records of boolean and string fields. Other guard constructs fail during compilation with the movement identity.

An internal transient state routes each decision to its selected destination in the same XState macrostep. Consumers can inspect the stable snapshot's state-node metadata for the Cohesive state identity and terminal flag. Terminal nodes continue to answer admission queries, including illegal and unknown actions. The root metadata identifies the source definition, revision and fingerprint. An accepted decision describes a legal movement. Persisting entity effects, issuing commit receipts and hosting durable reactions remain responsibilities of the entity and reaction hosts.

The CI conformance fixture compiles the actual tender declaration and a deliberately overlapping pair of guards, derives expected decisions with the .NET evaluator, and sends the same requests through XState actors.
