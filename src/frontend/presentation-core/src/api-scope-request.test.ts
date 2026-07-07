import { describe, expect, it, vi } from 'vitest'

import {
  createScopedApiHttpClient,
  projectApiScopeRequest,
  type ApiScopePolicyMetadata,
} from './api-scope-request'

const singleTenantHeaderPolicy: ApiScopePolicyMetadata = {
  access: 'requireSelected',
  allowDefaultScope: true,
  binding: 'header',
  cardinality: 'single',
  kind: 'sample.tenant',
  multipleScopesParameterName: 'X-Sample-Tenant-Ids',
  scopeModeParameterName: 'X-Sample-Tenant-Scope',
  singleScopeParameterName: 'X-Sample-Tenant-Id',
}

const multiTenantQueryPolicy: ApiScopePolicyMetadata = {
  access: 'filterToAccessible',
  allowDefaultScope: true,
  binding: 'query',
  cardinality: 'multiple',
  kind: 'sample.tenant',
  multipleScopesParameterName: 'tenant_ids',
}

const resourceTenantPolicy: ApiScopePolicyMetadata = {
  access: 'validateAccessible',
  allowDefaultScope: false,
  binding: 'resource',
  cardinality: 'single',
  kind: 'sample.tenant',
  resourceDerivation: {
    format: 'scopedProcessInstanceId',
    scopeField: 'scopeId',
    strategy: 'structuredResourceId',
  },
  resourceParameterName: 'processId',
}

describe('projectApiScopeRequest', () => {
  it('adds the selected tenant header for single-scope header operations', () => {
    const projected = projectApiScopeRequest(
      '/shape_graphs',
      { method: 'GET' },
      { mode: 'single', scopeId: 'tenant-a' },
      [singleTenantHeaderPolicy],
    )

    expect(projected.path).toBe('/shape_graphs')
    expect(new Headers(projected.init.headers).get('X-Sample-Tenant-Id')).toBe('tenant-a')
  })

  it('projects a selected tenant into the multi-scope query parameter when absent', () => {
    const projected = projectApiScopeRequest(
      '/processes?limit=5',
      { method: 'GET' },
      { mode: 'single', scopeId: 'tenant-a' },
      [multiTenantQueryPolicy],
    )

    expect(projected.path).toBe('/processes?limit=5&tenant_ids=tenant-a')
  })

  it('preserves explicit multi-scope query filters', () => {
    const projected = projectApiScopeRequest(
      '/processes?tenant_ids=tenant-b&limit=5',
      { method: 'GET' },
      { mode: 'single', scopeId: 'tenant-a' },
      [multiTenantQueryPolicy],
    )

    expect(projected.path).toBe('/processes?tenant_ids=tenant-b&limit=5')
  })

  it('does not invent transport bindings for resource-scoped operations', () => {
    const projected = projectApiScopeRequest(
      '/processes/process-123',
      { method: 'GET' },
      { mode: 'single', scopeId: 'tenant-a' },
      [resourceTenantPolicy],
    )

    expect(projected.path).toBe('/processes/process-123')
    expect(projected.init.headers).toBeUndefined()
  })
})

describe('createScopedApiHttpClient', () => {
  it('applies scope metadata at request time', async () => {
    const http = vi.fn(async () => ({ ok: true }))
    const scopedHttp = createScopedApiHttpClient(http, {
      getSelection: () => ({ mode: 'single', scopeId: 'tenant-a' }),
      policies: [singleTenantHeaderPolicy, multiTenantQueryPolicy],
    })

    await scopedHttp('/processes?limit=1', { method: 'GET' })

    expect(http).toHaveBeenCalledTimes(1)
    expect(http.mock.calls[0]?.[0]).toBe('/processes?limit=1&tenant_ids=tenant-a')
    expect(new Headers(http.mock.calls[0]?.[1].headers).get('X-Sample-Tenant-Id')).toBe('tenant-a')
  })
})
