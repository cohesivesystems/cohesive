import type {
  PresentationProjectionTrace,
  PresentationProjectionTraceView,
} from '@cohesivesystems/presentation-core'

export interface PresentationProjectionTracePanelProps {
  readonly enabled?: boolean
  readonly trace: PresentationProjectionTrace
}

export function PresentationProjectionTracePanel({
  enabled = true,
  trace,
}: PresentationProjectionTracePanelProps) {
  if (!enabled) {
    return null
  }

  return (
    <details className="fixed bottom-4 right-4 z-50 max-h-[70vh] w-[30rem] max-w-[calc(100vw-2rem)] overflow-hidden rounded-md border border-slate-950/10 bg-white/95 text-xs text-slate-700 shadow-xl backdrop-blur">
      <summary className="cursor-pointer select-none border-b border-slate-950/10 px-3 py-2 font-medium text-slate-950">
        Projection trace
      </summary>
      <div className="max-h-[calc(70vh-2.25rem)] overflow-auto p-3">
        <PresentationProjectionTraceContent trace={trace} />
      </div>
    </details>
  )
}

export function PresentationProjectionTraceContent({
  trace,
}: {
  readonly trace: PresentationProjectionTrace
}) {
  return (
    <div className="grid gap-3 text-xs text-slate-700">
      <section className="grid gap-1">
        <TraceRow label="Path" value={trace.pathname} />
        <TraceRow label="Module" value={trace.moduleAvailable ? 'available' : 'missing'} />
        <TraceRow label="Route" value={formatRoute(trace)} />
        <TraceRow label="Page host" value={formatPageHost(trace)} />
        <TraceRow label="Host renderer" value={formatPageHostRenderer(trace)} />
        <TraceRow label="Surface" value={formatSurface(trace)} />
        <TraceRow label="Data sources" value={formatList(trace.dataSourceIds)} />
      </section>
      <section className="grid gap-2">
        <h2 className="text-[0.7rem] font-semibold uppercase text-slate-500">Views</h2>
        {trace.views.length > 0 ? (
          trace.views.map((view) => (
            <TraceView key={view.id} view={view} />
          ))
        ) : (
          <p className="rounded border border-slate-950/10 bg-slate-50 px-2 py-1 text-slate-500">
            No resolved view tree.
          </p>
        )}
      </section>
    </div>
  )
}

function TraceView({ view }: { readonly view: PresentationProjectionTraceView }) {
  const rendererLabel = view.rendererResolved
    ? view.resolutionSource ?? 'resolved'
    : 'missing'

  return (
    <div className="grid gap-1 rounded border border-slate-950/10 bg-slate-50 px-2 py-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="font-medium text-slate-950">{view.name}</span>
        <span className="rounded bg-white px-1.5 py-0.5 text-[0.68rem] text-slate-600">
          {rendererLabel}
        </span>
      </div>
      <TraceRow label="Id" value={view.id} />
      <TraceRow label="Role" value={view.semanticRole} />
      <TraceRow label="Kind" value={view.kind} />
      <TraceRow label="Component" value={formatOptional(view.componentKey)} />
      <TraceRow label="Component role" value={formatOptional(view.componentRole)} />
      <TraceRow label="Subject" value={formatOptional(view.subjectDataSourceId)} />
      <TraceRow label="Data sources" value={formatList(view.dataSourceIds)} />
      <TraceRow label="Fields" value={formatList(view.fieldIds)} />
      <TraceRow label="Actions" value={String(view.actionCount)} />
      <TraceRow
        label="Regions"
        value={view.regions
          .map((region) => `${region.id}: ${formatList(region.viewIds)}`)
          .join('; ') || 'none'}
      />
    </div>
  )
}

function TraceRow({
  label,
  value,
}: {
  readonly label: string
  readonly value: string
}) {
  return (
    <div className="grid grid-cols-[5.75rem_minmax(0,1fr)] gap-2">
      <span className="text-slate-500">{label}</span>
      <span className="break-words font-mono text-[0.7rem] text-slate-800">{value}</span>
    </div>
  )
}

function formatRoute({ route }: PresentationProjectionTrace) {
  return route
    ? `${route.id} -> ${route.pageHostId} (${route.pathTemplate})`
    : 'unmatched'
}

function formatPageHost({ pageHost }: PresentationProjectionTrace) {
  return pageHost
    ? `${pageHost.id}; view=${formatOptional(pageHost.viewId)}; workspace=${formatOptional(pageHost.workspaceId)}`
    : 'none'
}

function formatPageHostRenderer({ pageHostRenderer }: PresentationProjectionTrace) {
  return pageHostRenderer
    ? `${formatOptional(pageHostRenderer.resolutionSource)}; key=${formatOptional(pageHostRenderer.rendererKey)}; componentRole=${formatOptional(pageHostRenderer.componentRole)}; viewRole=${formatOptional(pageHostRenderer.semanticRole)}; target=${formatOptional(pageHostRenderer.targetBindingSource)}`
    : 'none'
}

function formatSurface({ surface }: PresentationProjectionTrace) {
  return surface
    ? `${surface.id}; root=${formatOptional(surface.rootViewId)}; workspace=${formatOptional(surface.workspaceId)}`
    : 'none'
}

function formatList(values: readonly string[]) {
  return values.length > 0 ? values.join(', ') : 'none'
}

function formatOptional(value: string | null | undefined) {
  return value && value.length > 0 ? value : 'none'
}
