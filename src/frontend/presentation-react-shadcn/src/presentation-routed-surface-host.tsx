import type { PresentationDataSourceBinding } from '@cohesive/presentation-core'
import { PresentationDataSourceBinder } from '@cohesive/presentation-react'
import type { PresentationRendererRegistry } from '@cohesive/presentation-react'
import type { PresentationSurface } from '@cohesive/presentation-core'
import { PresentationSurfaceRenderer } from './presentation-surface-renderer'

export interface PresentationRoutedSurfaceHostProps<TContext = undefined> {
  readonly bindings: readonly PresentationDataSourceBinding[]
  readonly className?: string
  readonly componentSet?: string
  readonly contentClassName?: string
  readonly context?: TContext
  readonly rendererRegistry: PresentationRendererRegistry<TContext>
  readonly surface: PresentationSurface | null
}

/**
 * Standard route-level host for semantic presentation surfaces. It owns the
 * coarse page shell only; the surface tree, data sources, and concrete controls
 * are still projected by the presentation runtime.
 */
export function PresentationRoutedSurfaceHost<TContext = undefined>({
  bindings,
  className = 'min-h-screen bg-background px-4 py-5 text-slate-700',
  componentSet,
  contentClassName = 'mx-auto flex max-w-360 flex-col gap-5',
  context,
  rendererRegistry,
  surface,
}: PresentationRoutedSurfaceHostProps<TContext>) {
  return (
    <div className={className}>
      <main className={contentClassName}>
        <PresentationDataSourceBinder bindings={bindings}>
          {(dataSources) => (
            <PresentationSurfaceRenderer
              componentSet={componentSet}
              context={context as TContext}
              dataSources={dataSources}
              rendererRegistry={rendererRegistry}
              surface={surface}
            />
          )}
        </PresentationDataSourceBinder>
      </main>
    </div>
  )
}
