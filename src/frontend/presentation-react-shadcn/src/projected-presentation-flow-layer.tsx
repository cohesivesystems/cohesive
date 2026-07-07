import { useMemo, type ReactNode } from 'react'

import type {
  PresentationDataSourceResolver,
  PresentationModuleDefinition,
  ViewChromeSlotDefinition,
  ViewDefinition,
} from '@cohesive/presentation-core'
import {
  createPresentationTestAttributes,
} from '@cohesive/presentation-core'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type { PresentationDesignSystem } from '@cohesive/presentation-tailwind'
import type {
  PresentationActionGroupOptions,
} from './presentation-action-group'
import type {
  ProjectedDocumentActionStatusMap,
} from '@cohesive/presentation-core'
import type {
  ProjectedViewSurfaceChromeSlotRenderer,
} from './projected-view-surface'
import {
  renderStandardViewChromeSlot,
} from './standard-view-chrome-slot-renderers'
import type {
  PresentationFlowRuntimeEntry,
  PresentationFlowRuntimeRegistrySnapshot,
} from '@cohesive/presentation-react'
import {
  createProjectedPromptChildViewRenderer,
  projectPromptChildViewRendererDiagnostics,
  type ProjectedPromptChildViewRendererRegistry,
} from './projected-prompt-child-view-renderer'
import { ProjectedPromptView } from './projected-prompt-view'
import {
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesive/presentation-react'

export interface ProjectedPresentationFlowLayerProps<TContext> {
  readonly actionGroupOptions?: PresentationActionGroupOptions<TContext>
  readonly actionStatuses?: ProjectedDocumentActionStatusMap
  readonly actionRegionId?: string
  readonly childViewRegistry: ProjectedPromptChildViewRendererRegistry<TContext>
  readonly componentSet?: string
  readonly componentSystem: PresentationComponentSystem
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly designSystem: PresentationDesignSystem
  readonly flowRegistry: PresentationFlowRuntimeRegistrySnapshot
  readonly module: PresentationModuleDefinition | null
  readonly renderChromeSlot?: (
    context: ProjectedPresentationFlowLayerChromeSlotRenderContext<TContext>,
  ) => ReactNode
  readonly shouldRenderEntry?: (
    context: ProjectedPresentationFlowLayerRenderContext,
  ) => boolean
}

export interface ProjectedPresentationFlowLayerRenderContext
  extends Omit<PresentationFlowRuntimeEntry, 'view'> {
  readonly view: ViewDefinition
}

export interface ProjectedPresentationFlowLayerChromeSlotRenderContext<TContext>
  extends ProjectedPresentationFlowLayerRenderContext {
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly slot: ViewChromeSlotDefinition
}

export function ProjectedPresentationFlowLayer<TContext>({
  actionGroupOptions,
  actionStatuses,
  actionRegionId,
  childViewRegistry,
  componentSet,
  componentSystem,
  context,
  dataSourceResolver,
  designSystem,
  flowRegistry,
  module,
  renderChromeSlot,
  shouldRenderEntry,
}: ProjectedPresentationFlowLayerProps<TContext>) {
  const promptChildViewRendererDiagnostics = useMemo(
    () =>
      module
        ? flowRegistry.activeEntries.flatMap((entry) =>
            entry.view
              ? projectPromptChildViewRendererDiagnostics({
                componentSet,
                module,
                promptView: entry.view,
                registry: childViewRegistry,
                sourceId: 'presentation-flow.prompt-child-view-renderers',
              })
              : [])
        : [],
    [childViewRegistry, componentSet, flowRegistry.activeEntries, module],
  )
  useRegisterPresentationProjectionDiagnostics(
    'presentation-flow.prompt-child-view-renderers',
    promptChildViewRendererDiagnostics,
  )

  if (!module) {
    return null
  }

  return (
    <>
      {flowRegistry.activeEntries.map((entry) => {
        if (!entry.view) {
          return null
        }

        const promptView = entry.view
        const renderContext = {
          ...entry,
          view: promptView,
        } satisfies ProjectedPresentationFlowLayerRenderContext
        if (shouldRenderEntry?.(renderContext) === false) {
          return null
        }

        const renderPromptChildView = createProjectedPromptChildViewRenderer({
          componentSet,
          componentSystem,
          context,
          dataSourceResolver,
          designSystem,
          module,
          promptView,
          registry: childViewRegistry,
        })
        const renderPromptChromeSlot: ProjectedViewSurfaceChromeSlotRenderer = (slot, view) =>
          renderChromeSlot?.({
            ...renderContext,
            context,
            dataSourceResolver,
            slot,
          }) ?? renderStandardViewChromeSlot({
            actionContext: context,
            actionGroupOptions,
            actionStatuses,
            componentSystem,
            dataSourceResolver,
            designSystem,
            module,
            resource: dataSourceResolver.resolveViewPrimary(view ?? promptView)?.data,
            slot,
            view: view ?? promptView,
            workspaceView: view ?? promptView,
          })

        return (
          <div
            key={entry.flow.Id}
            {...createPresentationTestAttributes({
              flowId: entry.flow.Id,
              flowStateId: entry.state.Id,
              viewId: promptView.Id,
            })}
          >
            <ProjectedPromptView
              actionGroupOptions={actionGroupOptions}
              actionRegionId={actionRegionId}
              componentSystem={componentSystem}
              context={context}
              dataSourceResolver={dataSourceResolver}
              designSystem={designSystem}
              module={module}
              renderChromeSlot={renderPromptChromeSlot}
              renderView={renderPromptChildView}
              view={promptView}
            />
          </div>
        )
      })}
    </>
  )
}
