import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { createActor } from 'xstate';
import { compileMachine } from './machine.mjs';

const fixtures = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const kinds = new Set();
for (const { document, cases } of fixtures) {
  test(document.metadata.definitionId, () => {
    const machine = compileMachine(document);
    const initial = createActor(machine).start();
    const stateOf = snapshot => Object.values(snapshot.getMeta()).find(meta => meta.state !== undefined);
    assert.equal(stateOf(initial.getSnapshot()).state, document.definition.initial);
    initial.stop();
    const nodes = new Map(Object.entries(machine.config.states)
      .filter(([, node]) => node.meta).map(([key, node]) => [node.meta.state, key]));
    for (const item of cases) {
      const snapshot = machine.resolveState({ value: nodes.get(item.state), context: { decision: null } });
      const actor = createActor(machine, { snapshot }).start();
      actor.send({ type: 'command', action: item.action, facts: item.facts, input: item.input });
      const result = actor.getSnapshot();
      assert.equal(stateOf(result).state, item.next);
      assert.equal(stateOf(result).terminal, document.definition.finals.includes(item.next));
      assert.equal(result.context.decision.kind, item.kind);
      assert.equal(result.context.decision.movement ?? null, item.movement);
      kinds.add(item.kind);
      actor.stop();
    }
  });
}
test('all reference outcomes exercised', () => {
  assert.deepEqual([...kinds].sort(), ['Accepted', 'Ambiguous', 'Disabled', 'Illegal', 'Indeterminate', 'UnknownAction']);
});
test('unsupported guard fails during compilation', () => {
  const document = structuredClone(fixtures[0].document);
  const edge = document.definition.edges.find(edge => edge.guard);
  edge.guard.predicate = { $expr: 'unsupported' };
  assert.throws(() => compileMachine(document), error => error.message.includes(edge.id) && error.message.includes('unsupported'));
});
