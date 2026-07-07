import { createContext, useContext } from 'react'

import type {
  PresentationNavigationRuntime,
} from '@cohesive/presentation-core'

/**
 * Re-exports the core navigation runtime contracts used by React navigation
 * providers and projected components.
 */
export type {
  PresentationHrefNavigator,
  PresentationNavigationHrefFactory,
  PresentationNavigationRuntime,
  PresentationRouteNavigator,
} from '@cohesive/presentation-core'

/**
 * React context carrying navigation services for projected presentation
 * components.
 *
 * The default runtime is intentionally inert so components can render in tests,
 * previews, and partially wired hosts without failing at context-read time.
 */
export const PresentationNavigationRuntimeContext =
  createContext<PresentationNavigationRuntime>({
    createHref: () => null,
    navigateHref: () => undefined,
    navigateRoute: () => undefined,
  })

/**
 * Reads the active presentation navigation runtime from React context.
 *
 * Components should use this hook instead of importing a concrete router so the
 * same projected UI can run under different host navigation adapters.
 */
export function usePresentationNavigationRuntime() {
  return useContext(PresentationNavigationRuntimeContext)
}
