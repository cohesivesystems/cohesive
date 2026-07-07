import { createContext } from 'react'

import type {
  PresentationProjectionDiagnostic,
} from '@cohesive/presentation-core'

export interface PresentationProjectionDiagnosticsRegistry {
  readonly clearDiagnostics: (sourceId: string) => void
  readonly diagnostics: readonly PresentationProjectionDiagnostic[]
  readonly setDiagnostics: (
    sourceId: string,
    diagnostics: readonly PresentationProjectionDiagnostic[],
  ) => void
}

export const PresentationProjectionDiagnosticsContext =
  createContext<PresentationProjectionDiagnosticsRegistry | null>(null)
