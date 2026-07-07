import { type ReactNode } from 'react'

import {
  type PresentationDataSourceBinding,
  type PresentationDataSourceStateMap,
} from '@cohesive/presentation-core'
import { usePresentationDataSources } from './presentation-data-source-binding-runtime'

export interface PresentationDataSourceBinderProps {
  readonly bindings: readonly PresentationDataSourceBinding[]
  readonly children: (dataSources: PresentationDataSourceStateMap) => ReactNode
}

/**
 * Binds declared presentation data sources to runtime state for projected React
 * components.
 */
export function PresentationDataSourceBinder({
  bindings,
  children,
}: PresentationDataSourceBinderProps) {
  const dataSources = usePresentationDataSources(bindings)
  return <>{children(dataSources)}</>
}
