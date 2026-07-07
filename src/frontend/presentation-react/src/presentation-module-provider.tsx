import { type PropsWithChildren } from 'react'

import type { PresentationModuleDefinition } from '@cohesivesystems/presentation-core'
import { PresentationModuleContext } from './presentation-module-context'

export interface PresentationModuleProviderProps extends PropsWithChildren {
  readonly module: PresentationModuleDefinition | null
}

export function PresentationModuleProvider({
  children,
  module,
}: PresentationModuleProviderProps) {
  return (
    <PresentationModuleContext.Provider value={module}>
      {children}
    </PresentationModuleContext.Provider>
  )
}
