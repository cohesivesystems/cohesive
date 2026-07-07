import { useCallback, useMemo, type Context, type PropsWithChildren } from 'react'
import { useNavigate, useResolvedPath } from 'react-router'

import {
  createNavigationHref,
  type NavigationDefinitionProjection,
  type PresentationNavigationRuntime,
} from '@cohesivesystems/presentation-core'
import {
  PresentationNavigationRuntimeContext,
} from './navigation-runtime-context'

export interface ProjectedNavigationContextValue<TNavigation extends NavigationDefinitionProjection = NavigationDefinitionProjection> extends PresentationNavigationRuntime {
  readonly navigation: TNavigation | null
}

export interface ProjectedNavigationProviderProps<
  TNavigation extends NavigationDefinitionProjection,
> extends PropsWithChildren {
  readonly context?: Context<ProjectedNavigationContextValue<TNavigation>>
  readonly navigation: TNavigation | null
}

/**
 * Generic React binding for semantic navigation definitions. App-specific
 * navigation contexts can be layered on top, but href creation and route
 * execution stay owned by the presentation projection runtime.
 */
export function ProjectedNavigationProvider<
  TNavigation extends NavigationDefinitionProjection,
>({
  children,
  context,
  navigation,
}: ProjectedNavigationProviderProps<TNavigation>) {
  const navigate = useNavigate()
  const rootPath = useResolvedPath('/')
  const createHref = useCallback<ProjectedNavigationContextValue['createHref']>(
    (routeId, parameters) =>
      navigation ? createNavigationHref(navigation, routeId, parameters) : null,
    [navigation],
  )
  const navigateHref = useCallback<ProjectedNavigationContextValue['navigateHref']>(
    (href) => {
      if (href.length > 0) {
        void navigate(toRootRelativeHref(href, rootPath.pathname), {
          flushSync: true,
          relative: 'path',
        })
      }
    },
    [navigate, rootPath.pathname],
  )
  const navigateRoute = useCallback<ProjectedNavigationContextValue['navigateRoute']>(
    (routeId, parameters) => {
      const href = createHref(routeId, parameters)
      if (href) {
        navigateHref(href)
      }
    },
    [createHref, navigateHref],
  )
  const runtime = useMemo<PresentationNavigationRuntime>(
    () => ({
      createHref,
      navigateHref,
      navigateRoute,
    }),
    [createHref, navigateHref, navigateRoute],
  )
  const contextValue = useMemo<ProjectedNavigationContextValue<TNavigation>>(
    () => ({
      ...runtime,
      navigation,
    }),
    [navigation, runtime],
  )
  const ApplicationNavigationContext = context

  return (
    <PresentationNavigationRuntimeContext.Provider value={runtime}>
      {ApplicationNavigationContext ? (
        <ApplicationNavigationContext.Provider value={contextValue}>
          {children}
        </ApplicationNavigationContext.Provider>
      ) : (
        children
      )}
    </PresentationNavigationRuntimeContext.Provider>
  )
}

function toRootRelativeHref(href: string, rootPathname: string) {
  if (/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(href) || href.startsWith('//')) {
    return href
  }

  if (href.startsWith('/')) {
    return href
  }

  const root = rootPathname === '/' ? '' : rootPathname.replace(/\/+$/g, '')
  return `${root}/${href.replace(/^\/+/g, '')}`
}
