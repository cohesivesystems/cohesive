import type {
  PresentationContentDefinition,
} from './module'
import type {
  PresentationProjectionDiagnostic,
  PresentationProjectionDiagnosticSubject,
} from './presentation-projection-diagnostics'

export interface ProjectPresentationContentDiagnosticsOptions {
  readonly content: PresentationContentDefinition | null | undefined
  readonly contentFallbackDescription?: string
  readonly descriptionFallbackDescription?: string
  readonly details?: Readonly<Record<string, unknown>>
  readonly diagnosticIdPrefix: string
  readonly requireDescription?: boolean
  readonly requireTitle?: boolean
  readonly source: string
  readonly subject: PresentationProjectionDiagnosticSubject
  readonly surfaceLabel: string
  readonly titleFallbackDescription?: string
}

export function projectPresentationContentDiagnostics({
  content,
  contentFallbackDescription,
  descriptionFallbackDescription,
  details,
  diagnosticIdPrefix,
  requireDescription = false,
  requireTitle = false,
  source,
  subject,
  surfaceLabel,
  titleFallbackDescription,
}: ProjectPresentationContentDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  if (!content) {
    return [
      createPresentationContentDiagnostic({
        details,
        diagnosticIdPrefix,
        fallbackDescription:
          contentFallbackDescription ??
          titleFallbackDescription ??
          descriptionFallbackDescription ??
          'local fallback content',
        reason: 'missing-content',
        source,
        subject,
        surfaceLabel,
        surfacePart: 'content',
      }),
    ]
  }

  const diagnostics: PresentationProjectionDiagnostic[] = []
  if (requireTitle && !content.Title) {
    diagnostics.push(
      createPresentationContentDiagnostic({
        details,
        diagnosticIdPrefix,
        fallbackDescription: titleFallbackDescription ?? 'local title fallback content',
        reason: 'missing-content-title',
        source,
        subject,
        surfaceLabel,
        surfacePart: 'title content',
      }),
    )
  }

  if (requireDescription && !content.Description && !content.DescriptionTemplate) {
    diagnostics.push(
      createPresentationContentDiagnostic({
        details,
        diagnosticIdPrefix,
        fallbackDescription: descriptionFallbackDescription ?? 'local description fallback content',
        reason: 'missing-content-description',
        source,
        subject,
        surfaceLabel,
        surfacePart: 'description content',
      }),
    )
  }

  return diagnostics
}

function createPresentationContentDiagnostic({
  details,
  diagnosticIdPrefix,
  fallbackDescription,
  reason,
  source,
  subject,
  surfaceLabel,
  surfacePart,
}: {
  readonly details?: Readonly<Record<string, unknown>>
  readonly diagnosticIdPrefix: string
  readonly fallbackDescription: string
  readonly reason: string
  readonly source: string
  readonly subject: PresentationProjectionDiagnosticSubject
  readonly surfaceLabel: string
  readonly surfacePart: string
}): PresentationProjectionDiagnostic {
  return {
    category: 'incomplete-projection',
    details,
    id: `${diagnosticIdPrefix}.${reason}`,
    interpretation: {
      status: 'locally-interpreted',
      target: 'react',
    },
    message: `${surfaceLabel} does not declare ${surfacePart}; the frontend will use ${fallbackDescription}.`,
    severity: 'warning',
    source,
    subject,
  }
}
