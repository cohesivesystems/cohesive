import type {
  PresentationModuleDefinition,
} from '@cohesivesystems/presentation-core'
import type {
  DocumentWorkspaceProjectionRendererRegistry,
  DocumentWorkspaceProjectionRendererResolution,
  DocumentWorkspaceRuntimeSnapshot,
} from './document-workspace-runtime'
import {
  resolveDocumentWorkspaceProjectionCapability,
  resolveDocumentWorkspaceProjectionRenderer,
} from './document-workspace-runtime'
import type {
  PresentationProjectionDiagnostic,
} from '@cohesivesystems/presentation-core'
import {
  createPresentationEnumDiscriminator,
  matchesPresentationEnum,
} from '@cohesivesystems/presentation-core'
import {
  coordinationActionKinds,
  coordinationTriggerKinds,
} from '@cohesivesystems/presentation-contracts'

export interface ProjectDocumentWorkspaceProjectionRendererDiagnosticsOptions<
  TContext,
  TComponentSystem = unknown,
  TDesignSystem = unknown,
> {
  readonly componentSet?: string | null
  readonly module: PresentationModuleDefinition | null
  readonly projectionRenderers: DocumentWorkspaceProjectionRendererRegistry<
    TContext,
    TComponentSystem,
    TDesignSystem
  >
  readonly runtime: DocumentWorkspaceRuntimeSnapshot
  readonly sourceId?: string
}

/**
 * Reports projection renderer coverage for the document views exposed by the
 * active workspace runtime. The same semantic resolver used for rendering is
 * used here so the developer toolbar reports stale component bindings and
 * unhandled IR projection semantics as actionable frontend TODOs.
 */
export function projectDocumentWorkspaceProjectionRendererDiagnostics<
  TContext,
  TComponentSystem = unknown,
  TDesignSystem = unknown,
>({
  componentSet,
  module,
  projectionRenderers,
  runtime,
  sourceId = 'document-workspace-projection-renderers',
}: ProjectDocumentWorkspaceProjectionRendererDiagnosticsOptions<
  TContext,
  TComponentSystem,
  TDesignSystem
>): readonly PresentationProjectionDiagnostic[] {
  return [
    ...runtime.projectionViewIds.flatMap((viewId) =>
      projectProjectionRendererDiagnostics({
        componentSet,
        module,
        projectionRenderers,
        runtime,
        sourceId,
        viewId,
      }),
    ),
    ...projectProjectionCoordinationDiagnostics({
      componentSet,
      module,
      projectionRenderers,
      runtime,
      sourceId,
    }),
  ]
}

function projectProjectionRendererDiagnostics<
  TContext,
  TComponentSystem = unknown,
  TDesignSystem = unknown,
>({
  componentSet,
  module,
  projectionRenderers,
  runtime,
  sourceId,
  viewId,
}: ProjectDocumentWorkspaceProjectionRendererDiagnosticsOptions<
  TContext,
  TComponentSystem,
  TDesignSystem
> & {
  readonly viewId: string
}) {
  const resolution = resolveDocumentWorkspaceProjectionRenderer({
    componentSet,
    module,
    registry: projectionRenderers,
    runtime,
    viewId,
  })
  const projection = resolution.projection

  if (!projection) {
    return [
      {
        details: { viewId },
        id: `document-projection.${viewId}.missing-projection`,
        message:
          `Document workspace view '${viewId}' does not resolve to a declared ` +
          'document projection.',
        severity: 'warning',
        source: sourceId ?? 'document-workspace-projection-renderers',
        subject: {
          id: viewId,
          kind: 'document-projection-view',
        },
      } satisfies PresentationProjectionDiagnostic,
    ]
  }

  const diagnostics: PresentationProjectionDiagnostic[] = []
  if (!resolution.renderer) {
    diagnostics.push({
      details: createProjectionRendererDiagnosticDetails(resolution, viewId),
      id: `document-projection.${projection.Id}.missing-renderer`,
      message: `Document projection '${projection.Name}' has no frontend renderer binding.`,
      severity: 'warning',
      source: sourceId ?? 'document-workspace-projection-renderers',
      subject: {
        id: projection.Id,
        kind: 'document-projection',
        name: projection.Name,
      },
    })
  }

  if (resolution.renderer && isCompatibilityRendererSource(resolution.resolutionSource)) {
    diagnostics.push({
      details: createProjectionRendererDiagnosticDetails(resolution, viewId),
      id: `document-projection.${projection.Id}.semantic-renderer-fallback`,
      category: resolution.componentRole ? 'unbound' : 'missing-binding',
      interpretation: {
        status: resolution.componentRole ? 'unbound' : 'locally-interpreted',
        target: 'document-projection-component-role',
      },
      message: resolution.componentRole
        ? `Document projection '${projection.Name}' declares component role ` +
          `'${resolution.componentRole}', but rendering fell back to ` +
          `'${resolution.resolutionSource}'.`
        : `Document projection '${projection.Name}' rendered through ` +
          `'${resolution.resolutionSource}' because it has no ProjectionRenderer ` +
          'target component role binding.',
      severity: 'warning',
      source: sourceId ?? 'document-workspace-projection-renderers',
      suggestedNextStep: resolution.componentRole
        ? `Bind frontend component role '${resolution.componentRole}' in the document projection component pack.`
        : 'Declare a ProjectionRenderer target binding with ComponentRole for this projection and bind that role in the frontend component pack.',
      subject: {
        id: projection.Id,
        kind: 'document-projection',
        name: projection.Name,
      },
    })
  }

  if (
    resolution.renderer &&
    resolution.componentKey &&
    resolution.resolutionSource === 'component-key'
  ) {
    diagnostics.push({
      category: 'escape-hatch',
      details: createProjectionRendererDiagnosticDetails(resolution, viewId),
      id: `document-projection.${projection.Id}.component-key-escape-hatch`,
      interpretation: {
        status: 'escape-hatch',
        target: 'document-projection-component-key',
      },
      message:
        `Document projection '${projection.Name}' rendered through concrete ` +
        `component key '${resolution.componentKey}' as an escape hatch.`,
      severity: 'warning',
      source: sourceId ?? 'document-workspace-projection-renderers',
      suggestedNextStep:
        'Prefer ComponentRole for document projections and keep ComponentKey only for adapter-specific overrides.',
      subject: {
        id: projection.Id,
        kind: 'document-projection',
        name: projection.Name,
      },
    })
  }

  if (resolution.rendererKey) {
    diagnostics.push({
      category: 'escape-hatch',
      details: createProjectionRendererDiagnosticDetails(resolution, viewId),
      id: `document-projection.${projection.Id}.legacy-renderer-key`,
      interpretation: {
        status: 'escape-hatch',
        target: 'document-projection-component-role',
      },
      message:
        `Document projection '${projection.Name}' declares renderer key ` +
        `'${resolution.rendererKey}'. Document projection dispatch now uses ` +
        'ProjectionRenderer target component roles and semantic projection interpretation.',
      severity: 'warning',
      source: sourceId ?? 'document-workspace-projection-renderers',
      suggestedNextStep:
        'Replace the renderer key with a ProjectionRenderer target binding that declares ComponentRole.',
      subject: {
        id: projection.Id,
        kind: 'document-projection',
        name: projection.Name,
      },
    })
  }

  return diagnostics
}

