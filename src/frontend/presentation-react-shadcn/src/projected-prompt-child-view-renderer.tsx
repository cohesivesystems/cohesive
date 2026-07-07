import type { ReactNode } from 'react'

import {
  createPresentationEnumDiscriminator,
  createPresentationProjectionDiagnostic,
  defaultPresentationComponentSet,
  findPresentationView,
  getPresentationViewSemanticRole,
  resolvePresentationComponentBinding,
  type PresentationBindingDefinition,
  type PresentationModuleDefinition,
  type PresentationProjectionDiagnostic,
  type PresentationDataSourceResolver,
  type ViewDefinition,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type {
  PresentationDesignSystem,
} from '@cohesivesystems/presentation-tailwind'
import { ProjectedStatusBlock } from './projected-activity-state'
import {
  presentationBindingKinds,
  presentationTargetKinds,
  type ViewKind,
  viewKindLabels,
} from '@cohesivesystems/presentation-contracts'

export interface ProjectedPromptChildViewRenderContext<TContext> {
  readonly componentKey: string | null
  readonly componentRole: string | null
  readonly componentSystem: PresentationComponentSystem
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly designSystem: PresentationDesignSystem
  readonly module: PresentationModuleDefinition
  readonly promptView: ViewDefinition
  readonly renderView: (viewId: string) => ReactNode
  readonly resolutionSource: ProjectedPromptChildViewRendererResolutionSource | null
  readonly view: ViewDefinition
  readonly viewId: string
}

/**
 * Renders one child view declared inside a prompt region. Returning undefined
 * lets a later fallback renderer handle the view.
 */
export type ProjectedPromptChildViewRenderer<TContext> = (
  context: ProjectedPromptChildViewRenderContext<TContext>,
) => ReactNode | undefined

/**
 * Semantic roles that prompt child views can expose through view contracts.
 * These roles are target-independent; frontend registries attach local React
 * interpretations to them.
 */
export const promptChildViewSemanticRoles = {
  jsonDocumentDiff: 'json-document-diff',
  promptDocumentPreview: 'prompt-document-preview',
} as const

/**
 * Prompt-scoped renderer registry. Resolution prefers semantic roles and view
 * kinds before component-key and view-id escape hatches.
 */
export interface ProjectedPromptChildViewRendererRegistry<TContext> {
  readonly byComponentRole?: Readonly<Record<string, ProjectedPromptChildViewRenderer<TContext>>>
  readonly byComponentKey?: Readonly<Record<string, ProjectedPromptChildViewRenderer<TContext>>>
  readonly bySemanticRole?: Readonly<Record<string, ProjectedPromptChildViewRenderer<TContext>>>
  readonly byViewId?: Readonly<Record<string, ProjectedPromptChildViewRenderer<TContext>>>
  readonly byViewKind?: Readonly<Record<string, ProjectedPromptChildViewRenderer<TContext>>>
  readonly fallback?: ProjectedPromptChildViewRenderer<TContext>
}

export interface ProjectedPromptUnknownChildViewRenderContext<TContext> {
  readonly componentKey: string | null
  readonly componentRole: string | null
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly module: PresentationModuleDefinition
  readonly promptView: ViewDefinition
  readonly reason: 'missing-renderer' | 'missing-view'
  readonly resolutionSource: ProjectedPromptChildViewRendererResolutionSource | null
  readonly semanticRole: string | null
  readonly view: ViewDefinition | null
  readonly viewId: string
}

export type ProjectedPromptUnknownChildViewRenderer<TContext> = (
  context: ProjectedPromptUnknownChildViewRenderContext<TContext>,
) => ReactNode

/**
 * Builds a recursive child-view renderer for projected prompt bodies. Flow
 * hosts can supply a small registry for form views, preview views, or richer
 * document panels while ProjectedPromptView keeps owning region traversal.
 */
export interface CreateProjectedPromptChildViewRendererOptions<TContext> {
  readonly componentSet?: string
  readonly componentSystem: PresentationComponentSystem
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly designSystem: PresentationDesignSystem
  readonly module: PresentationModuleDefinition
  readonly promptView: ViewDefinition
  readonly registry: ProjectedPromptChildViewRendererRegistry<TContext>
  readonly renderUnknownView?: ProjectedPromptUnknownChildViewRenderer<TContext>
}

export type ProjectedPromptChildViewRendererResolutionSource =
  | 'component-key'
  | 'component-role'
  | 'fallback'
  | 'semantic-role'
  | 'view-id'
  | 'view-kind'

export interface ProjectedPromptChildViewRendererResolution<TContext> {
  readonly componentKey: string | null
  readonly componentRole: string | null
  readonly renderer: ProjectedPromptChildViewRenderer<TContext> | null
  readonly resolutionSource: ProjectedPromptChildViewRendererResolutionSource | null
  readonly semanticRole: string
  readonly targetBinding: PresentationBindingDefinition | null
}

export interface ProjectPromptChildViewRendererDiagnosticsOptions<TContext> {
  readonly componentSet?: string
  readonly module: PresentationModuleDefinition
  readonly promptView: ViewDefinition
  readonly registry: ProjectedPromptChildViewRendererRegistry<TContext>
  readonly sourceId?: string
}

export function createProjectedPromptChildViewRenderer<TContext>({
  componentSet = defaultPresentationComponentSet,
  componentSystem,
  context,
  dataSourceResolver,
  designSystem,
  module,
  promptView,
  registry,
  renderUnknownView = renderDefaultUnknownPromptChildView,
}: CreateProjectedPromptChildViewRendererOptions<TContext>) {
  const renderView = (viewId: string): ReactNode => {
    const view = findPresentationView<ViewDefinition>(module, viewId)
    if (!view) {
      return renderUnknownView({
        componentKey: null,
        componentRole: null,
        context,
        dataSourceResolver,
        module,
        promptView,
        reason: 'missing-view',
        resolutionSource: null,
        semanticRole: null,
        view: null,
        viewId,
      })
    }

    const { componentKey, componentRole, renderer, resolutionSource, semanticRole } = resolvePromptChildViewRenderer({
      componentSet,
      module,
      registry,
      view,
    })
    if (!renderer) {
      return renderUnknownView({
        componentKey,
        componentRole,
        context,
        dataSourceResolver,
        module,
        promptView,
        reason: 'missing-renderer',
        resolutionSource,
        semanticRole,
        view,
        viewId,
      })
    }

    return renderer({
      componentKey,
      componentRole,
      componentSystem,
      context,
      dataSourceResolver,
      designSystem,
      module,
      promptView,
      renderView,
      resolutionSource,
      view,
      viewId,
    })
  }

  return renderView
}

export function mergeProjectedPromptChildViewRendererRegistries<TContext>(
  ...registries: readonly ProjectedPromptChildViewRendererRegistry<TContext>[]
): ProjectedPromptChildViewRendererRegistry<TContext> {
  return {
    byComponentRole: mergeRendererMaps(registries.map((registry) => registry.byComponentRole)),
    byComponentKey: mergeRendererMaps(registries.map((registry) => registry.byComponentKey)),
    bySemanticRole: mergeRendererMaps(registries.map((registry) => registry.bySemanticRole)),
    byViewId: mergeRendererMaps(registries.map((registry) => registry.byViewId)),
    byViewKind: mergeRendererMaps(registries.map((registry) => registry.byViewKind)),
    fallback: [...registries].reverse().find((registry) => registry.fallback)?.fallback,
  }
}

function mergeRendererMaps<TContext>(
  maps: readonly (
    | Readonly<Record<string, ProjectedPromptChildViewRenderer<TContext>>>
    | undefined
  )[],
) {
  const merged: Record<string, ProjectedPromptChildViewRenderer<TContext>> = {}
  for (const map of maps) {
    if (map) {
      Object.assign(merged, map)
    }
  }

  return Object.keys(merged).length > 0 ? merged : undefined
}

export function resolvePromptChildViewRenderer<TContext>({
  componentSet,
  module,
  registry,
  view,
}: {
  readonly componentSet: string
  readonly module: PresentationModuleDefinition
  readonly registry: ProjectedPromptChildViewRendererRegistry<TContext>
  readonly view: ViewDefinition
}): ProjectedPromptChildViewRendererResolution<TContext> {
  const componentBinding = resolvePromptChildViewComponentBinding({
    componentSet,
    module,
    view,
  })
  const componentKey = componentBinding.componentKey
  const componentRole = componentBinding.componentRole
  const semanticRole = getPromptChildViewSemanticRole(view)
  const semanticRoleRenderer = registry.bySemanticRole?.[semanticRole]
  if (semanticRoleRenderer) {
    return {
      componentKey,
      componentRole,
      renderer: semanticRoleRenderer,
      resolutionSource: 'semantic-role',
      semanticRole,
      targetBinding: componentBinding.binding,
    }
  }

  const viewKindRenderer = findViewKindRenderer(registry, view)
  if (viewKindRenderer) {
    return {
      componentKey,
      componentRole,
      renderer: viewKindRenderer,
      resolutionSource: 'view-kind',
      semanticRole,
      targetBinding: componentBinding.binding,
    }
  }

  const componentRoleRenderer = componentRole
    ? registry.byComponentRole?.[componentRole]
    : undefined
  if (componentRoleRenderer) {
    return {
      componentKey,
      componentRole,
      renderer: componentRoleRenderer,
      resolutionSource: 'component-role',
      semanticRole,
      targetBinding: componentBinding.binding,
    }
  }

  const componentRenderer = componentKey
    ? registry.byComponentKey?.[componentKey]
    : undefined
  if (componentRenderer) {
    return {
      componentKey,
      componentRole,
      renderer: componentRenderer,
      resolutionSource: 'component-key',
      semanticRole,
      targetBinding: componentBinding.binding,
    }
  }

  const viewIdRenderer = registry.byViewId?.[view.Id]
  if (viewIdRenderer) {
    return {
      componentKey,
      componentRole,
      renderer: viewIdRenderer,
      resolutionSource: 'view-id',
      semanticRole,
      targetBinding: componentBinding.binding,
    }
  }

  if (registry.fallback) {
    return {
      componentKey,
      componentRole,
      renderer: registry.fallback,
      resolutionSource: 'fallback',
      semanticRole,
      targetBinding: componentBinding.binding,
    }
  }

  return {
    componentKey,
    componentRole,
    renderer: null,
    resolutionSource: null,
    semanticRole,
    targetBinding: componentBinding.binding,
  }
}

function resolvePromptChildViewComponentBinding({
  componentSet,
  module,
  view,
}: {
  readonly componentSet: string
  readonly module: PresentationModuleDefinition
  readonly view: ViewDefinition
}) {
  return resolvePresentationComponentBinding(module, {
    bindingKind: createPresentationEnumDiscriminator(
      presentationBindingKinds,
      'viewComponent',
      'ViewComponent',
    ),
    componentSet,
    id: view.Id,
    targetKind: createPresentationEnumDiscriminator(
      presentationTargetKinds,
      'react',
      'React',
    ),
  })
}

export function projectPromptChildViewRendererDiagnostics<TContext>({
  componentSet = defaultPresentationComponentSet,
  module,
  promptView,
  registry,
  sourceId = `prompt-child-view-renderers.${promptView.Id}`,
}: ProjectPromptChildViewRendererDiagnosticsOptions<TContext>): readonly PresentationProjectionDiagnostic[] {
  return promptView.Regions.flatMap((region) =>
    region.ViewIds.flatMap((viewId) => {
      const view = findPresentationView<ViewDefinition>(module, viewId)
      if (!view) {
        return [
          createPresentationProjectionDiagnostic({
            category: 'missing-definition',
            details: {
              promptViewId: promptView.Id,
              regionId: region.Id,
              viewId,
            },
            id: `prompt-child-view.${promptView.Id}.${viewId}.missing-view`,
            interpretation: {
              status: 'unbound',
              target: 'prompt-child-view-renderer',
            },
            message:
              `Prompt '${promptView.Name}' references child view '${viewId}', ` +
              'but that view is not present in the presentation module.',
            severity: 'warning',
            source: sourceId,
            subject: {
              id: viewId,
              kind: 'prompt-child-view',
            },
          }),
        ]
      }

      const resolution = resolvePromptChildViewRenderer({
        componentSet,
        module,
        registry,
        view,
      })
      const diagnostics: PresentationProjectionDiagnostic[] = []
      const componentRoleBound = resolution.componentRole
        ? Boolean(registry.byComponentRole?.[resolution.componentRole])
        : false

      if (!resolution.targetBinding) {
        diagnostics.push(
          createPresentationProjectionDiagnostic({
            category: 'missing-binding',
            details: {
              promptViewId: promptView.Id,
              regionId: region.Id,
              semanticRole: resolution.semanticRole,
              viewId,
              viewKind: view.Kind,
            },
            id: `prompt-child-view.${promptView.Id}.${viewId}.missing-target-binding`,
            interpretation: {
              status: 'unbound',
              target: 'prompt-child-view-component-role',
            },
            message:
              `Prompt child view '${view.Name}' has no ViewComponent target ` +
              'binding for the active frontend target.',
            severity: 'warning',
            source: sourceId,
            subject: {
              id: view.Id,
              kind: 'prompt-child-view',
              name: view.Name,
            },
            suggestedNextStep:
              'Declare a ViewComponent target binding with ComponentRole for this prompt child view.',
          }),
        )
      }

      if (resolution.componentKey) {
        diagnostics.push(
          createPresentationProjectionDiagnostic({
            category: 'escape-hatch',
            details: createPromptChildViewDiagnosticDetails({
              promptViewId: promptView.Id,
              regionId: region.Id,
              resolution,
              view,
              viewId,
            }),
            id: `prompt-child-view.${promptView.Id}.${viewId}.component-key-escape-hatch`,
            interpretation: {
              status: 'escape-hatch',
              target: 'prompt-child-view-component-key',
            },
            message:
              `Prompt child view '${view.Name}' declares concrete component ` +
              `key '${resolution.componentKey}'.`,
            severity: 'warning',
            source: sourceId,
            subject: {
              id: view.Id,
              kind: 'prompt-child-view',
              name: view.Name,
            },
            suggestedNextStep:
              'Prefer ViewComponent ComponentRole for prompt child views and reserve ComponentKey for adapter-specific overrides.',
          }),
        )
      }

      if (resolution.targetBinding && !resolution.componentRole) {
        diagnostics.push(
          createPresentationProjectionDiagnostic({
            category: 'missing-binding',
            details: createPromptChildViewDiagnosticDetails({
              promptViewId: promptView.Id,
              regionId: region.Id,
              resolution,
              view,
              viewId,
            }),
            id: `prompt-child-view.${promptView.Id}.${viewId}.missing-component-role`,
            interpretation: {
              status: 'unbound',
              target: 'prompt-child-view-component-role',
            },
            message:
              `Prompt child view '${view.Name}' ViewComponent target binding ` +
              'does not declare a component role.',
            severity: 'warning',
            source: sourceId,
            subject: {
              id: view.Id,
              kind: 'prompt-child-view',
              name: view.Name,
            },
            suggestedNextStep:
              'Set ComponentRole on the prompt child ViewComponent target binding.',
          }),
        )
      }

      if (resolution.componentRole) {
        diagnostics.push(
          createPresentationProjectionDiagnostic({
            category: componentRoleBound ? 'local-interpretation' : 'missing-binding',
            details: {
              ...createPromptChildViewDiagnosticDetails({
                promptViewId: promptView.Id,
                regionId: region.Id,
                resolution,
                view,
                viewId,
              }),
              componentRoleBound,
            },
            id: `prompt-child-view.${promptView.Id}.${viewId}.component-role-coverage`,
            interpretation: {
              status: componentRoleBound ? 'locally-interpreted' : 'unbound',
              target: 'prompt-child-view-component-role',
            },
            message: componentRoleBound
              ? `Prompt child view '${view.Name}' component role ` +
                `'${resolution.componentRole}' is interpreted by the local prompt child renderer registry.`
              : `Prompt child view '${view.Name}' component role ` +
                `'${resolution.componentRole}' is not interpreted by the local prompt child renderer registry.`,
            severity: componentRoleBound ? 'info' : 'warning',
            source: sourceId,
            subject: {
              id: view.Id,
              kind: 'prompt-child-view',
              name: view.Name,
            },
            suggestedNextStep: componentRoleBound
              ? undefined
              : `Bind prompt child component role '${resolution.componentRole}' in the frontend registry.`,
          }),
        )
      }

      if (!resolution.renderer) {
        diagnostics.push(
          createPresentationProjectionDiagnostic({
            category: 'missing-binding',
            details: createPromptChildViewDiagnosticDetails({
              promptViewId: promptView.Id,
              regionId: region.Id,
              resolution,
              view,
              viewId,
            }),
            id: `prompt-child-view.${promptView.Id}.${viewId}.missing-renderer`,
            interpretation: {
              status: 'unbound',
              target: 'prompt-child-view-renderer',
            },
            message:
              `Prompt child view '${view.Name}' has no frontend renderer binding.`,
            severity: 'warning',
            source: sourceId,
            subject: {
              id: view.Id,
              kind: 'prompt-child-view',
              name: view.Name,
            },
            suggestedNextStep:
              'Declare a semantic prompt child-view role or bind a standard view-kind renderer.',
          }),
        )
      }

      if (
        resolution.renderer &&
        resolution.resolutionSource === 'view-id'
      ) {
        diagnostics.push(
          createPresentationProjectionDiagnostic({
            category: 'escape-hatch',
            details: createPromptChildViewDiagnosticDetails({
              promptViewId: promptView.Id,
              regionId: region.Id,
              resolution,
              view,
              viewId,
            }),
            id: `prompt-child-view.${promptView.Id}.${viewId}.view-id-escape-hatch`,
            interpretation: {
              status: 'escape-hatch',
              target: 'prompt-child-view-renderer',
            },
            message:
              `Prompt child view '${view.Name}' rendered through ` +
              'view-id as an escape hatch.',
            severity: 'warning',
            source: sourceId,
            subject: {
              id: view.Id,
              kind: 'prompt-child-view',
              name: view.Name,
            },
            suggestedNextStep:
              'Move this prompt child view onto semantic-role, component-role, or view-kind renderer resolution.',
          }),
        )
      }

      return diagnostics
    }))
}

function createPromptChildViewDiagnosticDetails<TContext>({
  promptViewId,
  regionId,
  resolution,
  view,
  viewId,
}: {
  readonly promptViewId: string
  readonly regionId: string
  readonly resolution: ProjectedPromptChildViewRendererResolution<TContext>
  readonly view: ViewDefinition
  readonly viewId: string
}) {
  return {
    componentKey: resolution.componentKey,
    componentRole: resolution.componentRole,
    hasTargetBinding: Boolean(resolution.targetBinding),
    promptViewId,
    regionId,
    resolutionSource: resolution.resolutionSource,
    semanticRole: resolution.semanticRole,
    viewId,
    viewKind: view.Kind,
  }
}

export function getPromptChildViewSemanticRole(view: ViewDefinition) {
  if (view.PromptDocumentPreview) {
    return promptChildViewSemanticRoles.promptDocumentPreview
  }

  const designRole = view.Design?.Role
  if (designRole === 'diff' && view.Design?.Variant === 'json-document') {
    return promptChildViewSemanticRoles.jsonDocumentDiff
  }

  return designRole ?? getPresentationViewSemanticRole(view)
}

function findViewKindRenderer<TContext>(
  registry: ProjectedPromptChildViewRendererRegistry<TContext>,
  view: ViewDefinition,
) {
  for (const key of getViewKindKeys(view.Kind)) {
    const renderer = registry.byViewKind?.[key]
    if (renderer) {
      return renderer
    }
  }

  return undefined
}

function getViewKindKeys(kind: ViewDefinition['Kind']) {
  const keys = [String(kind)]
  const label = viewKindLabels[kind as ViewKind]
  if (label) {
    keys.push(label)
    keys.push(label.charAt(0).toLowerCase() + label.slice(1))
  }

  return keys
}

function renderDefaultUnknownPromptChildView<TContext>({
  componentKey,
  componentRole,
  reason,
  resolutionSource,
  semanticRole,
  view,
  viewId,
}: ProjectedPromptUnknownChildViewRenderContext<TContext>) {
  if (reason === 'missing-view') {
    return <ProjectedStatusBlock label={`Prompt child view '${viewId}' is not available.`} />
  }

  return (
    <ProjectedStatusBlock
      label={`Prompt child view '${view?.Name ?? viewId}' has no renderer for component role '${componentRole ?? 'none'}', component key '${componentKey ?? 'none'}', role '${semanticRole ?? 'unknown'}', and source '${resolutionSource ?? 'none'}'.`}
    />
  )
}
