import type {
  NavigationDefinition,
  NavigationHistoryDefinition,
  NavigationRouteDefinition,
  NavigationShellRegionDefinition,
  PageHostDefinition,
  PageHostKind,
} from '@cohesivesystems/presentation-contracts'
import {
  layoutNodeKinds,
  layoutOrientations,
  navigationContextKinds,
  navigationHistoryKinds,
  navigationRouteInstanceIdentityKinds,
  navigationRouteKinds,
  navigationRouteUpdateModes,
  pageHostKinds,
  pageRegionKinds,
  workspaceInstantiationModes,
} from '@cohesivesystems/presentation-contracts'

/**
 * Primitive parameter bag used when resolving semantic navigation route
 * templates into concrete hrefs.
 */
export type NavigationRouteParameters = Record<
  string,
  boolean | number | string | null | undefined
>

/**
 * Resolves a semantic route id and optional route parameters into a concrete
 * href, or `null` when the route cannot be represented for the active
 * navigation definition.
 */
export type PresentationNavigationHrefFactory = (
  routeId: string,
  parameters?: NavigationRouteParameters,
) => string | null

/**
 * Executes navigation to a concrete href that has already been resolved from a
 * presentation navigation route.
 *
 * The href may be absolute, root-relative, or application-relative depending on
 * the active host router binding.
 */
export type PresentationHrefNavigator = (href: string) => void

/**
 * Executes navigation by semantic route id using the active presentation
 * navigation definition.
 *
 * Implementations are responsible for resolving route parameters into a href
 * and ignoring navigation when the route cannot be resolved.
 */
export type PresentationRouteNavigator = (
  routeId: string,
  parameters?: NavigationRouteParameters,
) => void

/**
 * Runtime navigation services exposed to projected presentation components.
 *
 * This keeps projected components coupled to Cohesive navigation semantics
 * rather than to a concrete host router such as React Router.
 */
export interface PresentationNavigationRuntime {
  /** Resolves a semantic route id and optional parameters into a concrete href. */
  readonly createHref: PresentationNavigationHrefFactory

  /** Navigates to an already resolved href in the active host application. */
  readonly navigateHref: PresentationHrefNavigator

  /** Resolves and navigates to a semantic route in the active host application. */
  readonly navigateRoute: PresentationRouteNavigator
}

export type NavigationDefinitionProjection<
  TRoute extends NavigationRouteDefinition = NavigationRouteDefinition,
  TPageHost extends PageHostDefinition = PageHostDefinition,
> = Omit<NavigationDefinition, 'PageHosts' | 'Routes'> & {
  readonly Routes: readonly TRoute[]
  readonly PageHosts: readonly TPageHost[]
}

export function createNavigationHref<TRoute extends NavigationRouteDefinition>(
  navigation: NavigationDefinitionProjection<TRoute>,
  routeId: string,
  parameters?: NavigationRouteParameters,
) {
  const route = findNavigationRoute(navigation, routeId)
  if (!route) {
    return null
  }

  let href = route.PathTemplate
  for (const parameter of route.Parameters) {
    const token = `{${parameter.Name}}`
    if (!href.includes(token)) {
      continue
    }

    const value = parameters?.[parameter.Name]
    if (value === null || value === undefined || String(value).length === 0) {
      if (parameter.IsRequired) {
        return null
      }
      continue
    }

    href = href.replace(token, encodeURIComponent(String(value)))
  }

  return /\{[^}]+\}/.test(href) ? null : href
}

export function findNavigationRoute<
  TRoute extends NavigationRouteDefinition,
>(
  navigation: Pick<NavigationDefinitionProjection<TRoute>, 'Routes'>,
  routeId: string,
): TRoute | null {
  return navigation.Routes.find((route) => route.Id === routeId) ?? null
}

export function findNavigationPageHost<
  TPageHost extends PageHostDefinition,
>(
  navigation: Pick<
    NavigationDefinitionProjection<NavigationRouteDefinition, TPageHost>,
    'PageHosts'
  >,
  pageHostId: string,
): TPageHost | null {
  return navigation.PageHosts?.find((pageHost) => pageHost.Id === pageHostId) ?? null
}

export function createNavigationRoute({
  id,
  kind,
  label,
  pageHostId,
  parameterName,
  pathTemplate,
  parameterType = 'string',
}: {
  readonly id: string
  readonly kind?: NavigationRouteDefinition['Kind']
  readonly label?: string
  readonly pageHostId: string
  readonly parameterName?: string
  readonly parameterType?: string
  readonly pathTemplate: string
}): NavigationRouteDefinition {
  return {
    Id: id,
    Kind: kind ?? (parameterName
      ? navigationRouteKinds.entityDetail
      : navigationRouteKinds.page),
    Label: label ?? createNavigationLabel(id),
    PageHostId: pageHostId,
    Parameters: parameterName
      ? [{ IsRequired: true, Name: parameterName, Type: parameterType }]
      : [],
    PathTemplate: pathTemplate,
  }
}

