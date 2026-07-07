import {
  pageHostComponentRoles,
  presentationBindingKinds,
  type NavigationRouteDefinition,
  type PageHostDefinition,
  type PresentationBindingDefinition,
  type ViewDefinition,
  type ViewKind,
  viewKindLabels,
  viewKinds,
} from '@cohesivesystems/presentation-contracts'
import { getPresentationViewSemanticRole } from './presentation-semantics'
import {
  createPresentationEnumDiscriminator,
  resolvePresentationComponentBinding,
  type PresentationEnumDiscriminator,
} from './target-bindings'

/**
 * Minimal presentation module shape needed to resolve page-host renderer
 * defaults and target bindings. Apps can pass richer generated module types as
 * long as they expose these structural members.
 */
export interface PresentationPageHostRendererModuleProjection {
  /** Target adapter bindings, including optional PageHostComponent bindings. */
  readonly Targets?: readonly {
    readonly Bindings: readonly PresentationBindingDefinition[]
    readonly ComponentSet?: string | null
    readonly Target: string | number
  }[]

  /** Presentation views used to infer semantic roles and view-kind defaults. */
  readonly Views?: readonly ViewDefinition[]

  /** Workspaces used to distinguish generic workspaces from document workspaces. */
  readonly Workspaces?: readonly {
    readonly DocumentProfiles?: readonly unknown[]
    readonly Id: string
  }[]
}

export const presentationPageHostComponentRoles = pageHostComponentRoles

/**
 * Ordered registry of page-host renderer overrides and defaults. Resolution
 * prefers semantic page-host shape first: workspace defaults, root-view semantic
 * roles, view kinds, and target-declared component roles. Id and component-key
 * bindings remain available as explicit escape hatches for hosts not yet
 * modeled by first-class IR semantics.
 */
export interface PageHostRendererRegistry<TRenderer = unknown> {
  /** Renderers keyed by concrete adapter component keys. */
  readonly byComponentKey?: Readonly<Record<string, TRenderer>>

  /** Renderers keyed by semantic adapter component roles. */
  readonly byComponentRole?: Readonly<Record<string, TRenderer>>

  /** Exact page-host id overrides. These win over route and workspace matches. */
  readonly byPageHostId?: Readonly<Record<string, TRenderer>>

  /** Renderers keyed by semantic role, such as `surface-root`. */
  readonly bySemanticRole?: Readonly<Record<string, TRenderer>>

  /** Exact route id overrides. Useful when multiple routes share a page host. */
  readonly byRouteId?: Readonly<Record<string, TRenderer>>

  /** Exact root-view id overrides for page hosts that mount a view. */
  readonly byViewId?: Readonly<Record<string, TRenderer>>

  /** Renderers keyed by generated `ViewKind` value or compatible string form. */
  readonly byViewKind?: Readonly<Record<string, TRenderer>>

  /** Exact workspace id overrides for workspace-backed page hosts. */
  readonly byWorkspaceId?: Readonly<Record<string, TRenderer>>

  /** Last-resort renderer when no explicit or inferred renderer matches. */
  readonly fallback?: TRenderer
}

/**
 * Convenience registry type for apps that use the standard generated
 * navigation, route, and page-host shapes.
 */
export type SimplePageHostRendererRegistry<TRenderer = unknown> =
  PageHostRendererRegistry<TRenderer>

/**
 * Describes which resolution branch selected a page-host renderer. These
 * values are intentionally stable because projection diagnostics display them.
 */
export type PageHostRendererResolutionSource =
  | 'component-key'
  | 'component-role'
  | 'fallback'
  | 'page-host-id'
  | 'route-id'
  | 'semantic-role'
  | 'view-id'
  | 'view-kind'
  | 'workspace-id'

/**
 * Result of resolving a page-host renderer. The resolution includes both the
 * selected renderer and the semantic facts used for diagnostics and tracing.
 */
export interface PageHostRendererResolution<TRenderer = unknown> {
  /** Effective component key after caller and target bindings are considered. */
  readonly componentKey: string | null

  /** Effective component role after caller and target bindings are considered. */
  readonly componentRole: string | null

  /** Renderer selected from the registry, or null when no renderer matched. */
  readonly renderer: TRenderer | null

  /** Registry branch or target binding source that selected the renderer. */
  readonly resolutionSource: PageHostRendererResolutionSource | null

  /**
   * Legacy renderer key declared by a target binding, when present. Page-host
   * dispatch no longer consumes renderer keys; this value is retained only so
   * diagnostics can point at unprojected legacy IR.
   */
  readonly rendererKey: string | null

  /** Semantic role inferred from the root view, when available. */
  readonly semanticRole: string | null

  /** Target-binding source that supplied a role, key, or renderer key, when any. */
  readonly targetBindingSource: PageHostTargetBindingSource | null

