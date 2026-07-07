import { describe, expect, it } from 'vitest'

import {
  presentationBindingKinds,
  presentationTargetKinds,
  viewKinds,
  type PresentationBindingDefinition,
  type ViewDefinition,
  type ViewKind,
} from '@cohesivesystems/presentation-contracts'
import {
  mergePresentationRendererRegistries,
  resolvePresentationViewRenderer,
  type PresentationRendererBindingModuleProjection,
} from './presentation-renderer-registry'
import { createPresentationEnumDiscriminator } from './target-bindings'

describe('presentation renderer registry', () => {
  it('prefers semantic role renderers before view-kind renderers', () => {
    const view = createView({ id: 'summary-view', kind: viewKinds.surface })

    const resolution = resolvePresentationViewRenderer({
      module: createModule(),
      registry: {
        composites: {
          bySemanticRole: {
            'surface-section': 'semantic-renderer',
          },
          byViewKind: {
            [String(viewKinds.surface)]: 'view-kind-renderer',
          },
        },
      },
      view,
    })

    expect(resolution).toMatchObject({
      componentKey: null,
      componentRole: null,
      renderer: 'semantic-renderer',
      resolutionSource: 'semantic-role',
      semanticRole: 'surface-section',
    })
  })

  it('resolves component-role renderers from view component target bindings', () => {
    const view = createView({ id: 'runs-view', kind: viewKinds.collection })

    const resolution = resolvePresentationViewRenderer({
      componentSet: 'sample-admin-ui',
      module: createModule({
        bindings: [
          createViewComponentBinding({
            componentRole: 'custom-view-role',
            id: view.Id,
          }),
        ],
      }),
      registry: {
        composites: {
          byComponentRole: {
            'custom-view-role': 'role-renderer',
          },
        },
      },
      routeId: 'runs-route',
      targetKind: createPresentationEnumDiscriminator(
        presentationTargetKinds,
        'react',
        'React',
      ),
      view,
    })

    expect(resolution).toMatchObject({
      componentKey: null,
      componentRole: 'custom-view-role',
      renderer: 'role-renderer',
      resolutionSource: 'component-role',
    })
  })

  it('falls through to component-key renderers when component roles are unmapped', () => {
    const view = createView({ id: 'details-view', kind: viewKinds.recordDetail })

    const resolution = resolvePresentationViewRenderer({
      componentSet: 'sample-admin-ui',
      module: createModule({
        bindings: [
          createViewComponentBinding({
            componentKey: 'details-card',
            componentRole: 'unmapped-role',
            id: view.Id,
          }),
        ],
      }),
      registry: {
        composites: {
          byComponentKey: {
            'details-card': 'component-renderer',
          },
        },
      },
      targetKind: createPresentationEnumDiscriminator(
        presentationTargetKinds,
        'react',
        'React',
      ),
      view,
    })

    expect(resolution).toMatchObject({
      componentKey: 'details-card',
      componentRole: 'unmapped-role',
      renderer: 'component-renderer',
      resolutionSource: 'component-key',
    })
  })

  it('merges renderer registries with later declarations overriding earlier ones', () => {
    const merged = mergePresentationRendererRegistries(
      {
        composites: {
          byComponentRole: {
            shared: 'old-role-renderer',
          },
          fallback: 'old-fallback',
        },
        controls: {
          search: 'old-control',
        },
      },
      {
        composites: {
          byComponentRole: {
            shared: 'new-role-renderer',
          },
          fallback: 'new-fallback',
        },
        controls: {
          search: 'new-control',
        },
      },
    )

    expect(merged.composites?.byComponentRole?.shared).toBe('new-role-renderer')
    expect(merged.composites?.fallback).toBe('new-fallback')
    expect(merged.controls?.search).toBe('new-control')
  })
})

function createModule({
  bindings = [],
}: {
  readonly bindings?: readonly PresentationBindingDefinition[]
} = {}): PresentationRendererBindingModuleProjection {
  return {
    Targets: [
      {
        Bindings: bindings,
        ComponentSet: 'sample-admin-ui',
        Target: presentationTargetKinds.react,
      },
    ],
  }
}

function createView({
  id,
  kind,
}: {
  readonly id: string
  readonly kind: ViewKind
}): ViewDefinition {
  return {
    Actions: [],
    Chrome: { Slots: [] },
    Collection: null,
    DataSourceIds: [],
    Design: {},
    FieldIds: [],
    Id: id,
    Kind: kind,
    Regions: [],
    Subject: {},
  } as unknown as ViewDefinition
}

function createViewComponentBinding({
  componentKey = null,
  componentRole,
  id,
}: {
  readonly componentKey?: string | null
  readonly componentRole: string
  readonly id: string
}): PresentationBindingDefinition {
  return {
    ComponentKey: componentKey,
    ComponentRole: componentRole,
    Id: id,
    Kind: presentationBindingKinds.viewComponent,
    RouteId: null,
  } as unknown as PresentationBindingDefinition
}