function isCompatibilityRendererSource(
  source: DocumentWorkspaceProjectionRendererResolution<
    unknown,
    unknown,
    unknown
  >['resolutionSource'],
) {
  return source === 'projection-kind' ||
    source === 'semantic-reference-kind' ||
    source === 'subject-kind'
}

function projectProjectionCoordinationDiagnostics<
  TContext,
  TComponentSystem = unknown,
  TDesignSystem = unknown,
>({
  componentSet,
  module,
  projectionRenderers,
  runtime,
  sourceId = 'document-workspace-projection-renderers',
}: ProjectDocumentWorkspaceProjectionRendererDiagnosticsOptions<
  TContext,
  TComponentSystem,
  TDesignSystem
>) {
  const revealTargetProjectionIds = new Set(
    runtime.documentProfile.Coordination
      .filter((coordination) =>
        matchesPresentationEnum(coordination.Trigger.Kind, selectionChangedTriggerKind) &&
        matchesPresentationEnum(coordination.Action.Kind, revealSemanticSelectionActionKind),
      )
      .flatMap((coordination) => coordination.Action.TargetProjectionIds),
  )

  return [...revealTargetProjectionIds].flatMap((projectionId) => {
    const projection = runtime.projections.find((candidate) => candidate.Id === projectionId)
    if (!projection) {
      return []
    }

    const hasRevealCapability = resolveDocumentWorkspaceProjectionCapability({
      capability: 'revealSemanticSelection',
      componentSet,
      module,
      projection,
      registry: projectionRenderers,
    })
    if (hasRevealCapability) {
      return []
    }

    return [
      {
        details: {
          coordinationActionKind: 'RevealSemanticSelection',
          projectionKind: projection.Kind,
          semanticReferenceKind: projection.Coordinates?.SemanticReferenceKind ?? null,
          subjectKind: projection.Subject.Kind,
          viewId: projection.ViewId,
        },
        id: `document-projection.${projection.Id}.missing-reveal-semantic-selection`,
        message:
          `Document projection '${projection.Name}' is targeted by semantic ` +
          'selection reveal coordination, but this frontend adapter has not ' +
          'declared a reveal interpretation for it.',
        severity: 'warning',
        source: sourceId,
        subject: {
          id: projection.Id,
          kind: 'document-projection',
          name: projection.Name,
        },
      } satisfies PresentationProjectionDiagnostic,
    ]
  })
}

function createProjectionRendererDiagnosticDetails<
  TContext,
  TComponentSystem,
  TDesignSystem,
>(
  resolution: DocumentWorkspaceProjectionRendererResolution<
    TContext,
    TComponentSystem,
    TDesignSystem
  >,
  viewId: string,
) {
  return {
    componentKey: resolution.componentKey,
    componentRole: resolution.componentRole,
    projectionKind: resolution.projection?.Kind ?? null,
    rendererKey: resolution.rendererKey,
    resolutionSource: resolution.resolutionSource,
    semanticReferenceKind: resolution.projection?.Coordinates?.SemanticReferenceKind ?? null,
    subjectKind: resolution.projection?.Subject.Kind ?? null,
    viewId,
  }
}

const selectionChangedTriggerKind = createPresentationEnumDiscriminator(
  coordinationTriggerKinds,
  'selectionChanged',
  'SelectionChanged',
)

const revealSemanticSelectionActionKind = createPresentationEnumDiscriminator(
  coordinationActionKinds,
  'revealSemanticSelection',
  'RevealSemanticSelection',
)
