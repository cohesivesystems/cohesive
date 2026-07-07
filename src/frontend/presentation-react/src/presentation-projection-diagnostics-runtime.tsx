import {
  useCallback,
  useMemo,
  useState,
  type ReactNode,
} from 'react'

import {
  PresentationProjectionDiagnosticsContext,
} from './presentation-projection-diagnostics-context'
import {
  mergePresentationProjectionDiagnostics,
  type PresentationProjectionDiagnostic,
} from '@cohesive/presentation-core'

interface PresentationProjectionDiagnosticsProviderProps {
  readonly children: ReactNode
}

/**
 * Collects projection diagnostics from local interpreters so developer tooling
 * can render one consolidated view instead of scattering warnings through app
 * chrome.
 */
export function PresentationProjectionDiagnosticsProvider({
  children,
}: PresentationProjectionDiagnosticsProviderProps) {
  const [diagnosticsBySource, setDiagnosticsBySource] = useState<
    Readonly<Record<string, readonly PresentationProjectionDiagnostic[]>>
  >({})
  const setDiagnostics = useCallback((
    sourceId: string,
    diagnostics: readonly PresentationProjectionDiagnostic[],
  ) => {
    setDiagnosticsBySource((current) => {
      if (areDiagnosticsEqual(current[sourceId] ?? [], diagnostics)) {
        return current
      }

      return {
        ...current,
        [sourceId]: [...diagnostics],
      }
    })
  }, [])
  const clearDiagnostics = useCallback((sourceId: string) => {
    setDiagnosticsBySource((current) => {
      if (!current[sourceId]) {
        return current
      }

      const next = { ...current }
      delete next[sourceId]
      return next
    })
  }, [])
  const diagnostics = useMemo(
    () => mergePresentationProjectionDiagnostics(...Object.values(diagnosticsBySource)),
    [diagnosticsBySource],
  )
  const contextValue = useMemo(
    () => ({
      clearDiagnostics,
      diagnostics,
      setDiagnostics,
    }),
    [clearDiagnostics, diagnostics, setDiagnostics],
  )

  return (
    <PresentationProjectionDiagnosticsContext.Provider value={contextValue}>
      {children}
    </PresentationProjectionDiagnosticsContext.Provider>
  )
}

function areDiagnosticsEqual(
  left: readonly PresentationProjectionDiagnostic[],
  right: readonly PresentationProjectionDiagnostic[],
) {
  if (left.length !== right.length) {
    return false
  }

  return left.every((diagnostic, index) => {
    const candidate = right[index]
    return Boolean(
      candidate &&
        diagnostic.id === candidate.id &&
        diagnostic.message === candidate.message &&
        diagnostic.severity === candidate.severity &&
        diagnostic.source === candidate.source,
    )
  })
}
