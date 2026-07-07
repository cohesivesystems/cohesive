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
  createNavigationRoute,
  createPageHost,
  createWorkspacePageHost,
} from './navigation'
import {
  presentationPageHostComponentRoles,
  resolvePageHostRenderer,
  type PresentationPageHostRendererModuleProjection,
} from './page-host-renderer-registry'
import { createPresentationEnumDiscriminator } from './target-bindings'

describe('page-host renderer registry', () => {
  it('prefers semantic roles before inferred component roles', () => {
    const view = createView({ id: 'orders-surface', kind: viewKinds.surface })
    const pageHost = createPageHost({
      id: 'orders-host',
      viewId: view.Id,
    })
    const route = createNavigationRoute({
      id: 'orders-route',
      pageHostId: pageHost.Id,
      pathTemplate: '/orders',
    })

    const resolution = resolvePageHostRenderer({
      module: createModule({ views: [view] }),
      pageHost,
      registry: {
        byComponentRole: {
          [presentationPageHostComponentRoles.routedSurface]: 'role-renderer',
        },
        bySemanticRole: {
          'surface-section': 'semantic-renderer',
        },
      },
      route,
    })

    expect(resolution).toMatchObject({
      componentKey: null,
      componentRole: presentationPageHostComponentRoles.routedSurface,
      renderer: 'semantic-renderer',
      resolutionSource: 'semantic-role',
      semanticRole: 'surface-section',
    })
  })

  it('reads page-host component roles and legacy renderer keys from target bindings', () => {
    const view = createView({ id: 'runs-view', kind: viewKinds.collection })
    const pageHost = createPageHost({
      id: 'runs-host',
      viewId: view.Id,
    })
    const route = createNavigationRoute({
      id: 'runs-route',
      pageHostId: pageHost.Id,
      pathTemplate: '/runs',
    })

    const resolution = resolvePageHostRenderer({
      componentSet: 'sample-admin-ui',
      module: createModule({
        targets: [
          {
            Bindings: [
              createPageHostComponentBinding({
                componentRole: 'custom-page-host',
                id: pageHost.Id,
                rendererKey: 'legacy-runs-host',
              }),
            ],
            ComponentSet: 'sample-admin-ui',
            Target: presentationTargetKinds.react,
          },
        ],
        views: [view],
      }),
      pageHost,
      registry: {
        byComponentRole: {
          'custom-page-host': 'custom-renderer',
        },
      },
      route,
      targetKind: createPresentationEnumDiscriminator(
        presentationTargetKinds,
        'react',
        'React',
      ),
    })

    expect(resolution).toMatchObject({
      componentRole: 'custom-page-host',
      renderer: 'custom-renderer',
      rendererKey: 'legacy-runs-host',
      resolutionSource: 'component-role',
      targetBindingSource: 'target-page-host-binding',
    })
  })

  it('falls through to view-kind renderers when no semantic or component role matches', () => {
    const view = createView({ id: 'runs-view', kind: viewKinds.collection })
    const pageHost = createPageHost({
      id: 'runs-host',
      viewId: view.Id,
    })

    const resolution = resolvePageHostRenderer({
      module: createModule({ views: [view] }),
      pageHost,
      registry: {
        byViewKind: {
          Collection: 'collection-renderer',
        },
      },
    })

    expect(resolution).toMatchObject({
      componentRole: presentationPageHostComponentRoles.routedSurface,
      renderer: 'collection-renderer',
      resolutionSource: 'view-kind',
    })
  })

  it('infers document workspace page-host roles from workspace metadata', () => {
    const view = createView({
      id: 'workspace-view',
      kind: viewKinds.documentWorkspace,
    })
    const pageHost = createWorkspacePageHost({
      documentProfileId: 'invoice-profile',
      id: 'workspace-host',
      viewId: view.Id,
      workspaceId: 'workspace',
    })

    const resolution = resolvePageHostRenderer({
      module: createModule({
        views: [view],
        workspaces: [
          {
            DocumentProfiles: [{}],
            Id: 'workspace',
          },
        ],
      }),
      pageHost,
      registry: {
        byComponentRole: {
          [presentationPageHostComponentRoles.documentWorkspace]:
            'document-workspace-renderer',
        },
      },
    })

    expect(resolution).toMatchObject({
      componentRole: presentationPageHostComponentRoles.documentWorkspace,
      renderer: 'document-workspace-renderer',
      resolutionSource: 'component-role',
      semanticRole: 'workspace-view',
    })
  })
})

function createModule({
  targets = [],
  views = [],
  workspaces = [],
}: {
  readonly targets?: PresentationPageHostRendererModuleProjection['Targets']
  readonly views?: readonly ViewDefinition[]
  readonly workspaces?: PresentationPageHostRendererModuleProjection['Workspaces']
}): PresentationPageHostRendererModuleProjection {
  return {
    Targets: targets,
    Views: views,
    Workspaces: workspaces,
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

function createPageHostComponentBinding({
  componentRole,
  id,
  rendererKey,
}: {
  readonly componentRole: string
  readonly id: string
  readonly rendererKey: string
}): PresentationBindingDefinition {
  return {
    ComponentKey: null,
    ComponentRole: componentRole,
    Id: id,
    Kind: presentationBindingKinds.pageHostComponent,
    Options: {
      rendererKey,
    },
    RouteId: null,
  } as unknown as PresentationBindingDefinition
}
