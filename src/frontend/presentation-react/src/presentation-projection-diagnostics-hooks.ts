import { useContext, useEffect } from 'react'

import {
  PresentationProjectionDiagnosticsContext,
} from './presentation-projection-diagnostics-context'
import type {
  PresentationProjectionDiagnostic,
} from '@cohesivesystems/presentation-core'

export function usePresentationProjectionDiagnostics() {
  return useContext(PresentationProjectionDiagnosticsContext)?.diagnostics ?? []
}

export function useRegisterPresentationProjectionDiagnostics(
  sourceId: string,
  diagnostics: readonly PresentationProjectionDiagnostic[],
) {
  const registry = useContext(PresentationProjectionDiagnosticsContext)
  const clearDiagnostics = registry?.clearDiagnostics
  const setDiagnostics = registry?.setDiagnostics

  useEffect(() => {
    setDiagnostics?.(sourceId, diagnostics)
  }, [diagnostics, setDiagnostics, sourceId])

  useEffect(() => {
    return () => clearDiagnostics?.(sourceId)
  }, [clearDiagnostics, sourceId])
}
