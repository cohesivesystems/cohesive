import { describe, expect, it } from 'vitest'

import {
  createEndpointExecutor,
  readRequiredEndpointBody,
  readRequiredEndpointRouteParameter,
  type EndpointExecutorRegistry,
} from './endpoint-executor'

describe('createEndpointExecutor', () => {
  it('dispatches endpoint ids through the registered executor', async () => {
    const registry = {
      'endpoint:echo': async (request) => ({
        body: request.body,
        query: request.query,
        routeId: request.routeParameters?.id,
      }),
    } satisfies EndpointExecutorRegistry

    const executeEndpoint = createEndpointExecutor(registry)

    await expect(
      executeEndpoint('endpoint:echo', {
        body: { name: 'shape' },
        query: { status: 'open' },
        routeParameters: { id: 'resource-1' },
      }),
    ).resolves.toEqual({
      body: { name: 'shape' },
      query: { status: 'open' },
      routeId: 'resource-1',
    })
  })

  it('reports missing endpoint bindings with the endpoint family label', async () => {
    const executeEndpoint = createEndpointExecutor({}, { label: 'Training API' })

    await expect(executeEndpoint('endpoint:missing')).rejects.toThrow(
      "No Training API endpoint executor is registered for endpoint 'endpoint:missing'.",
    )
  })
})

describe('readRequiredEndpointRouteParameter', () => {
  it('returns a present route parameter', () => {
    expect(
      readRequiredEndpointRouteParameter(
        'endpoint:get-resource',
        { routeParameters: { id: 'resource-1' } },
        'id',
      ),
    ).toBe('resource-1')
  })

  it('rejects empty route parameters', () => {
    expect(() =>
      readRequiredEndpointRouteParameter(
        'endpoint:get-resource',
        { routeParameters: { id: '' } },
        'id',
      ),
    ).toThrow("Endpoint 'endpoint:get-resource' requires route parameter 'id'.")
  })
})

describe('readRequiredEndpointBody', () => {
  it('returns a present body', () => {
    expect(
      readRequiredEndpointBody<{ readonly name: string }>(
        'endpoint:create-resource',
        { body: { name: 'shape' } },
      ),
    ).toEqual({ name: 'shape' })
  })

  it('rejects missing bodies', () => {
    expect(() =>
      readRequiredEndpointBody('endpoint:create-resource', {}),
    ).toThrow("Endpoint 'endpoint:create-resource' requires a request body.")
  })
})
