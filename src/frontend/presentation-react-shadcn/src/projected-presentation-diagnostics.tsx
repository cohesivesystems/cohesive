import type {
  PresentationProjectionDiagnostic,
  PresentationProjectionDiagnosticSeverity,
} from '@cohesive/presentation-core'

export interface ProjectedPresentationDiagnosticsProps {
  readonly diagnostics: readonly PresentationProjectionDiagnostic[]
  readonly emptyLabel?: string
  readonly minSeverity?: PresentationProjectionDiagnosticSeverity
  readonly title?: string | null
}

const severityRank: Record<PresentationProjectionDiagnosticSeverity, number> = {
  info: 0,
  warning: 1,
  error: 2,
}

/** Renders projection TODOs emitted by frontend interpreters. */
export function ProjectedPresentationDiagnostics({
  diagnostics,
  emptyLabel,
  minSeverity = 'warning',
  title = 'Projection diagnostics',
}: ProjectedPresentationDiagnosticsProps) {
  const visibleDiagnostics = diagnostics.filter(
    (diagnostic) => severityRank[diagnostic.severity] >= severityRank[minSeverity],
  )

  if (visibleDiagnostics.length === 0) {
    return emptyLabel ? (
      <p className="rounded-md border border-slate-950/10 bg-slate-50 px-3 py-2 text-sm text-slate-500">
        {emptyLabel}
      </p>
    ) : null
  }

  const hasError = visibleDiagnostics.some((diagnostic) => diagnostic.severity === 'error')
  const hasWarning = visibleDiagnostics.some((diagnostic) => diagnostic.severity === 'warning')

  return (
    <section
      className={getDiagnosticsPanelClassName({ hasError, hasWarning })}
    >
      {title ? <div className="font-medium">{title}</div> : null}
      <ul className="mt-1 grid gap-1">
        {visibleDiagnostics.map((diagnostic) => (
          <li className="grid gap-0.5" key={`${diagnostic.source}:${diagnostic.id}`}>
            <div className="flex flex-wrap items-center gap-1">
              <span className={getDiagnosticSeverityClassName(diagnostic.severity)}>
                {formatDiagnosticSeverity(diagnostic.severity)}
              </span>
              {diagnostic.category ? (
                <span className="rounded bg-white/70 px-1.5 py-0.5 text-[0.65rem] font-medium uppercase tracking-wide opacity-80">
                  {diagnostic.category}
                </span>
              ) : null}
              {diagnostic.interpretation ? (
                <span className="rounded bg-white/70 px-1.5 py-0.5 text-[0.65rem] font-medium uppercase tracking-wide opacity-80">
                  {diagnostic.interpretation.status}
                </span>
              ) : null}
              <span>{diagnostic.message}</span>
            </div>
            <span className="break-all font-mono text-[0.7rem] opacity-75">
              {diagnostic.source}
              {diagnostic.interpretation
                ? ` - target:${diagnostic.interpretation.target}`
                : ''}
              {diagnostic.subject ? ` - ${diagnostic.subject.kind}:${diagnostic.subject.id}` : ''}
            </span>
            {diagnostic.suggestedNextStep ? (
              <span className="text-[0.72rem] opacity-85">
                {diagnostic.suggestedNextStep}
              </span>
            ) : null}
          </li>
        ))}
      </ul>
    </section>
  )
}

function getDiagnosticsPanelClassName({
  hasError,
  hasWarning,
}: {
  readonly hasError: boolean
  readonly hasWarning: boolean
}) {
  if (hasError) {
    return 'rounded-md border border-red-300/70 bg-red-50 px-3 py-2 text-sm text-red-900'
  }

  if (hasWarning) {
    return 'rounded-md border border-amber-300/70 bg-amber-50 px-3 py-2 text-sm text-amber-950'
  }

  return 'rounded-md border border-sky-200/80 bg-sky-50 px-3 py-2 text-sm text-sky-950'
}

function getDiagnosticSeverityClassName(
  severity: PresentationProjectionDiagnosticSeverity,
) {
  if (severity === 'error') {
    return 'rounded bg-red-100 px-1.5 py-0.5 text-[0.65rem] font-medium uppercase tracking-wide text-red-700'
  }

  if (severity === 'warning') {
    return 'rounded bg-amber-100 px-1.5 py-0.5 text-[0.65rem] font-medium uppercase tracking-wide text-amber-800'
  }

  return 'rounded bg-sky-100 px-1.5 py-0.5 text-[0.65rem] font-medium uppercase tracking-wide text-sky-700'
}

function formatDiagnosticSeverity(
  severity: PresentationProjectionDiagnosticSeverity,
) {
  return severity === 'warning' ? 'warn' : severity
}
