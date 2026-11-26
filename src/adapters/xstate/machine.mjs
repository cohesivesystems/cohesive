import { assign, createMachine } from 'xstate';

function unsupported(subject, reason) {
  throw new RangeError(`machines.xstate.unsupported: ${subject}: ${reason}`);
}

function compileGuard(edge, types) {
  const guard = edge.guard;
  if (!guard) return () => true;

  function requiredSingle(contract) {
    if (contract.cardinality !== 'Single' || contract.presence !== 'Required'
      || contract.nullability !== 'NonNullable' || contract.constraints?.length) {
      unsupported(edge.id, 'expected a single required, non-null value without constraints');
    }
  }

  function fields(contract) {
    requiredSingle(contract);
    const type = types[contract.type];
    if (contract.shape || type?.$type !== 'object') unsupported(edge.id, 'expected an inline record contract');
    return new Map(type.fields.map(field => {
      requiredSingle(field);
      const scalar = types[field.type];
      if (scalar?.$type !== 'scalar' || scalar.format !== 'None'
        || !['Bool', 'String'].includes(scalar.kind)) unsupported(edge.id, field.name);
      return [field.name, scalar.kind === 'Bool' ? 'boolean' : 'string'];
    }));
  }

  const facts = fields(guard.factsContract);
  const input = fields(guard.inputContract);
  function predicate(expr) {
    if (expr.$expr === 'field' && expr.path.segments.length === 1) {
      const [field] = expr.path.segments;
      const source = expr.binding ?? guard.factsBinding;
      const fromFacts = source === guard.factsBinding;
      if ((fromFacts || source === guard.inputBinding) && field.kind === 'Field'
        && (fromFacts ? facts : input).get(field.segment) === 'boolean') {
        return event => (fromFacts ? event.facts : event.input)[field.segment];
      }
    }
    if (expr.$expr === 'unary' && expr.operator === 'Not') {
      const operand = predicate(expr.operand);
      return event => !operand(event);
    }
    if (expr.$expr === 'binary' && ['And', 'Or'].includes(expr.operator)) {
      const left = predicate(expr.left);
      const right = predicate(expr.right);
      return expr.operator === 'And' ? event => left(event) && right(event) : event => left(event) || right(event);
    }
    unsupported(edge.id, `predicate ${expr.$expr} ${expr.operator ?? ''}`);
  }
  const evaluate = predicate(guard.predicate);
  function matches(value, fields) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
    for (const [name, type] of fields) {
      if (!Object.hasOwn(value, name) || typeof value[name] !== type) return false;
    }
    return true;
  }
  return event => matches(event.facts, facts) && matches(event.input, input) ? evaluate(event) : undefined;
}

/** Compile a trusted, canonical machine/v1 document (produced or verified by Cohesive).
 * Send { type: 'command', action, facts, input }. Inspect snapshot.context.decision after delivery.
 * Stable snapshot values are generated node keys; state metadata retains the domain identity.
 */
export function compileMachine(document) {
  if (document.kind !== 'machine/v1' || document.metadata.schemaVersion !== 'cohesive-execution/v4'
    || document.extensions.length) unsupported(document.kind, 'expected an extension-free v4 machine document');
  const model = document.definition;
  const nodes = new Map(model.states.map((state, i) => [state, { key: 's' + i, actions: new Map() }]));
  const finals = new Set(model.finals);
  const protocol = new Set();
  for (const edge of model.edges) {
    const actions = nodes.get(edge.from).actions;
    if (!actions.has(edge.action)) actions.set(edge.action, []);
    actions.get(edge.action).push({ edge, enabled: compileGuard(edge, model.$types) });
    protocol.add(edge.action);
  }

  function decide(actions, event) {
    const candidates = actions.get(event.action);
    if (!candidates) return { kind: protocol.has(event.action) ? 'Illegal' : 'UnknownAction' };
    let selected;
    for (const { edge, enabled } of candidates) {
      const admitted = enabled(event);
      if (admitted === undefined) return { kind: 'Indeterminate' };
      if (!admitted) continue;
      if (selected) return { kind: 'Ambiguous' };
      selected = edge;
    }
    return selected ? { kind: 'Accepted', movement: selected.id } : { kind: 'Disabled' };
  }

  const states = {};
  for (const [state, { key, actions }] of nodes) {
    const choice = 'choose_' + key;
    states[key] = {
      meta: { state, terminal: finals.has(state) },
      on: { command: { target: choice, actions: assign({ decision: ({ event }) => decide(actions, event) }) } },
    };
    const branches = [];
    for (const candidates of actions.values()) {
      for (const { edge } of candidates) {
        branches.push({ guard: ({ context }) => context.decision.movement === edge.id, target: nodes.get(edge.to).key });
      }
    }
    states[choice] = { always: [...branches, { target: key }] };
  }
  return createMachine({
    id: 'cohesive', initial: nodes.get(model.initial).key, context: { decision: null }, states,
    meta: { definition: document.metadata.definitionId, revision: document.metadata.revisionId,
      fingerprint: document.metadata.fingerprint.value },
  });
}