  /** Root view mounted by the page host, when the page host references one. */
  readonly view: ViewDefinition | null
}

/**
 * Resolves a page-host renderer without rendering it.
 *
 * Resolution order is:
 * semantic role, component role, view kind, component key, explicit id
 * overrides, fallback. Id and component-key branches are treated as escape
 * hatches by diagnostics.
 */
export function resolvePageHostRenderer<
  TRenderer,
  TModule extends PresentationPageHostRendererModuleProjection =
    PresentationPageHostRendererModuleProjection,
  TRoute extends NavigationRouteDefinition = NavigationRouteDefinition,
  TPageHost extends PageHostDefinition = PageHostDefinition,
>({
  componentKey,
  componentRole,
  componentSet,
  module,
  pageHost,
  registry,
  route,
  targetKind,
}: {
  readonly componentKey?: string | null
  readonly componentRole?: string | null
  readonly componentSet?: string | null
  readonly module: TModule
  readonly pageHost: TPageHost | null
  readonly registry: PageHostRendererRegistry<TRenderer>
  readonly route?: TRoute | null
  readonly targetKind?: PresentationEnumDiscriminator | null
}): PageHostRendererResolution<TRenderer> {
  const resolvedComponentKey = componentKey ?? null
  const resolvedComponentRole = componentRole ?? null
  const view = findPageHostView(module, pageHost)
  const semanticRole = view ? getPresentationViewSemanticRole(view) : null
  const targetBinding = resolvePageHostTargetRendererBinding({
    componentSet,
    module,
    pageHost,
    route,
    targetKind,
    view,
  })
  const targetRendererKey = targetBinding?.rendererKey ?? null
  const targetComponentKey = targetBinding?.componentKey ?? null
  const targetComponentRole = targetBinding?.componentRole ?? null
  const defaultComponentRole = resolveDefaultPresentationPageHostComponentRole({
    module,
    pageHost,
    view,
  })
  const effectiveComponentKey = resolvedComponentKey ?? targetComponentKey
  const effectiveComponentRole =
    resolvedComponentRole ?? targetComponentRole ?? defaultComponentRole
  const pageHostId = pageHost?.Id
  const routeId = route?.Id
  const workspaceId = pageHost?.Workspace?.WorkspaceId
  const viewId = pageHost?.View?.ViewId

  if (semanticRole) {
    const renderer = registry.bySemanticRole?.[semanticRole]
    if (renderer !== undefined && renderer !== null) {
      return createResolution<TRenderer>({
        componentKey: effectiveComponentKey,
        componentRole: effectiveComponentRole,
        renderer,
        rendererKey: targetRendererKey,
        resolutionSource: 'semantic-role',
        semanticRole,
        targetBindingSource: targetBinding?.source ?? null,
        view,
      })
    }
  }

  if (effectiveComponentRole) {
    const renderer = registry.byComponentRole?.[effectiveComponentRole]
    if (renderer !== undefined && renderer !== null) {
      return createResolution<TRenderer>({
        componentKey: effectiveComponentKey,
        componentRole: effectiveComponentRole,
        renderer,
        rendererKey: targetRendererKey,
        resolutionSource: 'component-role',
        semanticRole,
        targetBindingSource: targetBinding?.source ?? null,
        view,
      })
    }
  }

  if (view) {
    const renderer = findPageHostViewKindRenderer(registry, view)
    if (renderer !== undefined && renderer !== null) {
      return createResolution<TRenderer>({
        componentKey: effectiveComponentKey,
        componentRole: effectiveComponentRole,
        renderer,
        rendererKey: targetRendererKey,
        resolutionSource: 'view-kind',
        semanticRole,
        targetBindingSource: targetBinding?.source ?? null,
        view,
      })
    }
  }

  if (effectiveComponentKey) {
    const renderer = registry.byComponentKey?.[effectiveComponentKey]
    if (renderer !== undefined && renderer !== null) {
      return createResolution<TRenderer>({
        componentKey: effectiveComponentKey,
        componentRole: effectiveComponentRole,
        renderer,
        rendererKey: targetRendererKey,
        resolutionSource: 'component-key',
        semanticRole,
        targetBindingSource: targetBinding?.source ?? null,
        view,
      })
    }
  }

  if (pageHostId) {
    const renderer = registry.byPageHostId?.[pageHostId]
    if (renderer !== undefined && renderer !== null) {
      return createResolution<TRenderer>({
        componentKey: effectiveComponentKey,
        componentRole: effectiveComponentRole,
        renderer,
        rendererKey: targetRendererKey,
        resolutionSource: 'page-host-id',
        semanticRole,
        targetBindingSource: targetBinding?.source ?? null,
        view,
      })
    }
  }

  if (routeId) {
    const renderer = registry.byRouteId?.[routeId]
    if (renderer !== undefined && renderer !== null) {
      return createResolution<TRenderer>({
        componentKey: effectiveComponentKey,
        componentRole: effectiveComponentRole,
        renderer,
        rendererKey: targetRendererKey,
        resolutionSource: 'route-id',
        semanticRole,
        targetBindingSource: targetBinding?.source ?? null,
        view,
      })
    }
  }

  if (workspaceId) {
    const renderer = registry.byWorkspaceId?.[workspaceId]
    if (renderer !== undefined && renderer !== null) {
      return createResolution<TRenderer>({
        componentKey: effectiveComponentKey,
        componentRole: effectiveComponentRole,
        renderer,
        rendererKey: targetRendererKey,
        resolutionSource: 'workspace-id',
        semanticRole,
        targetBindingSource: targetBinding?.source ?? null,
        view,
      })
    }
  }

  if (viewId) {
    const renderer = registry.byViewId?.[viewId]
    if (renderer !== undefined && renderer !== null) {
      return createResolution<TRenderer>({
        componentKey: effectiveComponentKey,
        componentRole: effectiveComponentRole,
        renderer,
        rendererKey: targetRendererKey,
        resolutionSource: 'view-id',
        semanticRole,
        targetBindingSource: targetBinding?.source ?? null,
        view,
      })
    }
  }

  if (registry.fallback !== undefined && registry.fallback !== null) {
    return createResolution<TRenderer>({
      componentKey: effectiveComponentKey,
      componentRole: effectiveComponentRole,
      renderer: registry.fallback,
      rendererKey: targetRendererKey,
      resolutionSource: 'fallback',
      semanticRole,
      targetBindingSource: targetBinding?.source ?? null,
      view,
    })
  }

  return createResolution<TRenderer>({
    componentKey: effectiveComponentKey,
    componentRole: effectiveComponentRole,
    renderer: null,
    rendererKey: targetRendererKey,
    resolutionSource: null,
    semanticRole,
    targetBindingSource: targetBinding?.source ?? null,
    view,
  })
}

