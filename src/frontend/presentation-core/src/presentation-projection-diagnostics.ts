export type PresentationProjectionDiagnosticSeverity = 'error' | 'info' | 'warning'

export type PresentationProjectionDiagnosticCategory =
  | 'escape-hatch'
  | 'incomplete-projection'
  | 'local-interpretation'
  | 'missing-binding'
  | 'missing-definition'
  | 'unsupported'
  | 'unbound'

export type PresentationProjectionInterpretationStatus =
  | 'bound'
  | 'escape-hatch'
  | 'locally-interpreted'
  | 'projected'
  | 'unbound'
  | 'unsupported'

/** Stable reference to the IR element or frontend binding that produced a diagnostic. */
export interface PresentationProjectionDiagnosticSubject {
  readonly id: string
  readonly kind: string
  readonly name?: string | null
}

/** Projection/interpreter boundary that produced a diagnostic. */
export interface PresentationProjectionInterpretation {
  readonly status: PresentationProjectionInterpretationStatus
  readonly target: string
}

/**
 * Structured TODO emitted by projection interpreters when a backend-declared
 * construct cannot yet be fully interpreted by the frontend.
 */
export interface PresentationProjectionDiagnostic {
  readonly category?: PresentationProjectionDiagnosticCategory
  readonly details?: Readonly<Record<string, unknown>>
  readonly id: string
  readonly interpretation?: PresentationProjectionInterpretation
  readonly message: string
  readonly severity: PresentationProjectionDiagnosticSeverity
  readonly source: string
  readonly subject?: PresentationProjectionDiagnosticSubject
  readonly suggestedNextStep?: string
}

export function createPresentationProjectionDiagnostic(
  diagnostic: PresentationProjectionDiagnostic,
): PresentationProjectionDiagnostic {
  return diagnostic
}

/** Collapses diagnostic streams from independent projection interpreters. */
export function mergePresentationProjectionDiagnostics(
  ...groups: readonly (readonly PresentationProjectionDiagnostic[] | null | undefined)[]
): readonly PresentationProjectionDiagnostic[] {
  const diagnostics = groups.flatMap((group) => group ?? [])
  const seen = new Set<string>()

  return diagnostics.filter((diagnostic) => {
    const key = `${diagnostic.source}:${diagnostic.id}`
    if (seen.has(key)) {
      return false
    }

    seen.add(key)
    return true
  })
}
