import * as commands from './generated/commands.generated.js';
import type { ApiHttpClient } from './generated/queries.generated.js';
import type { Command, ServiceTransitionAction } from './generated/contracts.generated.js';

export class HttpFailure extends Error {
  constructor(readonly status: number) { super(`Request failed (${status}).`); }
}

/** Captures the selected demo identity for the entire request, including concurrent refreshes. */
export function httpClient(roleHeader: string, role: string, baseUrl = ''): ApiHttpClient {
  return async (path, init) => {
    const headers = new Headers(init.headers);
    if (roleHeader) headers.set(roleHeader, role);
    const response = await fetch(baseUrl + path, { ...init, headers });
    if (!response.ok) throw new HttpFailure(response.status);
    return response.json();
  };
}

/** Join discovery to generated invocation functions by their shared backend API identity. */
export function invokeAction(http: ApiHttpClient, loadId: string, action: ServiceTransitionAction, input: Command) {
  const key = (Object.keys(commands.apiOperationIds) as commands.ApiOperationKey[])
    .find(key => commands.apiOperationIds[key] === action.ApiOperationId);
  if (!key) throw new Error('This action is not supported by the generated client.');
  return commands[key](http, loadId, action.ConcurrencyToken, input);
}