/**
 * Infers the standard component role for a page host from its workspace and
 * root view semantics. Target bindings can override this when a host needs a
 * more specific frontend interpretation.
 */
export function resolveDefaultPresentationPageHostComponentRole({
  module,
  pageHost,
  view,
}: {
  readonly module: PresentationPageHostRendererModuleProjection
  readonly pageHost: PageHostDefinition | null
  readonly view: ViewDefinition | null
}) {
  const workspaceRef = pageHost?.Workspace
  if (workspaceRef) {
    const workspace = module.Workspaces?.find(
      (candidate) => candidate.Id === workspaceRef.WorkspaceId,
    )
    return workspace?.DocumentProfiles && workspace.DocumentProfiles.length > 0
      ? presentationPageHostComponentRoles.documentWorkspace
      : presentationPageHostComponentRoles.routedSurface
  }

  if (!view) {
    return null
  }

  const semanticRole = getPresentationViewSemanticRole(view)
  if (semanticRole === 'surface-root' || semanticRole === 'surface-section') {
    return presentationPageHostComponentRoles.routedSurface
  }

  return viewKindComponentRoles[String(view.Kind)] ?? null
}

export type PageHostTargetBindingSource =
  | 'target-page-host-binding'
  | 'target-route-binding'
  | 'target-view-binding'
  | 'target-workspace-binding'

interface PageHostTargetRendererBindingResolution {
  readonly componentKey: string | null
  readonly componentRole: string | null
  readonly rendererKey: string | null
  readonly source: PageHostTargetBindingSource
}

function findPageHostView<
  TModule extends PresentationPageHostRendererModuleProjection,
>(
  module: TModule,
  pageHost: PageHostDefinition | null,
) {
  const viewId = pageHost?.View?.ViewId
  if (!viewId) {
    return null
  }

  return module.Views?.find((view) => view.Id === viewId) ?? null
}

function findPageHostViewKindRenderer<TRenderer>(
  registry: PageHostRendererRegistry<TRenderer>,
  view: ViewDefinition,
) {
  const keys = new Set<string>([String(view.Kind)])
  const label = viewKindLabels[view.Kind as ViewKind]
  if (label) {
    keys.add(label)
    keys.add(label.charAt(0).toLowerCase() + label.slice(1))
  }

  for (const key of keys) {
    const renderer = registry.byViewKind?.[key]
    if (renderer !== undefined && renderer !== null) {
      return renderer
    }
  }

  return undefined
}

function resolvePageHostTargetRendererBinding<
  TModule extends PresentationPageHostRendererModuleProjection,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