export function createPageHost({
  id,
  kind = pageHostKinds.singleView,
  viewId,
}: {
  readonly id: string
  readonly kind?: PageHostKind
  readonly viewId: string
}): PageHostDefinition {
  return {
    Annotations: [],
    Id: id,
    Kind: kind,
    Layout: createSingleViewPageHostLayout(viewId),
    Regions: [
      {
        Annotations: [],
        Id: 'content',
        Kind: pageRegionKinds.content,
        Name: 'Content',
        PageHostIds: [],
        Placement: 'main',
        ProjectionIds: [],
        ViewIds: [viewId],
      },
    ],
    State: null,
    View: { Annotations: [], ViewId: viewId },
    Workspace: null,
  }
}

export function createWorkspacePageHost({
  documentProfileId,
  id,
  viewId,
  workspaceId,
}: {
  readonly documentProfileId: string
  readonly id: string
  readonly viewId: string
  readonly workspaceId: string
}): PageHostDefinition {
  return {
    ...createPageHost({ id, kind: pageHostKinds.workspace, viewId }),
    Workspace: {
      DocumentProfileId: documentProfileId,
      DocumentBinding: null,
      InitialProjectionIds: [],
      Instantiation: workspaceInstantiationModes.shared,
      LayoutProfileId: null,
      WorkspaceId: workspaceId,
    },
  }
}

export function hasNavigationShellRegion(
  navigation: Pick<NavigationDefinition, 'Shell'> | null,
  regionId: string,
) {
  return navigation?.Shell.Regions.some((region) => region.Id === regionId) ?? false
}

export function getNavigationShellRegions(
  navigation: Pick<NavigationDefinition, 'Shell'> | null,
  options: {
    readonly placement?: string
    readonly regionIds?: readonly string[]
  } = {},
): readonly NavigationShellRegionDefinition[] {
  const regionIds = options.regionIds ? new Set(options.regionIds) : null
  return navigation?.Shell.Regions.filter((region) =>
    (!options.placement || region.Placement === options.placement) &&
    (!regionIds || regionIds.has(region.Id)),
  ) ?? []
}

export function resolveNavigationRouteId(
  navigation: Pick<NavigationDefinitionProjection, 'Routes'> | null,
  pathname: string,
) {
  if (!navigation) {
    return null
  }

  return (
    navigation.Routes.find((route) =>
      doesRouteTemplateMatch(pathname, route.PathTemplate),
    )?.Id ?? null
  )
}

export function shouldUseNavigationRouteTransitions(
  navigation: Pick<NavigationDefinition, 'Contexts'> | null,
) {
  return (
    resolveNavigationHistory(navigation)?.RouteUpdateMode ??
    navigationRouteUpdateModes.transition
  ) !== navigationRouteUpdateModes.synchronous
}

export function createNavigationRouteInstanceKey(
  navigation: Pick<NavigationDefinitionProjection, 'Contexts' | 'Routes'> | null,
  location: {
    readonly hash?: string
    readonly pathname: string
    readonly search?: string
  },
) {
  const identity =
    resolveNavigationHistory(navigation)?.RouteInstanceIdentity ??
    navigationRouteInstanceIdentityKinds.matchedRoute

  switch (identity) {
    case navigationRouteInstanceIdentityKinds.fullLocation:
      return `${location.pathname}${location.search ?? ''}${location.hash ?? ''}`
    case navigationRouteInstanceIdentityKinds.pathAndSearch:
      return `${location.pathname}${location.search ?? ''}`
    case navigationRouteInstanceIdentityKinds.path:
      return location.pathname
    case navigationRouteInstanceIdentityKinds.matchedRoute:
    default:
      return resolveNavigationRouteId(navigation, location.pathname) ?? location.pathname
  }
}

function resolveNavigationHistory(
  navigation: Pick<NavigationDefinition, 'Contexts'> | null,
): NavigationHistoryDefinition | null {
  return (
    navigation?.Contexts.find(
      (context) =>
        context.Kind === navigationContextKinds.browser &&
        context.History.Kind === navigationHistoryKinds.browser,
    )?.History ??
    navigation?.Contexts.find((context) => context.Kind === navigationContextKinds.browser)
      ?.History ??
    navigation?.Contexts[0]?.History ??
    null
  )
}

export function doesRouteTemplateMatch(pathname: string, template: string) {
  const pathSegments = toPathSegments(pathname)
  const templateSegments = toPathSegments(template)
  if (pathSegments.length !== templateSegments.length) {
    return false
  }

  for (let index = 0; index < templateSegments.length; index += 1) {
    const templateSegment = templateSegments[index]
    if (templateSegment.startsWith('{') && templateSegment.endsWith('}')) {
      continue
    }

    if (templateSegment !== pathSegments[index]) {
      return false
    }
  }

  return true
}

export function toPathSegments(path: string) {
  const pathname = path.split(/[?#]/)[0] ?? '/'
  if (pathname === '/') {
    return []
  }

  return pathname.replace(/^\/+|\/+$/g, '').split('/').filter(Boolean)
}

function createSingleViewPageHostLayout(viewId: string) {
  return {
    DefaultRegionId: 'content',
    Root: {
      Children: [],
      Id: 'content',
      Kind: layoutNodeKinds.view,
      Orientation: layoutOrientations.none,
      Placement: 'main',
      ProjectionIds: [],
      Size: null,
      ViewIds: [viewId],
    },
  }
}

function createNavigationLabel(id: string) {
  return id
    .split(/[-_.\s]+/)
    .filter(Boolean)
    .map((part) => `${part.charAt(0).toUpperCase()}${part.slice(1)}`)
    .join(' ')
}
