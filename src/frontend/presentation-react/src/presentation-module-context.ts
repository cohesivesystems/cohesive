import { createContext, useContext } from 'react'

import type { PresentationModuleDefinition } from '@cohesivesystems/presentation-core'

export const PresentationModuleContext =
  createContext<PresentationModuleDefinition | null>(null)

export function usePresentationModule() {
  return useContext(PresentationModuleContext)
}