>({
  componentSet,
  module,
  pageHost,
  route,
  targetKind,
  view,
}: {
  readonly componentSet?: string | null
  readonly module: TModule
  readonly pageHost: TPageHost | null
  readonly route?: TRoute | null
  readonly targetKind?: PresentationEnumDiscriminator | null
  readonly view: ViewDefinition | null
}): PageHostTargetRendererBindingResolution | null {
  if (!module.Targets) {
    return null
  }
  const targetBindingModule = { Targets: module.Targets }

  const candidates = [
    pageHost ? { id: pageHost.Id, source: 'target-page-host-binding' as const } : null,
    route ? { id: route.Id, source: 'target-route-binding' as const } : null,
    pageHost?.Workspace?.WorkspaceId
      ? {
          id: pageHost.Workspace.WorkspaceId,
          source: 'target-workspace-binding' as const,
        }
      : null,
    view ? { id: view.Id, source: 'target-view-binding' as const } : null,
  ].filter((candidate): candidate is {
    readonly id: string
    readonly source: PageHostTargetBindingSource
  } => candidate !== null)

  for (const candidate of candidates) {
    const resolvedBinding = resolvePresentationComponentBinding(targetBindingModule, {
      bindingKind: createPresentationEnumDiscriminator(
        presentationBindingKinds,
        'pageHostComponent',
        'PageHostComponent',
      ),
      componentSet,
      id: candidate.id,
      routeId: route?.Id,
      targetKind,
    })
    const binding = resolvedBinding.binding

    if (!binding) {
      continue
    }

    const rendererKey = readPageHostRendererKey(binding)
    if (resolvedBinding.componentRole || resolvedBinding.componentKey || rendererKey) {
      return {
        componentKey: resolvedBinding.componentKey,
        componentRole: resolvedBinding.componentRole,
        rendererKey,
        source: candidate.source,
      }
    }
  }

  return null
}

function createResolution<TRenderer>(
  resolution: PageHostRendererResolution<TRenderer>,
): PageHostRendererResolution<TRenderer> {
  return resolution
}

const viewKindComponentRoles = createViewKindComponentRoles()

function createViewKindComponentRoles() {
  const keys: Record<string, string> = {}
  addViewKindComponentRole(
    keys,
    viewKinds.collection,
    'Collection',
    presentationPageHostComponentRoles.routedSurface,
  )
  addViewKindComponentRole(
    keys,
    viewKinds.dashboard,
    'Dashboard',
    presentationPageHostComponentRoles.routedSurface,
  )
  addViewKindComponentRole(
    keys,
    viewKinds.documentWorkspace,
    'DocumentWorkspace',
    presentationPageHostComponentRoles.documentWorkspace,
  )
  addViewKindComponentRole(
    keys,
    viewKinds.form,
    'Form',
    presentationPageHostComponentRoles.routedSurface,
  )
  addViewKindComponentRole(
    keys,
    viewKinds.graph,
    'Graph',
    presentationPageHostComponentRoles.routedSurface,
  )
  addViewKindComponentRole(
    keys,
    viewKinds.page,
    'Page',
    presentationPageHostComponentRoles.routedSurface,
  )
  addViewKindComponentRole(
    keys,
    viewKinds.prompt,
    'Prompt',
    presentationPageHostComponentRoles.routedSurface,
  )
  addViewKindComponentRole(
    keys,
    viewKinds.recordDetail,
    'RecordDetail',
    presentationPageHostComponentRoles.routedSurface,
  )
  addViewKindComponentRole(
    keys,
    viewKinds.search,
    'Search',
    presentationPageHostComponentRoles.routedSurface,
  )
  addViewKindComponentRole(
    keys,
    viewKinds.surface,
    'Surface',
    presentationPageHostComponentRoles.routedSurface,
  )
  addViewKindComponentRole(
    keys,
    viewKinds.tabbedSurface,
    'TabbedSurface',
    presentationPageHostComponentRoles.routedSurface,
  )
  addViewKindComponentRole(
    keys,
    viewKinds.timeline,
    'Timeline',
    presentationPageHostComponentRoles.routedSurface,
  )
  return keys
}

function addViewKindComponentRole(
  keys: Record<string, string>,
  numericValue: ViewKind,
  label: string,
  componentRole: string,
) {
  keys[String(numericValue)] = componentRole
  keys[label] = componentRole
  keys[label.charAt(0).toLowerCase() + label.slice(1)] = componentRole
}

function readPageHostRendererKey(binding: PresentationBindingDefinition) {
  const options = binding.Options
  if (!isRecord(options)) {
    return null
  }

  const rendererKey = options.rendererKey ?? options.RendererKey
  return typeof rendererKey === 'string' && rendererKey.length > 0
    ? rendererKey
    : null
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}
