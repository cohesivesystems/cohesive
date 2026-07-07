import {
  useContext,
  useMemo,
  type Context,
  type PropsWithChildren,
} from 'react'

import type { PresentationStandardsComposer } from '@cohesive/presentation-core'

export interface PresentationStandardsProviderProps<TStandards, TContribution>
  extends PropsWithChildren {
  readonly compose: PresentationStandardsComposer<TStandards, TContribution>
  readonly context: Context<TStandards | null>
  readonly standards: TContribution
}

/**
 * Generic standards provider. Nested providers compose their local contribution
 * over the inherited standards, so feature areas can override or enrich app
 * defaults without replacing the whole standards object.
 */
export function PresentationStandardsProvider<TStandards, TContribution>({
  children,
  compose,
  context,
  standards,
}: PresentationStandardsProviderProps<TStandards, TContribution>) {
  const StandardsContext = context
  const parent = useContext(StandardsContext)
  const value = useMemo(
    () => (
      parent
        ? compose(parent as unknown as TContribution, standards)
        : compose(standards)
    ),
    [compose, parent, standards],
  )

  return (
    <StandardsContext.Provider value={value}>
      {children}
    </StandardsContext.Provider>
  )
}
