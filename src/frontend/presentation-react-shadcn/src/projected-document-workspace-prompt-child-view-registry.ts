import { createElement } from 'react'

import type {
  DocumentWorkspaceProjectionRendererRegistry,
} from '@cohesive/presentation-react'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type {
  PresentationDesignSystem,
} from '@cohesive/presentation-tailwind'
import type {
  ProjectedDocumentActionStatusMap,
} from '@cohesive/presentation-core'
import {
  mergeProjectedPromptChildViewRendererRegistries,
  promptChildViewSemanticRoles,
  type ProjectedPromptChildViewRenderer,
  type ProjectedPromptChildViewRendererRegistry,
} from './projected-prompt-child-view-renderer'
import {
  ProjectedDocumentWorkspacePromptPreviewProjection,
  ProjectedJsonDocumentDiffProjection,
  type ProjectedDocumentWorkspacePromptPreviewRenderContextOptions,
} from './projected-document-workspace-prompt-child-view-renderers'
import {
  promptChildViewComponentRoles,
} from '@cohesive/presentation-contracts'

export type {
  ProjectedDocumentWorkspacePromptPreviewRenderContextOptions,
} from './projected-document-workspace-prompt-child-view-renderers'

export interface CreateProjectedDocumentWorkspacePromptChildViewRegistryOptions<TContext> {
  readonly actionStatuses: ProjectedDocumentActionStatusMap
  readonly createPromptPreviewRenderContext: (
    context: ProjectedDocumentWorkspacePromptPreviewRenderContextOptions,
  ) => TContext
  readonly projectionRenderers: DocumentWorkspaceProjectionRendererRegistry<
    TContext,
    PresentationComponentSystem,
    PresentationDesignSystem
  >
  readonly registries?: readonly ProjectedPromptChildViewRendererRegistry<TContext>[]
  readonly renderJsonDocumentDiff?: ProjectedPromptChildViewRenderer<TContext>
  readonly renderPromptDocumentPreview?: ProjectedPromptChildViewRenderer<TContext>
}

/**
 * Creates the standard prompt child-view registry for document workspaces.
 *
 * The backend declares semantic child views such as JSON document diffs and
 * prompt document previews. This helper binds those roles to reusable
 * document-workspace renderers while leaving the concrete projection render
 * context to the app adapter.
 */
export function createProjectedDocumentWorkspacePromptChildViewRegistry<TContext>({
  actionStatuses,
  createPromptPreviewRenderContext,
  projectionRenderers,
  registries = [],
  renderJsonDocumentDiff,
  renderPromptDocumentPreview,
}: CreateProjectedDocumentWorkspacePromptChildViewRegistryOptions<TContext>): ProjectedPromptChildViewRendererRegistry<TContext> {
  const jsonDocumentDiffRenderer =
    renderJsonDocumentDiff ?? ((context) =>
      createElement(ProjectedJsonDocumentDiffProjection, {
        componentSystem: context.componentSystem,
        dataSourceResolver: context.dataSourceResolver,
        view: context.view,
      }))
  const promptDocumentPreviewRenderer =
    renderPromptDocumentPreview ?? ((context) =>
      createElement(ProjectedDocumentWorkspacePromptPreviewProjection<TContext>, {
        actionStatuses,
        componentSystem: context.componentSystem,
        createProjectionRenderContext: createPromptPreviewRenderContext,
        dataSourceResolver: context.dataSourceResolver,
        designSystem: context.designSystem,
        projectionRenderers,
        promptView: context.promptView,
        view: context.view,
        viewId: context.viewId,
      }))

  return mergeProjectedPromptChildViewRendererRegistries(
    {
      byComponentRole: {
        [promptChildViewComponentRoles.jsonDocumentDiff]: jsonDocumentDiffRenderer,
        [promptChildViewComponentRoles.promptDocumentPreview]: promptDocumentPreviewRenderer,
      },
      bySemanticRole: {
        [promptChildViewSemanticRoles.jsonDocumentDiff]: jsonDocumentDiffRenderer,
        [promptChildViewSemanticRoles.promptDocumentPreview]: promptDocumentPreviewRenderer,
      },
    },
    ...registries,
  )
}
