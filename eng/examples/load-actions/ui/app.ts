import { catalog, detailsLoad as details, inspectTransitionsload } from './generated/queries.generated.js';
import { serviceTransitionAvailabilityKinds as kinds } from './generated/contracts.generated.js';
import type { ServiceTransitionAction } from './generated/contracts.generated.js';
import { httpClient, HttpFailure, invokeAction } from './transport.js';

function element<T extends HTMLElement>(id: string): T {
  const node = document.getElementById(id);
  if (!node) throw new Error(`Missing element: ${id}`);
  return node as T;
}
const role = element<HTMLSelectElement>('role');
const load = element<HTMLSelectElement>('load');
const reason = element<HTMLTextAreaElement>('reason');
const check = element<HTMLButtonElement>('check');
const submit = element<HTMLButtonElement>('submit');
const refresh = element<HTMLButtonElement>('refresh');
const form = element<HTMLFormElement>('action-form');
let roleHeader = '';
let actions: ServiceTransitionAction[] = [];
let selected: string | undefined;
let checked: ServiceTransitionAction | undefined;
let revision = 0;
let busy = false;
let committing = false;
const client = () => httpClient(roleHeader, role.value);
const title = (id: string) => id.charAt(0).toUpperCase() + id.slice(1);

function message(text = '', error = false) {
  const node = element('message');
  node.textContent = text;
  node.hidden = !text;
  node.classList.toggle('error', error);
}
function invalidate() {
  revision++;
  checked = undefined;
  busy = false;
}
function render() {
  role.disabled = load.disabled = refresh.disabled = busy;
  reason.disabled = committing;
  check.disabled = busy || !selected;
  submit.disabled = busy || checked?.Kind !== kinds.permitted;
  form.hidden = !selected;
  element('empty').hidden = actions.length !== 0 || busy;
  element('action-count').textContent = String(actions.length);
  const list = element('actions');
  list.replaceChildren(...actions.map(action => {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'action-choice';
    button.setAttribute('aria-pressed', String(selected === action.OperationId));
    button.textContent = `${title(action.OperationId)} load`;
    button.disabled = busy;
    button.onclick = () => {
      invalidate(); selected = action.OperationId; message(); render(); reason.focus();
    };
    return button;
  }));
  submit.textContent = selected ? `${title(selected)} load` : 'Submit action';
  const availability = element('availability');
  availability.textContent = busy ? 'Checking the latest load…' : checked?.Kind === kinds.permitted
    ? 'This action is permitted with the current input.' : checked
      ? 'This action is unavailable for the current load and input.'
      : 'Check this action with your proposed input.';
}

async function reload(notice = '', isError = false) {
  invalidate();
  const ticket = revision;
  actions = []; selected = undefined; busy = true;
  message(notice, isError); render();
  try {
    const http = client();
    const [record, available] = await Promise.all([details(http, load.value), inspectTransitionsload(http, load.value, {})]);
    if (ticket !== revision) return;
    element('reference').textContent = record.Id;
    element('status').textContent = record.Status;
    element('documents').textContent = record.DocumentsComplete ? 'Complete' : 'Missing documents';
    element('last-reason').textContent = record.LastReason || 'No actions recorded';
    actions = available;
  } catch {
    if (ticket === revision) message('Could not refresh this load. Try again.', true);
  } finally {
    if (ticket === revision) { busy = false; render(); }
  }
}

check.onclick = async () => {
  if (!selected || busy) return;
  invalidate();
  const ticket = revision;
  busy = true; message(); render();
  try {
    const available = await inspectTransitionsload(client(), load.value, { Inputs: { [selected]: { Reason: reason.value } } });
    if (ticket !== revision) return;
    actions = available;
    checked = available.find(action => action.OperationId === selected);
    if (!checked) { selected = undefined; message('This action is no longer available to this operator.', true); }
  } catch {
    if (ticket === revision) message('Could not check this action. Try again.', true);
  } finally {
    if (ticket === revision) { busy = false; render(); }
  }
};
reason.oninput = () => { invalidate(); render(); };
role.onchange = load.onchange = () => { reason.value = ''; void reload(); };
refresh.onclick = () => void reload();
form.onsubmit = async event => {
  event.preventDefault();
  if (busy || checked?.Kind !== kinds.permitted) return;
  const action = checked;
  invalidate(); committing = busy = true; render();
  let notice = 'Action completed. The load is up to date.';
  let failed = false;
  try {
    await invokeAction(client(), load.value, action, { Reason: reason.value });
    reason.value = '';
  } catch (error) {
    failed = true;
    notice = error instanceof HttpFailure && error.status === 409
      ? 'This load changed after your check. Review the latest state and check again.'
      : 'The action could not be confirmed. Review the refreshed load before trying again.';
  } finally {
    committing = false;
    await reload(notice, failed);
  }
};

async function start() {
  try {
    const choices = await catalog(httpClient('', ''));
    roleHeader = choices.RoleHeader;
    element('load-count').textContent = String(choices.Loads.length).padStart(2, '0');
    for (const value of choices.Roles) role.add(new Option(title(value), value));
    for (const value of choices.Loads) load.add(new Option(value, value));
    await reload();
  } catch { message('Could not connect to the demo. Reload the page to retry.', true); }
}
void start();
