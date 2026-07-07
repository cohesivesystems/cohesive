import type { ReactNode } from 'react'

import type {
  NavigationDefinitionProjection,
} from '@cohesivesystems/presentation-core'
import type {
  NavigationRouteDefinition,
  PageHostDefinition,
} from '@cohesivesystems/presentation-contracts'
import type {
  UnknownPageHostRenderContext,
} from '@cohesivesystems/presentation-react'
import { ProjectedStatusBlock } from './projected-activity-state'

type UnavailablePageHostReason = UnknownPageHostRenderContext<
  unknown,
  NavigationDefinitionProjection<NavigationRouteDefinition, PageHostDefinition>,
  NavigationRouteDefinition,
  PageHostDefinition,
  unknown
>['reason']

/**
 * Diagnostic renderer used when a navigation target cannot be projected to a
 * concrete page-host component.
 */
export function renderDefaultUnavailablePageHost<
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
>({
  componentKey,
  pageHost,
  reason,
  route,
}: UnknownPageHostRenderContext<
  TModule,
  TNavigation,
  TRoute,
  TPageHost,
  TProjectionContext
>): ReactNode {
  const label =
    reason === 'unmatched-route'
      ? 'This route is not available.'
      : createUnavailablePageHostLabel({ componentKey, pageHost, reason, route })

  return <ProjectedStatusBlock label={label} />
}

function createUnavailablePageHostLabel({
  componentKey,
  pageHost,
  reason,
  route,
}: {
  readonly componentKey?: string | null
  readonly pageHost: PageHostDefinition | null
  readonly reason: UnavailablePageHostReason
  readonly route: NavigationRouteDefinition | null
}) {
  if (!pageHost) {
    return route?.Label
      ? `${route.Label} is not available.`
      : 'This navigation target is not available.'
  }

  if (componentKey) {
    return `Navigation target '${pageHost.Id}' is bound to unknown component '${componentKey}'.`
  }

  if (reason === 'missing-component-binding' && route?.Label) {
    return `${route.Label} is not available yet.`
  }

  return `Navigation target '${pageHost.Id}' is not available.`
}
