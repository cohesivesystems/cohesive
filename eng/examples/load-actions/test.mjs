import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import * as queries from './wwwroot/js/generated/queries.generated.js';
import { serviceTransitionAvailabilityKinds as kinds } from './wwwroot/js/generated/contracts.generated.js';
import { httpClient, HttpFailure, invokeAction } from './wwwroot/js/transport.js';

// Exercise the actual HTTP host and generated client. Each run gets an isolated in-memory store.
test('logistics actions use authorized discovery, machine guards and optimistic concurrency', { timeout: 30000 }, async t => {
  const server = spawn('dotnet', [fileURLToPath(new URL('./bin/Debug/net10.0/LoadActions.dll', import.meta.url)),
    '--serve', '--urls', 'http://127.0.0.1:0'], {
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe']
  });
  t.after(async () => { if (server.exitCode === null && server.signalCode === null) { server.kill('SIGTERM'); await once(server, 'exit'); } });
  let output = '';
  const base = await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error(`Host did not start: ${output}`)), 15000);
    const read = chunk => {
      output += chunk;
      const match = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/);
      if (match) { clearTimeout(timeout); resolve(match[1]); }
    };
    server.stdout.on('data', read); server.stderr.on('data', read);
    server.once('error', error => { clearTimeout(timeout); reject(error); });
    server.once('exit', code => { clearTimeout(timeout); reject(new Error(`Host exited ${code}: ${output}`)); });
  });
  const catalog = await queries.catalog(httpClient('', '', base));
  const client = role => httpClient(catalog.RoleHeader, role, base);
  const [ready, missing] = catalog.Loads;
  const dispatcher = client('dispatcher');
  const supervisor = client('supervisor');
  const viewer = client('viewer');
  const inspect = (http, id, Inputs) => queries.inspectTransitionsload(http, id, { Inputs });
  const command = { Reason: 'Customer requested this change' };
  assert.deepEqual(await inspect(viewer, ready), []);
  const pending = await inspect(dispatcher, ready);
  assert.equal(pending.length, 1);
  assert.equal(pending[0].Kind, kinds.requiresInput);
  const releaseId = pending[0].OperationId;
  const full = { [releaseId]: command };
  const [release] = await inspect(dispatcher, ready, full);
  assert.equal(release.Kind, kinds.permitted);
  const [blocked] = await inspect(dispatcher, missing, full);
  assert.equal(blocked.Kind, kinds.unavailable);
  assert.equal((await queries.details(dispatcher, ready)).LastReason, '');
  await assert.rejects(invokeAction(viewer, ready, release, command), error => error instanceof HttpFailure && error.status === 403);
  await assert.rejects(inspect(dispatcher, ready, { unknown: command }), error => error instanceof HttpFailure && error.status === 400);
  const supervisorPending = await inspect(supervisor, ready);
  assert.equal(supervisorPending.length, 2);
  const supervisorInputs = Object.fromEntries(supervisorPending.map(action => [action.OperationId, command]));
  const permitted = await inspect(supervisor, ready, supervisorInputs);
  const cancel = permitted.find(action => action.OperationId !== releaseId);
  assert.equal(cancel.Kind, kinds.permitted);
  assert.equal(cancel.ConcurrencyToken, release.ConcurrencyToken);
  await invokeAction(dispatcher, ready, release, command);
  await assert.rejects(invokeAction(supervisor, ready, cancel, command), error => error instanceof HttpFailure && error.status === 409);
  const record = await queries.details(supervisor, ready);
  assert.equal(record.Status, 'Released');
  assert.equal(record.LastReason, command.Reason);
  assert.ok((await inspect(supervisor, ready, supervisorInputs)).every(action => action.Kind === kinds.unavailable));
  // A separate load still supports a successful action through the same generated dispatch.
  const otherActions = await inspect(supervisor, missing, supervisorInputs);
  const otherCancel = otherActions.find(action => action.OperationId === cancel.OperationId);
  await invokeAction(supervisor, missing, otherCancel, command);
  assert.equal((await queries.details(supervisor, missing)).Status, 'Cancelled');
});
