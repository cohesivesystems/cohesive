import type {
  ActionDefinition,
  ActionPlacementDefinition,
  CollectionChromeSlotDefinition,
  CollectionRowActionDefinition,
  CollectionSelectionActionDefinition,
  InputFormDefinition,
  PresentationModuleDefinition,
  PresentationValueDefinition,
  QueryFormDefinition,
  ViewChromeSlotDefinition,
  ViewDefinition,
} from './module'
import {
  findPresentationAction,
  findPresentationDataSource,
  findPresentationField,
  findPresentationFlow,
  findPresentationInputForm,
  findPresentationQueryForm,
  findPresentationView,
} from './module'
import type {
  PresentationDataSourceTargetInterpretation,
} from './data-source-projection'
import type {
  PresentationDataSourceBinding,
} from './presentation-data-source-binding-model'
import {
  presentationDataSourceBindingKinds,
} from './presentation-data-source-binding-model'
import {
  getCollectionChromeSlotRendererCandidateKeys,
  hasCollectionChromeSlotRendererBinding,
} from './collection-chrome-slot-renderer-registry'
import {
  getViewChromeSlotRendererCandidateKeys,
  hasViewChromeSlotRendererBinding,
  isViewChromeSlotKind,
} from './view-chrome-slot-renderer-registry'
import {
  createCollectionChromeRuntime,
  isCollectionChromeSlotKind,
  isCollectionChromeSlotPlacement,
} from './collection-chrome-runtime'
import {
  getPresentationViewProjectedActions,
  getPresentationViewProjectedFieldRefs,
  type PresentationViewProjectedActionRef,
} from './presentation-semantics'
import type {
  PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'
import {
  collectionChromeProjectionModes,
  isCollectionChromeSlotProjectionMode,
  readCollectionChromeProjectionMode,
  resolveProjectedCollectionChromeSlots,
} from './projected-collection-runtime'
import type {
  PresentationProjectionTrace,
  PresentationProjectionTraceView,
} from './presentation-projection-trace'
import {
  actionKinds,
  collectionChromeSlotKinds,
  collectionChromeSlotPlacements,
  collectionDetailActivations,
  collectionRowActionKinds,
  collectionSelectionActionParameterSources,
  collectionSelectionModes,
  dataSourceKinds,
  presentationValueKinds,
  pageHostComponentRoles,
  presentationBindingKinds,
  viewKinds,
  viewChromeSlotKinds,
} from '@cohesive/presentation-contracts'

export interface ProjectPresentationTraceCoverageDiagnosticsOptions {
  readonly collectionChromeSlotRendererKeys?: readonly string[]
  readonly collectionSelectionStateRuntimeBound?: boolean
  readonly module: PresentationProjectionCoverageModule | null
  readonly queryFormStateAdapterIds?: readonly string[]
  readonly trace: PresentationProjectionTrace
  readonly viewChromeSlotRendererKeys?: readonly string[]
}

export interface ProjectPresentationDataSourceCoverageDiagnosticsOptions {
  readonly bindings: readonly PresentationDataSourceBinding[]
  readonly dataSourceIds: readonly string[]
  readonly module: Pick<PresentationModuleDefinition, 'DataSources'> | null
  readonly routeParameters?: Readonly<Record<string, string | undefined>>
  readonly sourceId: string
  readonly targetInterpretation?: Pick<PresentationDataSourceTargetInterpretation, 'queryLowering'>
}

export type PresentationProjectionCoverageModule =
  {
    readonly Actions?: readonly PresentationModuleDefinition['Actions'][number][]
    readonly DataSources?: readonly PresentationModuleDefinition['DataSources'][number][]
    readonly Fields?: readonly PresentationModuleDefinition['Fields'][number][]
    readonly Flows?: readonly PresentationModuleDefinition['Flows'][number][]
    readonly InputForms?: readonly PresentationModuleDefinition['InputForms'][number][]
    readonly QueryForms?: readonly PresentationModuleDefinition['QueryForms'][number][]
    readonly Views: readonly PresentationModuleDefinition['Views'][number][]
  }

/**
 * Checks active route, page-host, view renderer, action, and input-form coverage
 * using the same semantic trace that the developer toolbar displays.
 */
export function projectPresentationTraceCoverageDiagnostics({
  collectionChromeSlotRendererKeys = [],
  collectionSelectionStateRuntimeBound = false,
  module,
  queryFormStateAdapterIds = [],
  trace,
  viewChromeSlotRendererKeys = [],
}: ProjectPresentationTraceCoverageDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  return [
    ...projectRouteCoverageDiagnostics(trace),
    ...projectViewRendererCoverageDiagnostics(trace),
    ...projectViewModelCoverageDiagnostics(
      module,
      trace,
      queryFormStateAdapterIds,
      collectionSelectionStateRuntimeBound,
      collectionChromeSlotRendererKeys,
      viewChromeSlotRendererKeys,
    ),
  ]
}

/** Checks concrete data-source bindings projected for a routed surface. */
export function projectPresentationDataSourceCoverageDiagnostics({
  bindings,
  dataSourceIds,
  module,
  routeParameters = {},
  sourceId,
  targetInterpretation,
}: ProjectPresentationDataSourceCoverageDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  const bindingByDataSourceId = new Map(bindings.map((binding) => [binding.dataSourceId, binding]))

  return Array.from(new Set(dataSourceIds)).flatMap((dataSourceId) => {
    const dataSource = findPresentationDataSource(module, dataSourceId)
    if (!dataSource) {
      return [
        createDiagnostic({
          id: `data-source.${dataSourceId}.missing-definition`,
          message: `Data source '${dataSourceId}' is referenced by the active surface but is not present in the presentation module.`,
          severity: 'error',
          source: sourceId,
          subject: {
            id: dataSourceId,
            kind: 'data-source',
          },
        }),
      ]
    }

    const binding = bindingByDataSourceId.get(dataSourceId)
    if (!binding) {
      return [
        createDiagnostic({
          category: 'missing-binding',
          id: `data-source.${dataSourceId}.missing-binding`,
          interpretation: {
            status: 'unbound',
            target: 'data-source-binding',
          },
          message: `Data source '${dataSource.Name}' has no projected frontend binding.`,
          severity: 'warning',
          source: sourceId,
          subject: {
            id: dataSource.Id,
            kind: 'data-source',
            name: dataSource.Name,
          },
          suggestedNextStep:
            'Register a data-source binding factory or add a generic target interpreter for this data source kind.',
        }),
      ]
    }

    const diagnostics: PresentationProjectionDiagnostic[] = []
    diagnostics.push(
      ...projectDataSourceQueryLoweringTransformDiagnostics({
        dataSource,
        sourceId,
        targetInterpretation,
      }),
    )

    if (isUnboundDataSourceBinding(binding)) {
      diagnostics.push(createDiagnostic({
        category: 'missing-binding',
        details: { bindingKind: binding.kind },
        id: `data-source.${dataSource.Id}.unbound-frontend-binding`,
        interpretation: {
          status: 'unbound',
          target: 'data-source-binding-factory',
        },
        message: `Data source '${dataSource.Name}' is declared by the IR but has no frontend binding factory.`,
        severity: 'warning',
        source: sourceId,
        subject: {
          id: dataSource.Id,
          kind: 'data-source',
          name: dataSource.Name,
        },
        suggestedNextStep:
          'Bind this data source through backend-declared query lowering or an explicit frontend adapter.',
      }))
    }

    const missingRequiredParameters = dataSource.Parameters.filter((parameter) =>
      parameter.IsRequired &&
      !parameter.DefaultValue &&
      !routeParameters[parameter.Name])
    if (
      missingRequiredParameters.length > 0 &&
      binding.kind === presentationDataSourceBindingKinds.tanstackQuery &&
      binding.enabled === false
    ) {
      diagnostics.push(createDiagnostic({
        details: {
          missingParameters: missingRequiredParameters.map((parameter) => parameter.Name),
        },
        id: `data-source.${dataSource.Id}.missing-route-parameters`,
        message:
          `Data source '${dataSource.Name}' is bound but disabled because required ` +
          `route parameter(s) are missing: ${missingRequiredParameters
            .map((parameter) => parameter.Name)
            .join(', ')}.`,
        severity: 'warning',
        source: sourceId,
        subject: {
          id: dataSource.Id,
          kind: 'data-source',
          name: dataSource.Name,
        },
      }))
    }

    return diagnostics
  })
}

function projectRouteCoverageDiagnostics(
  trace: PresentationProjectionTrace,
): readonly PresentationProjectionDiagnostic[] {
  const diagnostics: PresentationProjectionDiagnostic[] = []
  if (!trace.moduleAvailable) {
    diagnostics.push(createDiagnostic({
      id: 'route.missing-module',
      message: 'Presentation module is not available for the active route.',
      severity: 'error',
      source: 'presentation-route-coverage',
      subject: {
        id: trace.pathname,
        kind: 'route',
      },
    }))
  }

  if (!trace.route) {
    diagnostics.push(createDiagnostic({
      id: `route.${trace.pathname}.unmatched`,
      message: `No presentation route matched '${trace.pathname}'.`,
      severity: 'error',
      source: 'presentation-route-coverage',
      subject: {
        id: trace.pathname,
        kind: 'route',
      },
    }))
    return diagnostics
  }

  if (!trace.pageHost) {
    diagnostics.push(createDiagnostic({
      id: `route.${trace.route.id}.missing-page-host`,
      message: `Route '${trace.route.id}' resolves to page host '${trace.route.pageHostId}', but the page host is not available.`,
      severity: 'error',
      source: 'presentation-route-coverage',
      subject: {
        id: trace.route.id,
        kind: 'route',
        name: trace.route.label,
      },
    }))
  } else if (!trace.pageHostRenderer?.resolutionSource) {
    diagnostics.push(createDiagnostic({
      category: 'missing-binding',
      details: {
        componentKey: trace.pageHostRenderer?.componentKey ?? null,
        componentRole: trace.pageHostRenderer?.componentRole ?? null,
        rendererKey: trace.pageHostRenderer?.rendererKey ?? null,
        semanticRole: trace.pageHostRenderer?.semanticRole ?? null,
        targetBindingSource: trace.pageHostRenderer?.targetBindingSource ?? null,
      },
      id: `page-host.${trace.pageHost.id}.missing-renderer`,
      interpretation: {
        status: 'unbound',
        target: 'page-host-renderer',
      },
      message: `Page host '${trace.pageHost.id}' has no frontend renderer binding.`,
      severity: 'warning',
      source: 'presentation-route-coverage',
      subject: {
        id: trace.pageHost.id,
        kind: 'page-host',
      },
      suggestedNextStep:
        'Add a page-host renderer binding or project this host through the generic routed-surface interpreter.',
    }))
  } else if (
    trace.pageHostRenderer?.resolutionSource &&
    isPageHostRendererEscapeHatchSource(trace.pageHostRenderer.resolutionSource)
  ) {
    diagnostics.push(createDiagnostic({
      category: 'escape-hatch',
      details: {
        componentKey: trace.pageHostRenderer.componentKey,
        componentRole: trace.pageHostRenderer.componentRole,
        rendererKey: trace.pageHostRenderer.rendererKey,
        resolutionSource: trace.pageHostRenderer.resolutionSource,
        semanticRole: trace.pageHostRenderer.semanticRole,
        targetBindingSource: trace.pageHostRenderer.targetBindingSource,
      },
      id: `page-host.${trace.pageHost.id}.${trace.pageHostRenderer.resolutionSource}-escape-hatch`,
      interpretation: {
        status: 'escape-hatch',
        target: 'page-host-renderer',
      },
      message:
        `Page host '${trace.pageHost.id}' rendered through ` +
        `'${trace.pageHostRenderer.resolutionSource}' as an escape hatch.`,
      severity: 'warning',
      source: 'presentation-route-coverage',
      subject: {
        id: trace.pageHost.id,
        kind: 'page-host',
      },
      suggestedNextStep:
        'Move this page host onto ComponentRole, workspace, root-view semantic role, or view kind resolution.',
    }))
  }

  if (trace.pageHost && trace.pageHostRenderer?.rendererKey) {
    diagnostics.push(createDiagnostic({
      category: 'escape-hatch',
      details: {
        rendererKey: trace.pageHostRenderer.rendererKey,
        resolutionSource: trace.pageHostRenderer.resolutionSource,
        targetBindingSource: trace.pageHostRenderer.targetBindingSource,
      },
      id: `page-host.${trace.pageHost.id}.legacy-renderer-key`,
      interpretation: {
        status: 'escape-hatch',
        target: 'page-host-component-role',
      },
      message:
        `Page host '${trace.pageHost.id}' declares legacy renderer key ` +
        `'${trace.pageHostRenderer.rendererKey}'. Page-host dispatch now uses ` +
        'component roles and semantic target interpretation.',
      severity: 'warning',
      source: 'presentation-route-coverage',
      subject: {
        id: trace.pageHost.id,
        kind: 'page-host',
      },
      suggestedNextStep:
        'Replace the renderer key with a PageHostComponent target binding that declares ComponentRole.',
    }))
  }

  return diagnostics
}

function isPageHostRendererEscapeHatchSource(source: string) {
  return source === 'component-key' ||
    source === 'page-host-id' ||
    source === 'route-id' ||
    source === 'view-id' ||
    source === 'workspace-id'
}

function projectDataSourceQueryLoweringTransformDiagnostics({
  dataSource,
  sourceId,
  targetInterpretation,
}: {
  readonly dataSource: PresentationModuleDefinition['DataSources'][number]
  readonly sourceId: string
  readonly targetInterpretation?: Pick<PresentationDataSourceTargetInterpretation, 'queryLowering'>
}): readonly PresentationProjectionDiagnostic[] {
  const usedTransformIds = collectDataSourceQueryLoweringTransformIds(dataSource)
  if (usedTransformIds.length === 0) {
    return []
  }

  const boundTransformIds = new Set(
    Object.keys(targetInterpretation?.queryLowering?.transformsById ?? {}),
  )
  return usedTransformIds
    .filter((transformId) => !boundTransformIds.has(transformId))
    .map((transformId) =>
      createDiagnostic({
        category: 'unbound',
        details: {
          transformId,
        },
        id: `data-source.${dataSource.Id}.query-transform.${transformId}.unbound`,
        interpretation: {
          status: 'unbound',
          target: 'query-lowering-transform',
        },
        message:
          `Data source '${dataSource.Name}' uses query lowering transform ` +
          `'${transformId}', but the active frontend target interpretation does not bind it.`,
        severity: 'warning',
        source: sourceId,
        subject: {
          id: dataSource.Id,
          kind: 'data-source',
          name: dataSource.Name,
        },
        suggestedNextStep:
          'Bind this transform id on the data-source target interpretation or replace it with a projected expression.',
      }),
    )
}

function collectDataSourceQueryLoweringTransformIds(
  dataSource: PresentationModuleDefinition['DataSources'][number],
) {
  const transformIds = new Set<string>()
  for (const endpointBinding of dataSource.Query?.EndpointBindings ?? []) {
    for (const lowering of endpointBinding.Lowerings) {
      for (const fieldBinding of lowering.FieldBindings) {
        const transform = fieldBinding.Transform?.trim()
        if (transform) {
          transformIds.add(transform)
        }
      }
    }
  }

  return Array.from(transformIds).sort()
}

function projectViewRendererCoverageDiagnostics(
  trace: PresentationProjectionTrace,
): readonly PresentationProjectionDiagnostic[] {
  return trace.views.flatMap((view) => {
    if (isViewRendererCoverageOwnedByDocumentWorkspaceHost(trace, view)) {
      return []
    }

    if (!view.rendererResolved) {
      return [
        createViewDiagnostic({
          category: 'missing-binding',
          details: {
            componentKey: view.componentKey,
            componentRole: view.componentRole,
            semanticRole: view.semanticRole,
            viewKind: view.kind,
          },
          interpretation: {
            status: 'unbound',
            target: 'view-renderer',
          },
          message: `View '${view.name}' has no frontend renderer binding.`,
          reason: 'missing-renderer',
          severity: 'warning',
          source: 'presentation-view-coverage',
          suggestedNextStep:
            'Add a semantic renderer binding or expand the standard view interpreter for this view kind.',
          view,
        }),
      ]
    }

    if (view.componentKey && view.resolutionSource === 'component-key') {
      return [
        createViewDiagnostic({
          category: 'escape-hatch',
          details: {
            componentKey: view.componentKey,
            componentRole: view.componentRole,
            resolutionSource: view.resolutionSource,
          },
          interpretation: {
            status: 'escape-hatch',
            target: 'view-renderer',
          },
          message:
            `View '${view.name}' rendered through concrete component key ` +
            `'${view.componentKey}' as an escape hatch.`,
          reason: 'component-key-escape-hatch',
          severity: 'warning',
          source: 'presentation-view-coverage',
          suggestedNextStep:
            'Prefer ComponentRole or semantic renderer resolution; keep ComponentKey only for adapter-specific overrides.',
          view,
        }),
      ]
    }

    return []
  })
}

function isViewRendererCoverageOwnedByDocumentWorkspaceHost(
  trace: PresentationProjectionTrace,
  view: PresentationProjectionTraceView,
) {
  const pageHostComponentRole = trace.pageHostRenderer?.componentRole
  const isDocumentWorkspaceHost =
    pageHostComponentRole === pageHostComponentRoles.documentWorkspace ||
    Boolean(trace.pageHost?.documentProfileId)

  if (!isDocumentWorkspaceHost) {
    return false
  }

  return (
    view.semanticRole === 'workspace-view' ||
    view.semanticRole === 'document-view' ||
    isTraceViewKind(view, viewKinds.documentWorkspace, 'DocumentWorkspace') ||
    isTraceViewKind(view, viewKinds.panel, 'Panel')
  )
}

function isTraceViewKind(
  view: PresentationProjectionTraceView,
  kind: string | number,
  label: string,
) {
  return view.kind === String(kind) || view.kind === label
}

function projectViewModelCoverageDiagnostics(
  module: PresentationProjectionCoverageModule | null,
  trace: PresentationProjectionTrace,
  queryFormStateAdapterIds: readonly string[],
  collectionSelectionStateRuntimeBound: boolean,
  collectionChromeSlotRendererKeys: readonly string[],
  viewChromeSlotRendererKeys: readonly string[],
): readonly PresentationProjectionDiagnostic[] {
  if (!module) {
    return []
  }
  const coverageModule = normalizeCoverageModule(module)

  return trace.views.flatMap((viewTrace) => {
    const view = findPresentationView<ViewDefinition>(coverageModule, viewTrace.id)
    if (!view) {
      return [
        createViewDiagnostic({
          message: `View '${viewTrace.name}' is in the active trace but is not present in the presentation module.`,
          reason: 'missing-definition',
          severity: 'error',
          source: 'presentation-view-coverage',
          view: viewTrace,
        }),
      ]
    }

    return [
      ...projectViewChromeCoverageDiagnostics(
        coverageModule,
        view,
        viewTrace,
        viewChromeSlotRendererKeys,
      ),
      ...projectCollectionCoverageDiagnostics(
        coverageModule,
        view,
        viewTrace,
        collectionSelectionStateRuntimeBound,
        collectionChromeSlotRendererKeys,
      ),
      ...projectFieldCoverageDiagnostics(coverageModule, view, viewTrace),
      ...projectActionCoverageDiagnostics(coverageModule, view, viewTrace),
      ...projectInputFormCoverageDiagnostics(
        coverageModule,
        view,
        viewTrace,
        queryFormStateAdapterIds,
      ),
    ]
  })
}

function projectFieldCoverageDiagnostics(
  module: PresentationModuleDefinition,
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
): readonly PresentationProjectionDiagnostic[] {
  return resolveViewProjectedFieldRefs(view).flatMap(({ fieldId, source }) => {
    const field = findPresentationField(module, fieldId)
    return field
      ? []
      : [
          createViewDiagnostic({
            details: { fieldId, source },
            message: `View '${view.Name}' references field '${fieldId}' from ${source}, but the field is not present in the presentation module.`,
            reason: `missing-field.${source}.${fieldId}`,
            severity: 'error',
            source: 'presentation-view-coverage',
            view: viewTrace,
          }),
        ]
  })
}

function projectViewChromeCoverageDiagnostics(
  module: PresentationModuleDefinition,
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
  viewChromeSlotRendererKeys: readonly string[],
): readonly PresentationProjectionDiagnostic[] {
  return [
    ...(view.Chrome?.Slots ?? []).flatMap((slot) => [
      ...projectViewChromeSlotReferenceDiagnostics(module, view, viewTrace, slot),
      ...projectViewChromeSlotInterpretationDiagnostics(
        view,
        viewTrace,
        slot,
        viewChromeSlotRendererKeys,
      ),
    ]),
    ...projectPromptStatusChromeCoverageDiagnostics(view, viewTrace),
    ...projectPromptStatusMessageContentDiagnostics(view, viewTrace),
    ...projectPromptDocumentPreviewContentDiagnostics(view, viewTrace),
    ...projectPromptDocumentPreviewBadgeContentDiagnostics(module, view, viewTrace),
  ]
}

function projectPromptStatusChromeCoverageDiagnostics(
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
): readonly PresentationProjectionDiagnostic[] {
  const statusMessages = view.PromptStatusMessages ?? []
  if (statusMessages.length === 0) {
    return []
  }

  const statusSlotRegionIds = new Set(
    (view.Chrome?.Slots ?? [])
      .filter((slot) => isViewChromeSlotKind(slot, viewChromeSlotKinds.status))
      .map((slot) => slot.StateId ?? slot.Id),
  )
  const messageRegionIds = Array.from(new Set(
    statusMessages.map((message) => message.Region).filter(Boolean),
  ))

  return messageRegionIds
    .filter((regionId) => !statusSlotRegionIds.has(regionId))
    .map((regionId) =>
      createViewDiagnostic({
        category: 'missing-binding',
        details: { promptStatusRegionId: regionId },
        interpretation: {
          status: 'unbound',
          target: 'view-chrome-status-slot',
        },
        message:
          `Prompt view '${view.Name}' declares status messages for region ` +
          `'${regionId}', but no Status chrome slot consumes that region.`,
        reason: `unbound-prompt-status-region.${regionId}`,
        severity: 'warning',
        source: 'presentation-view-coverage',
        suggestedNextStep:
          'Declare a View.Chrome Status slot whose id or state id matches this prompt status region.',
        view: viewTrace,
      }))
}

function projectPromptStatusMessageContentDiagnostics(
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
): readonly PresentationProjectionDiagnostic[] {
  return (view.PromptStatusMessages ?? []).flatMap((message) => {
    const hasLegacyMessage = Boolean(message.Message)
    const hasLegacyTemplate = Boolean(message.MessageTemplate)
    if (!hasLegacyMessage && !hasLegacyTemplate) {
      return []
    }

    const content = message.Content
    if (!content) {
      return [
        createViewDiagnostic({
          category: 'incomplete-projection',
          details: {
            promptStatusMessageId: message.Id,
            promptStatusMessageName: message.Name,
            region: message.Region,
          },
          interpretation: {
            status: 'locally-interpreted',
            target: 'prompt-status-message-content',
          },
          message:
            `Prompt status message '${message.Name}' on view '${view.Name}' still declares ` +
            'legacy message fields without Content; the frontend will use legacy text fallback semantics.',
          reason: `legacy-prompt-status-content.${message.Id}`,
          severity: 'warning',
          source: 'presentation-view-coverage',
          suggestedNextStep:
            'Declare PromptStatusMessage.Content with PresentationContentDefinition and remove legacy Message/MessageTemplate fallback fields.',
          view: viewTrace,
        }),
      ]
    }

    const hasContentLabel = Boolean(
      content.Description ??
      content.DescriptionTemplate ??
      content.Title ??
      content.Subtitle,
    )
    return hasContentLabel
      ? []
      : [
          createViewDiagnostic({
            category: 'incomplete-projection',
            details: {
              promptStatusMessageId: message.Id,
              promptStatusMessageName: message.Name,
              region: message.Region,
            },
            interpretation: {
              status: 'locally-interpreted',
              target: 'prompt-status-message-content',
            },
            message:
              `Prompt status message '${message.Name}' on view '${view.Name}' declares Content ` +
              'without label content; the frontend will use legacy text fallback semantics.',
            reason: `legacy-prompt-status-content-label.${message.Id}`,
            severity: 'warning',
            source: 'presentation-view-coverage',
            suggestedNextStep:
              'Declare PromptStatusMessage.Content title, subtitle, description, or description template.',
            view: viewTrace,
          }),
        ]
  })
}

function projectPromptDocumentPreviewContentDiagnostics(
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
): readonly PresentationProjectionDiagnostic[] {
  const preview = view.PromptDocumentPreview
  if (!preview) {
    return []
  }

  if (!preview.Content) {
    return [
      createViewDiagnostic({
        category: 'incomplete-projection',
        details: {
          previewViewId: preview.PreviewViewId,
          workspacePageViewId: preview.WorkspacePageViewId,
        },
        interpretation: {
          status: 'locally-interpreted',
          target: 'prompt-document-preview-content',
        },
        message:
          `Prompt document preview on view '${view.Name}' still declares legacy title/document text ` +
          'without Content; the frontend will use legacy preview content fallback semantics.',
        reason: 'legacy-prompt-document-preview-content',
        severity: 'warning',
        source: 'presentation-view-coverage',
        suggestedNextStep:
          'Declare PromptDocumentPreview.Content with PresentationContentDefinition and remove legacy Title/DocumentText fallback fields.',
        view: viewTrace,
      }),
    ]
  }

  const diagnostics: PresentationProjectionDiagnostic[] = []
  if (!preview.Content.Title) {
    diagnostics.push(
      createViewDiagnostic({
        category: 'incomplete-projection',
        details: {
          previewViewId: preview.PreviewViewId,
          workspacePageViewId: preview.WorkspacePageViewId,
        },
        interpretation: {
          status: 'locally-interpreted',
          target: 'prompt-document-preview-content',
        },
        message:
          `Prompt document preview on view '${view.Name}' declares Content without title content; ` +
          'the frontend will use legacy preview title fallback semantics.',
        reason: 'legacy-prompt-document-preview-title',
        severity: 'warning',
        source: 'presentation-view-coverage',
        suggestedNextStep:
          'Declare PromptDocumentPreview.Content.Title.',
        view: viewTrace,
      }),
    )
  }

  const hasDocumentTextContent = Boolean(
    preview.Content.Description ??
    preview.Content.DescriptionTemplate ??
    preview.Content.Subtitle,
  )
  if (!hasDocumentTextContent) {
    diagnostics.push(
      createViewDiagnostic({
        category: 'incomplete-projection',
        details: {
          previewViewId: preview.PreviewViewId,
          workspacePageViewId: preview.WorkspacePageViewId,
        },
        interpretation: {
          status: 'locally-interpreted',
          target: 'prompt-document-preview-content',
        },
        message:
          `Prompt document preview on view '${view.Name}' declares Content without document text content; ` +
          'the frontend will use legacy preview document text fallback semantics.',
        reason: 'legacy-prompt-document-preview-text',
        severity: 'warning',
        source: 'presentation-view-coverage',
        suggestedNextStep:
          'Declare PromptDocumentPreview.Content.Description, DescriptionTemplate, or Subtitle.',
        view: viewTrace,
      }),
    )
  }

  return diagnostics
}

function projectPromptDocumentPreviewBadgeContentDiagnostics(
  module: PresentationModuleDefinition,
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
): readonly PresentationProjectionDiagnostic[] {
  const preview = view.PromptDocumentPreview
  if (!preview) {
    return []
  }

  return preview.Badges.flatMap((badge) => {
    const diagnostics: PresentationProjectionDiagnostic[] = []
    const hasLegacyValue = Boolean(badge.Value || badge.ValueTemplate)
    const hasContentLabel = Boolean(
      badge.Content?.Description ??
      badge.Content?.DescriptionTemplate ??
      badge.Content?.Title ??
      badge.Content?.Subtitle,
    )

    if (!badge.Content) {
      diagnostics.push(createViewDiagnostic({
        category: 'incomplete-projection',
        details: {
          badgeId: badge.Id,
          badgeName: badge.Name,
          previewViewId: preview.PreviewViewId,
          workspacePageViewId: preview.WorkspacePageViewId,
        },
        interpretation: {
          status: 'locally-interpreted',
          target: 'presentation-badge-content',
        },
        message:
          `Prompt document preview badge '${badge.Name}' on view '${view.Name}' does not ` +
          'declare Content; the frontend will use badge value or name fallback semantics.',
        reason: `legacy-prompt-document-preview-badge-content.${badge.Id}`,
        severity: 'warning',
        source: 'presentation-view-coverage',
        suggestedNextStep:
          'Declare PresentationBadge.Content with title, subtitle, description, or description template.',
        view: viewTrace,
      }))
    } else if (hasLegacyValue && !hasContentLabel) {
      diagnostics.push(createViewDiagnostic({
        category: 'incomplete-projection',
        details: {
          badgeId: badge.Id,
          badgeName: badge.Name,
          previewViewId: preview.PreviewViewId,
          workspacePageViewId: preview.WorkspacePageViewId,
        },
        interpretation: {
          status: 'locally-interpreted',
          target: 'presentation-badge-content',
        },
        message:
          `Prompt document preview badge '${badge.Name}' on view '${view.Name}' declares ` +
          'Content without label content; the frontend will use badge value fallback semantics.',
        reason: `legacy-prompt-document-preview-badge-content-label.${badge.Id}`,
        severity: 'warning',
        source: 'presentation-view-coverage',
        suggestedNextStep:
          'Declare PresentationBadge.Content title, subtitle, description, or description template.',
        view: viewTrace,
      }))
    }

    if (badge.FieldId && !findPresentationField(module, badge.FieldId)) {
      diagnostics.push(createViewDiagnostic({
        category: 'missing-definition',
        details: {
          badgeId: badge.Id,
          badgeName: badge.Name,
          fieldId: badge.FieldId,
          previewViewId: preview.PreviewViewId,
          workspacePageViewId: preview.WorkspacePageViewId,
        },
        interpretation: {
          status: 'unbound',
          target: 'presentation-field',
        },
        message:
          `Prompt document preview badge '${badge.Name}' on view '${view.Name}' references ` +
          `field '${badge.FieldId}', but the field is not present in the presentation module.`,
        reason: `missing-prompt-document-preview-badge-field.${badge.Id}.${badge.FieldId}`,
        severity: 'error',
        source: 'presentation-view-coverage',
        view: viewTrace,
      }))
    }

    if (badge.Tone && !isSupportedPresentationBadgeTone(badge.Tone)) {
      diagnostics.push(createViewDiagnostic({
        category: 'unsupported',
        details: {
          badgeId: badge.Id,
          badgeName: badge.Name,
          supportedTones: presentationBadgeToneValues,
          tone: badge.Tone,
        },
        interpretation: {
          status: 'unsupported',
          target: 'presentation-badge-tone',
        },
        message:
          `Prompt document preview badge '${badge.Name}' on view '${view.Name}' declares ` +
          `tone '${badge.Tone}', but the active frontend badge design interpreter does not ` +
          'declare that tone.',
        reason: `unsupported-prompt-document-preview-badge-tone.${badge.Id}.${badge.Tone}`,
        severity: 'warning',
        source: 'presentation-view-coverage',
        suggestedNextStep:
          'Use a standard presentation badge tone or extend the design-system badge tone interpreter.',
        view: viewTrace,
      }))
    }

    return diagnostics
  })
}

const presentationBadgeToneValues = [
  'accent',
  'danger',
  'info',
  'muted',
  'neutral',
  'success',
  'warning',
] as const

const presentationBadgeTones = new Set<string>(presentationBadgeToneValues)

function isSupportedPresentationBadgeTone(tone: string) {
  return presentationBadgeTones.has(tone.trim().toLowerCase())
}

function projectViewChromeSlotReferenceDiagnostics(
  module: PresentationModuleDefinition,
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
  slot: ViewChromeSlotDefinition,
): readonly PresentationProjectionDiagnostic[] {
  const diagnostics: PresentationProjectionDiagnostic[] = []

  if (
    isViewChromeSlotKind(slot, viewChromeSlotKinds.badgeStrip) &&
    slot.FieldIds.length > 0 &&
    (slot.Badges?.length ?? 0) === 0
  ) {
    diagnostics.push(createViewChromeSlotDiagnostic({
      category: 'incomplete-projection',
      details: {
        fieldIds: slot.FieldIds,
      },
      interpretation: {
        status: 'locally-interpreted',
        target: 'view-chrome-badge-strip-field-ids',
      },
      message:
        `Badge strip slot '${slot.Id}' on '${view.Name}' still declares FieldIds without ` +
        'first-class badge definitions; the frontend will synthesize field-backed badges.',
      reason: `legacy-badge-strip-field-ids.${slot.Id}`,
      severity: 'warning',
      slot,
      suggestedNextStep:
        'Declare ViewChromeSlot.Badges with PresentationBadgeDefinition entries and remove BadgeStrip FieldIds.',
      view: viewTrace,
    }))
  }

  for (const fieldId of slot.FieldIds) {
    if (!findPresentationField(module, fieldId)) {
      diagnostics.push(createViewChromeSlotDiagnostic({
        message:
          `View chrome slot '${slot.Id}' on '${view.Name}' references ` +
          `field '${fieldId}', but the field is not present in the presentation module.`,
        reason: `missing-view-chrome-slot-field.${slot.Id}.${fieldId}`,
        severity: 'error',
        slot,
        view: viewTrace,
      }))
    }
  }

  for (const badge of slot.Badges ?? []) {
    if (badge.FieldId && !findPresentationField(module, badge.FieldId)) {
      diagnostics.push(createViewChromeSlotDiagnostic({
        category: 'missing-definition',
        details: {
          badgeId: badge.Id,
          badgeName: badge.Name,
          fieldId: badge.FieldId,
        },
        interpretation: {
          status: 'unbound',
          target: 'presentation-field',
        },
        message:
          `View chrome slot '${slot.Id}' on '${view.Name}' declares badge ` +
          `'${badge.Name}' for field '${badge.FieldId}', but the field is not present ` +
          'in the presentation module.',
        reason: `missing-view-chrome-slot-badge-field.${slot.Id}.${badge.Id}.${badge.FieldId}`,
        severity: 'error',
        slot,
        view: viewTrace,
      }))
    }

    if (badge.Tone && !isSupportedPresentationBadgeTone(badge.Tone)) {
      diagnostics.push(createViewChromeSlotDiagnostic({
        category: 'unsupported',
        details: {
          badgeId: badge.Id,
          badgeName: badge.Name,
          supportedTones: presentationBadgeToneValues,
          tone: badge.Tone,
        },
        interpretation: {
          status: 'unsupported',
          target: 'presentation-badge-tone',
        },
        message:
          `View chrome slot '${slot.Id}' on '${view.Name}' declares badge ` +
          `'${badge.Name}' with tone '${badge.Tone}', but the active frontend badge ` +
          'design interpreter does not declare that tone.',
        reason: `unsupported-view-chrome-slot-badge-tone.${slot.Id}.${badge.Id}.${badge.Tone}`,
        severity: 'warning',
        slot,
        suggestedNextStep:
          'Use a standard presentation badge tone or extend the design-system badge tone interpreter.',
        view: viewTrace,
      }))
    }
  }

  for (const actionPlacement of slot.Actions) {
    if (!findPresentationAction(module, actionPlacement.ActionId)) {
      diagnostics.push(createViewChromeSlotDiagnostic({
        message:
          `View chrome slot '${slot.Id}' on '${view.Name}' references ` +
          `action '${actionPlacement.ActionId}', but the action is not present in the presentation module.`,
        reason: `missing-view-chrome-slot-action.${slot.Id}.${actionPlacement.ActionId}`,
        severity: 'error',
        slot,
        view: viewTrace,
      }))
    }
  }

  for (const viewId of slot.ViewIds) {
    if (!findPresentationView(module, viewId)) {
      diagnostics.push(createViewChromeSlotDiagnostic({
        message:
          `View chrome slot '${slot.Id}' on '${view.Name}' references ` +
          `view '${viewId}', but the view is not present in the presentation module.`,
        reason: `missing-view-chrome-slot-view.${slot.Id}.${viewId}`,
        severity: 'error',
        slot,
        view: viewTrace,
      }))
    }
  }

  if (slot.StateId && !view.State.some((state) => state.Id === slot.StateId)) {
    diagnostics.push(createViewChromeSlotDiagnostic({
      message:
        `View chrome slot '${slot.Id}' on '${view.Name}' references ` +
        `state '${slot.StateId}', but the state is not declared by the view.`,
      reason: `missing-view-chrome-slot-state.${slot.Id}.${slot.StateId}`,
      severity: 'error',
      slot,
      view: viewTrace,
    }))
  }

  return diagnostics
}

function projectViewChromeSlotInterpretationDiagnostics(
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
  slot: ViewChromeSlotDefinition,
  viewChromeSlotRendererKeys: readonly string[],
): readonly PresentationProjectionDiagnostic[] {
  const candidateRendererKeys = getViewChromeSlotRendererCandidateKeys(slot)
  if (hasViewChromeSlotRendererBinding(viewChromeSlotRendererKeys, slot)) {
    return [
      createViewChromeSlotDiagnostic({
        category: 'local-interpretation',
        details: {
          candidateRendererKeys,
          slotId: slot.Id,
          slotKind: slot.Kind,
          slotPlacement: slot.Placement,
        },
        interpretation: {
          status: 'bound',
          target: 'view-chrome-slot-renderer-registry',
        },
        message:
          `View chrome slot '${slot.Id}' on '${view.Name}' is bound ` +
          'by the view chrome slot renderer registry.',
        reason: `bound-view-chrome-slot-renderer.${slot.Id}`,
        severity: 'info',
        slot,
        view: viewTrace,
      }),
    ]
  }

  if (isViewChromeSlotKind(slot, viewChromeSlotKinds.custom)) {
    return [
      createViewChromeSlotDiagnostic({
        category: 'missing-binding',
        details: {
          candidateRendererKeys,
          slotId: slot.Id,
          slotKind: slot.Kind,
          slotPlacement: slot.Placement,
        },
        interpretation: {
          status: 'unbound',
          target: 'view-chrome-slot-renderer-registry',
        },
        message:
          `View chrome slot '${slot.Id}' on '${view.Name}' is Custom, ` +
          'but no frontend view chrome-slot interpreter is registered for it.',
        reason: `unbound-view-chrome-custom-slot.${slot.Id}`,
        severity: 'warning',
        slot,
        suggestedNextStep:
          'Register a view chrome-slot renderer for this custom slot or model it with a first-class slot kind.',
        view: viewTrace,
      }),
    ]
  }

  if (isViewChromeSlotRendererExpected(slot)) {
    return [
      createViewChromeSlotDiagnostic({
        category: 'missing-binding',
        details: {
          candidateRendererKeys,
          slotId: slot.Id,
          slotKind: slot.Kind,
          slotPlacement: slot.Placement,
        },
        interpretation: {
          status: 'unbound',
          target: 'view-chrome-slot-renderer-registry',
        },
        message:
          `View chrome slot '${slot.Id}' on '${view.Name}' has no ` +
          'registered frontend renderer for its kind and placement.',
        reason: `unbound-view-chrome-slot-renderer.${slot.Id}`,
        severity: 'warning',
        slot,
        suggestedNextStep:
          'Register a view chrome-slot renderer or add a generic interpreter for this slot kind.',
        view: viewTrace,
      }),
    ]
  }

  return []
}

function isViewChromeSlotRendererExpected(
  slot: ViewChromeSlotDefinition,
) {
  return isViewChromeSlotKind(slot, viewChromeSlotKinds.actions) ||
    isViewChromeSlotKind(slot, viewChromeSlotKinds.badgeStrip) ||
    isViewChromeSlotKind(slot, viewChromeSlotKinds.metricStrip) ||
    isViewChromeSlotKind(slot, viewChromeSlotKinds.viewSwitch) ||
    isViewChromeSlotKind(slot, viewChromeSlotKinds.layoutSwitch) ||
    isViewChromeSlotKind(slot, viewChromeSlotKinds.headingTrailing) ||
    isViewChromeSlotKind(slot, viewChromeSlotKinds.status)
}

function createViewChromeSlotDiagnostic({
  category,
  details,
  interpretation,
  message,
  reason,
  severity,
  slot,
  suggestedNextStep,
  view,
}: {
  readonly category?: PresentationProjectionDiagnostic['category']
  readonly details?: Readonly<Record<string, unknown>>
  readonly interpretation?: PresentationProjectionDiagnostic['interpretation']
  readonly message: string
  readonly reason: string
  readonly severity: PresentationProjectionDiagnostic['severity']
  readonly slot: ViewChromeSlotDefinition
  readonly suggestedNextStep?: string
  readonly view: PresentationProjectionTraceView
}) {
  return createViewDiagnostic({
    category,
    details: {
      viewChromeSlotId: slot.Id,
      viewChromeSlotKind: slot.Kind,
      viewChromeSlotPlacement: slot.Placement,
      ...details,
    },
    interpretation,
    message,
    reason,
    severity,
    source: 'presentation-view-chrome-coverage',
    suggestedNextStep,
    view,
  })
}

function projectCollectionCoverageDiagnostics(
  module: PresentationModuleDefinition,
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
  collectionSelectionStateRuntimeBound: boolean,
  collectionChromeSlotRendererKeys: readonly string[],
): readonly PresentationProjectionDiagnostic[] {
  const collection = view.Collection
  if (!collection) {
    return []
  }

  const diagnostics: PresentationProjectionDiagnostic[] = []
  const bodySlot = resolveCollectionChromeBodySlot(collection)
  const detailSlot = resolveCollectionChromeDetailSlot(collection)
  const rowIdentityPath = bodySlot?.RowIdentityPath ?? null
  const selectionMode = bodySlot?.SelectionMode ?? collectionSelectionModes.none
  const selectionStateId = bodySlot?.StateId ?? null
  const rowActions = resolveCollectionChromeRowActions(collection)
  const selectionActions = resolveCollectionChromeSelectionActions(collection)
  const projectedActions = getPresentationViewProjectedActions(view)

  diagnostics.push(
    ...projectCollectionOuterViewDuplicationDiagnostics(view, viewTrace),
    ...projectProjectedCollectionActionContextDiagnostics({
      bodySlot,
      collectionChromeSlotRendererKeys,
      projectedActions,
      selectionMode,
      selectionStateId,
      view,
      viewTrace,
    }),
    ...projectCollectionChromeCoverageDiagnostics(
      module,
      view,
      viewTrace,
      collectionChromeSlotRendererKeys,
    ),
  )

  if (bodySlot || detailSlot) {
    const isSelectionEnabled = isCollectionSelectionEnabled(selectionMode)
    if (isSelectionEnabled) {
      if (!rowIdentityPath) {
        diagnostics.push(createViewDiagnostic({
          details: {
            bodySlotId: bodySlot?.Id ?? null,
            selectionMode,
            selectionStateId,
          },
          message:
            `Collection view '${view.Name}' enables row selection, ` +
            'but its Body chrome slot does not declare a row identity path.',
          reason: `missing-collection-row-identity.${view.Id}`,
          severity: 'error',
          source: 'presentation-collection-coverage',
          view: viewTrace,
        }))
      }

      if (!selectionStateId) {
        diagnostics.push(createViewDiagnostic({
          details: {
            bodySlotId: bodySlot?.Id ?? null,
            selectionMode,
          },
          interpretation: {
            status: 'unbound',
            target: 'collection-selection-state',
          },
          message:
            `Collection view '${view.Name}' enables row selection, ` +
            'but its Body chrome slot does not declare a selection state id.',
          reason: `missing-collection-selection-state-id.${view.Id}`,
          severity: 'warning',
          source: 'presentation-collection-coverage',
          suggestedNextStep:
            'Declare StateId on the collection Body chrome slot so frontend targets can bind the selected row identities.',
          view: viewTrace,
        }))
      } else if (!collectionSelectionStateRuntimeBound) {
        diagnostics.push(createViewDiagnostic({
          category: 'missing-binding',
          details: {
            bodySlotId: bodySlot?.Id ?? null,
            selectionMode,
            selectionStateId,
          },
          interpretation: {
            status: 'unbound',
            target: 'collection-selection-state-runtime',
          },
          message:
            `Collection view '${view.Name}' declares selection state ` +
            `'${selectionStateId}', but no frontend selection-state runtime is bound.`,
          reason: `missing-collection-selection-state-runtime.${selectionStateId}`,
          severity: 'warning',
          source: 'presentation-collection-coverage',
          suggestedNextStep:
            'Install a generic collection selection-state runtime or bind this state id through a target adapter.',
          view: viewTrace,
        }))
      }
    }

    const activatedRowActionId = bodySlot?.ActivatedRowActionId ?? null
    const activatedRowAction = activatedRowActionId
      ? rowActions.find((rowAction) =>
          rowAction.Id === activatedRowActionId ||
          rowAction.ActionId === activatedRowActionId)
      : rowActions.find(isPrimaryCollectionRowAction)
    if (activatedRowActionId && !activatedRowAction) {
      diagnostics.push(createViewDiagnostic({
        details: { activatedRowActionId },
        message:
          `Collection view '${view.Name}' declares activated row action ` +
          `'${activatedRowActionId}', but no row action with that id or action id exists.`,
        reason: `missing-activated-collection-row-action.${activatedRowActionId}`,
        severity: 'error',
        source: 'presentation-collection-coverage',
        view: viewTrace,
      }))
    }

    if (bodySlot?.ActivateOnRowClick && !activatedRowAction) {
      diagnostics.push(createViewDiagnostic({
        category: 'missing-binding',
        details: { activatedRowActionId: activatedRowActionId ?? null },
        interpretation: {
          status: 'unbound',
          target: 'collection-body-row-activation',
        },
        message:
          `Collection view '${view.Name}' enables row activation, ` +
          'but no activated or primary row action can be resolved from the Body chrome slot.',
        reason: `missing-row-click-activation-action.${view.Id}`,
        severity: 'error',
        source: 'presentation-collection-coverage',
        view: viewTrace,
      }))
    }

    if (bodySlot?.SelectOnRowClick && !isSelectionEnabled) {
      diagnostics.push(createViewDiagnostic({
        category: 'missing-binding',
        details: {
          bodySlotId: bodySlot.Id,
          selectionMode,
          selectOnRowClick: bodySlot.SelectOnRowClick,
        },
        interpretation: {
          status: 'unbound',
          target: 'collection-row-click-selection',
        },
        message:
          `Collection view '${view.Name}' enables row-click selection, ` +
          'but row selection is disabled.',
        reason: `row-click-selection-without-selection.${view.Id}`,
        severity: 'warning',
        source: 'presentation-collection-coverage',
        suggestedNextStep:
          'Enable single or multiple selection, or disable SelectOnRowClick.',
        view: viewTrace,
      }))
    }

    if (detailSlot?.DetailViewId) {
      if (isCollectionDetailActivationUnsupported(detailSlot.DetailActivation)) {
        diagnostics.push(createViewDiagnostic({
          category: 'missing-binding',
          details: {
            detailActivation: detailSlot.DetailActivation,
            detailSlotId: detailSlot.Id,
            detailViewId: detailSlot.DetailViewId,
          },
          interpretation: {
            status: 'unbound',
            target: 'collection-detail-activation',
          },
          message:
            `Collection view '${view.Name}' declares detail activation ` +
            `'${detailSlot.DetailActivation}', but the frontend interpreter currently binds selection and row-activation detail context only.`,
          reason: `unsupported-collection-detail-activation.${detailSlot.DetailViewId}`,
          severity: 'warning',
          source: 'presentation-collection-coverage',
          suggestedNextStep:
            'Use Selection or RowActivation, or add a frontend interpretation for this activation mode.',
          view: viewTrace,
        }))
      }

      if (
        isCollectionDetailActivationSelection(detailSlot.DetailActivation) &&
        !bodySlot?.SelectOnRowClick
      ) {
        diagnostics.push(createViewDiagnostic({
          category: 'missing-binding',
          details: {
            bodySlotId: bodySlot?.Id ?? null,
            detailActivation: detailSlot.DetailActivation,
            detailSlotId: detailSlot.Id,
            detailViewId: detailSlot.DetailViewId,
            selectOnRowClick: bodySlot?.SelectOnRowClick ?? false,
          },
          interpretation: {
            status: 'unbound',
            target: 'collection-selection-detail-context',
          },
          message:
            `Collection view '${view.Name}' renders detail from selection, ` +
            'but row-click selection is disabled.',
          reason: `selection-detail-without-row-click-selection.${detailSlot.DetailViewId}`,
          severity: 'warning',
          source: 'presentation-collection-coverage',
          suggestedNextStep:
            'Enable SelectOnRowClick for selection-driven detail previews, or change the detail activation mode.',
          view: viewTrace,
        }))
      }

      if (
        isCollectionDetailActivationSelection(detailSlot.DetailActivation) &&
        bodySlot?.SelectOnRowClick &&
        bodySlot.ActivateOnRowClick
      ) {
        diagnostics.push(createViewDiagnostic({
          category: 'unsupported',
          details: {
            bodySlotId: bodySlot.Id,
            detailActivation: detailSlot.DetailActivation,
            detailSlotId: detailSlot.Id,
            detailViewId: detailSlot.DetailViewId,
          },
          interpretation: {
            status: 'unsupported',
            target: 'collection-row-click-interaction',
          },
          message:
            `Collection view '${view.Name}' both selects and activates rows on click; ` +
            'selection-driven detail previews may be bypassed by navigation.',
          reason: `selection-detail-and-row-click-activation.${detailSlot.DetailViewId}`,
          severity: 'warning',
          source: 'presentation-collection-coverage',
          suggestedNextStep:
            'Use SelectOnRowClick for preview-first rows and expose navigation through row actions.',
          view: viewTrace,
        }))
      }

      if (!findPresentationView(module, detailSlot.DetailViewId)) {
        diagnostics.push(createViewDiagnostic({
          details: {
            detailSlotId: detailSlot.Id,
            detailViewId: detailSlot.DetailViewId,
          },
          message:
            `Collection view '${view.Name}' references detail view ` +
            `'${detailSlot.DetailViewId}', but that view is not present in the presentation module.`,
          reason: `missing-collection-detail-view.${detailSlot.DetailViewId}`,
          severity: 'error',
          source: 'presentation-collection-coverage',
          view: viewTrace,
        }))
      }

      if (!isSelectionEnabled) {
        diagnostics.push(createViewDiagnostic({
          category: 'missing-binding',
          details: {
            detailSlotId: detailSlot.Id,
            detailViewId: detailSlot.DetailViewId,
          },
          interpretation: {
            status: 'unbound',
            target: 'collection-detail-selection-context',
          },
          message:
            `Collection view '${view.Name}' declares detail view ` +
            `'${detailSlot.DetailViewId}', but row selection is not enabled for the collection.`,
          reason: `missing-collection-detail-selection-context.${detailSlot.DetailViewId}`,
          severity: 'warning',
          source: 'presentation-collection-coverage',
          suggestedNextStep:
            'Enable collection row selection and declare a row identity path so the detail view can bind to selected-row context.',
          view: viewTrace,
        }))
      }
    }
  }

  for (const rowAction of rowActions) {
    diagnostics.push(
      ...projectCollectionRowActionPredicateCoverageDiagnostics(rowAction, view, viewTrace),
    )

    const action = findPresentationAction<ActionDefinition>(module, rowAction.ActionId)
    if (!action) {
      continue
    }

    const boundParameterNames = new Set(rowAction.Parameters.map((parameter) => parameter.Name))
    const missingRequiredParameters = action.Parameters.filter((parameter) =>
      parameter.IsRequired && !boundParameterNames.has(parameter.Name))
    if (missingRequiredParameters.length > 0) {
      diagnostics.push(createViewDiagnostic({
        details: {
          actionId: action.Id,
          missingParameters: missingRequiredParameters.map((parameter) => parameter.Name),
          rowActionId: rowAction.Id,
        },
        message:
          `Collection row action '${rowAction.Id}' invokes '${action.Name}', ` +
          `but does not bind required parameter(s): ${missingRequiredParameters
            .map((parameter) => parameter.Name)
            .join(', ')}.`,
        reason: `missing-collection-row-action-parameters.${rowAction.Id}`,
        severity: 'error',
        source: 'presentation-collection-coverage',
        view: viewTrace,
      }))
    }

    for (const parameter of rowAction.Parameters) {
      if (parameter.FieldId && !findPresentationField(module, parameter.FieldId)) {
        diagnostics.push(createViewDiagnostic({
          details: {
            actionId: action.Id,
            fieldId: parameter.FieldId,
            parameter: parameter.Name,
            rowActionId: rowAction.Id,
          },
          message:
            `Collection row action '${rowAction.Id}' parameter '${parameter.Name}' ` +
            `references field '${parameter.FieldId}', but that field is not present in the presentation module.`,
          reason: `missing-collection-row-action-parameter-field.${rowAction.Id}.${parameter.Name}`,
          severity: 'error',
          source: 'presentation-collection-coverage',
          view: viewTrace,
        }))
      }
    }
  }

  for (const selectionAction of selectionActions) {
    diagnostics.push(
      ...projectCollectionSelectionActionPredicateCoverageDiagnostics(
        selectionAction,
        view,
        viewTrace,
      ),
    )

    if (!rowIdentityPath) {
      diagnostics.push(createViewDiagnostic({
        details: {
          actionId: selectionAction.ActionId,
          bodySlotId: bodySlot?.Id ?? null,
          selectionActionId: selectionAction.Id,
        },
        message:
          `Collection selection action '${selectionAction.Id}' on '${view.Name}' ` +
          'requires selected row identity context, but the Body chrome slot does not declare a row identity path.',
        reason: `missing-selection-action-row-identity.${selectionAction.Id}`,
        severity: 'error',
        source: 'presentation-collection-coverage',
        view: viewTrace,
      }))
    }

    if (!isCollectionSelectionEnabled(selectionMode)) {
      diagnostics.push(createViewDiagnostic({
        details: {
          actionId: selectionAction.ActionId,
          selectionActionId: selectionAction.Id,
        },
        message:
          `Collection selection action '${selectionAction.Id}' on '${view.Name}' ` +
          'is declared, but row selection is not enabled for the collection.',
        reason: `missing-selection-action-selection-mode.${selectionAction.Id}`,
        severity: 'warning',
        source: 'presentation-collection-coverage',
        view: viewTrace,
      }))
    }

    const action = findPresentationAction<ActionDefinition>(module, selectionAction.ActionId)
    if (!action) {
      continue
    }

    const boundParameterNames = new Set(selectionAction.Parameters.map((parameter) => parameter.Name))
    const missingRequiredParameters = action.Parameters.filter((parameter) =>
      parameter.IsRequired && !boundParameterNames.has(parameter.Name))
    if (missingRequiredParameters.length > 0) {
      diagnostics.push(createViewDiagnostic({
        details: {
          actionId: action.Id,
          missingParameters: missingRequiredParameters.map((parameter) => parameter.Name),
          selectionActionId: selectionAction.Id,
        },
        message:
          `Collection selection action '${selectionAction.Id}' invokes '${action.Name}', ` +
          `but does not bind required parameter(s): ${missingRequiredParameters
            .map((parameter) => parameter.Name)
            .join(', ')}.`,
        reason: `missing-collection-selection-action-parameters.${selectionAction.Id}`,
        severity: 'error',
        source: 'presentation-collection-coverage',
        view: viewTrace,
      }))
    }

    for (const parameter of selectionAction.Parameters) {
      if (parameter.FieldId && !findPresentationField(module, parameter.FieldId)) {
        diagnostics.push(createViewDiagnostic({
          details: {
            actionId: action.Id,
            fieldId: parameter.FieldId,
            parameter: parameter.Name,
            selectionActionId: selectionAction.Id,
          },
          message:
            `Collection selection action '${selectionAction.Id}' parameter '${parameter.Name}' ` +
            `references field '${parameter.FieldId}', but that field is not present in the presentation module.`,
          reason: `missing-collection-selection-action-parameter-field.${selectionAction.Id}.${parameter.Name}`,
          severity: 'error',
          source: 'presentation-collection-coverage',
          view: viewTrace,
        }))
      }

      if (isSelectedRowValueSelectionActionParameter(parameter.Source) && !parameter.ValuePath) {
        diagnostics.push(createViewDiagnostic({
          details: {
            actionId: action.Id,
            parameter: parameter.Name,
            parameterSource: parameter.Source,
            selectionActionId: selectionAction.Id,
          },
          message:
            `Collection selection action '${selectionAction.Id}' parameter '${parameter.Name}' ` +
            'uses selected row values but does not declare a value path.',
          reason: `missing-collection-selection-action-parameter-value-path.${selectionAction.Id}.${parameter.Name}`,
          severity: 'error',
          source: 'presentation-collection-coverage',
          view: viewTrace,
        }))
      }
    }
  }

  return diagnostics
}

function projectCollectionChromeCoverageDiagnostics(
  module: PresentationModuleDefinition,
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
  collectionChromeSlotRendererKeys: readonly string[],
): readonly PresentationProjectionDiagnostic[] {
  const collection = view.Collection
  if (!collection) {
    return []
  }

  const diagnostics: PresentationProjectionDiagnostic[] = []
  const slots = resolveProjectedCollectionChromeSlots(collection)
  diagnostics.push(
    ...projectCollectionChromeCompatibilityDiagnostics(view, viewTrace, collection, slots),
    ...projectCollectionChromeRequiredSlotDiagnostics(module, view, viewTrace, collection),
  )
  const seenSlotIds = new Set<string>()

  for (const slot of slots) {
    if (seenSlotIds.has(slot.Id)) {
      diagnostics.push(createCollectionChromeSlotDiagnostic({
        details: { slotId: slot.Id },
        message:
          `Collection view '${view.Name}' declares collection chrome slot ` +
          `'${slot.Id}' more than once.`,
        reason: `duplicate-collection-chrome-slot.${slot.Id}`,
        severity: 'warning',
        slot,
        view: viewTrace,
      }))
    }
    seenSlotIds.add(slot.Id)

    diagnostics.push(
      ...projectCollectionChromeSlotReferenceDiagnostics(module, view, viewTrace, slot),
      ...projectCollectionChromeSlotInterpretationDiagnostics(
        view,
        viewTrace,
        slot,
        collectionChromeSlotRendererKeys,
      ),
    )
  }

  return diagnostics
}

function projectCollectionOuterViewDuplicationDiagnostics(
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
): readonly PresentationProjectionDiagnostic[] {
  if (!view.Collection) {
    return []
  }

  const duplicatedFields = [
    view.Subject.DataSourceId ? 'Subject.DataSourceId' : null,
    view.DataSourceIds.length > 0 ? 'DataSourceIds' : null,
    view.FieldIds.length > 0 ? 'FieldIds' : null,
    view.Actions.length > 0 ? 'Actions' : null,
  ].filter((field): field is string => Boolean(field))

  if (duplicatedFields.length === 0) {
    return []
  }

  return [
    createViewDiagnostic({
      category: 'incomplete-projection',
      details: { duplicatedFields },
      interpretation: {
        status: 'unsupported',
        target: 'collection-chrome-single-source-of-truth',
      },
      message:
        `Collection view '${view.Name}' still declares collection-specific ` +
        `outer view data: ${duplicatedFields.join(', ')}.`,
      reason: `duplicated-collection-outer-view-data.${view.Id}`,
      severity: 'warning',
      source: 'presentation-collection-coverage',
      suggestedNextStep:
        'Move collection data sources, fields, actions, and query/detail semantics into Collection.Chrome slots.',
      view: viewTrace,
    }),
  ]
}

function projectProjectedCollectionActionContextDiagnostics({
  bodySlot,
  collectionChromeSlotRendererKeys,
  projectedActions,
  selectionMode,
  selectionStateId,
  view,
  viewTrace,
}: {
  readonly bodySlot: CollectionChromeSlotDefinition | null
  readonly collectionChromeSlotRendererKeys: readonly string[]
  readonly projectedActions: readonly PresentationViewProjectedActionRef[]
  readonly selectionMode: unknown
  readonly selectionStateId: string | null
  readonly view: ViewDefinition
  readonly viewTrace: PresentationProjectionTraceView
}): readonly PresentationProjectionDiagnostic[] {
  const diagnostics: PresentationProjectionDiagnostic[] = []
  const bodySlotRendererBound = bodySlot
    ? hasCollectionChromeSlotRendererBinding(collectionChromeSlotRendererKeys, bodySlot)
    : false

  for (const actionRef of projectedActions) {
    if (actionRef.contextKind === 'collection-row') {
      if (!bodySlot) {
        diagnostics.push(createViewDiagnostic({
          category: 'missing-binding',
          details: {
            actionId: actionRef.actionId,
            actionSource: actionRef.source,
            collectionActionContextKind: actionRef.contextKind,
            rowActionId: actionRef.rowAction?.Id ?? null,
            slotId: actionRef.slotId ?? null,
          },
          interpretation: {
            status: 'unbound',
            target: 'collection-row-action-context',
          },
          message:
            `Collection row action '${actionRef.actionId}' on '${view.Name}' ` +
            'requires row context, but the collection does not declare a Body chrome slot.',
          reason: `missing-collection-row-action-context.${actionRef.source}`,
          severity: 'error',
          source: 'presentation-action-context-coverage',
          suggestedNextStep:
            'Declare a Body chrome slot so row actions can be interpreted against collection rows.',
          view: viewTrace,
        }))
      } else if (!bodySlotRendererBound) {
        diagnostics.push(createViewDiagnostic({
          category: 'missing-binding',
          details: {
            actionId: actionRef.actionId,
            actionSource: actionRef.source,
            bodySlotId: bodySlot.Id,
            collectionActionContextKind: actionRef.contextKind,
            rowActionId: actionRef.rowAction?.Id ?? null,
            slotId: actionRef.slotId ?? null,
          },
          interpretation: {
            status: 'unbound',
            target: 'collection-row-action-context-renderer',
          },
          message:
            `Collection row action '${actionRef.actionId}' on '${view.Name}' ` +
            `requires row context from Body chrome slot '${bodySlot.Id}', but no Body slot renderer is bound.`,
          reason: `unbound-collection-row-action-context-renderer.${actionRef.source}`,
          severity: 'warning',
          source: 'presentation-action-context-coverage',
          suggestedNextStep:
            'Bind a Body chrome slot renderer that supplies row context to collection row actions.',
          view: viewTrace,
        }))
      }

      const missingRowValueParameters = actionRef.rowAction?.Parameters
        .filter((parameter) => !parameter.ValuePath)
        .map((parameter) => parameter.Name) ?? []
      if (missingRowValueParameters.length > 0) {
        diagnostics.push(createViewDiagnostic({
          details: {
            actionId: actionRef.actionId,
            actionSource: actionRef.source,
            missingRowValueParameters,
            rowActionId: actionRef.rowAction?.Id ?? null,
          },
          message:
            `Collection row action '${actionRef.rowAction?.Id ?? actionRef.actionId}' ` +
            `on '${view.Name}' has parameter(s) without row value paths: ${missingRowValueParameters.join(', ')}.`,
          reason: `missing-collection-row-action-value-path.${actionRef.source}`,
          severity: 'error',
          source: 'presentation-action-context-coverage',
          view: viewTrace,
        }))
      }
    }

    if (actionRef.contextKind === 'collection-selection') {
      if (!isCollectionSelectionEnabled(selectionMode)) {
        diagnostics.push(createViewDiagnostic({
          category: 'missing-binding',
          details: {
            actionId: actionRef.actionId,
            actionSource: actionRef.source,
            collectionActionContextKind: actionRef.contextKind,
            selectionActionId: actionRef.selectionAction?.Id ?? null,
            selectionMode,
            slotId: actionRef.slotId ?? null,
          },
          interpretation: {
            status: 'unbound',
            target: 'collection-selection-action-context',
          },
          message:
            `Collection selection action '${actionRef.actionId}' on '${view.Name}' ` +
            'requires selection context, but row selection is not enabled.',
          reason: `missing-collection-selection-action-context.${actionRef.source}`,
          severity: 'warning',
          source: 'presentation-action-context-coverage',
          suggestedNextStep:
            'Enable collection row selection, or model the action as a plain view/slot action.',
          view: viewTrace,
        }))
      }

      if (!selectionStateId) {
        diagnostics.push(createViewDiagnostic({
          category: 'missing-binding',
          details: {
            actionId: actionRef.actionId,
            actionSource: actionRef.source,
            collectionActionContextKind: actionRef.contextKind,
            selectionActionId: actionRef.selectionAction?.Id ?? null,
            slotId: actionRef.slotId ?? null,
          },
          interpretation: {
            status: 'unbound',
            target: 'collection-selection-action-state',
          },
          message:
            `Collection selection action '${actionRef.actionId}' on '${view.Name}' ` +
            'requires selected-row state, but no selection StateId is declared.',
          reason: `missing-collection-selection-action-state.${actionRef.source}`,
          severity: 'warning',
          source: 'presentation-action-context-coverage',
          suggestedNextStep:
            'Declare StateId on the collection Body or SelectionActions chrome slot.',
          view: viewTrace,
        }))
      }

      if (!actionRef.context.selectedRowIdentityPath) {
        diagnostics.push(createViewDiagnostic({
          details: {
            actionId: actionRef.actionId,
            actionSource: actionRef.source,
            collectionActionContextKind: actionRef.contextKind,
            selectionActionId: actionRef.selectionAction?.Id ?? null,
            slotId: actionRef.slotId ?? null,
          },
          message:
            `Collection selection action '${actionRef.actionId}' on '${view.Name}' ` +
            'requires selected-row context, but the collection Body slot does not declare RowIdentityPath.',
          reason: `missing-collection-selection-action-row-identity.${actionRef.source}`,
          severity: 'error',
          source: 'presentation-action-context-coverage',
          view: viewTrace,
        }))
      }
    }
  }

  return diagnostics
}

function resolveCollectionChromeBodySlot(
  collection: NonNullable<ViewDefinition['Collection']>,
) {
  return resolveProjectedCollectionChromeSlots(collection)
    .find((slot) => isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.body)) ?? null
}

function resolveCollectionChromeDetailSlot(
  collection: NonNullable<ViewDefinition['Collection']>,
) {
  return resolveProjectedCollectionChromeSlots(collection)
    .find((slot) => isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.detail)) ?? null
}

function resolveCollectionChromeRowActions(
  collection: NonNullable<ViewDefinition['Collection']>,
) {
  return sortCollectionActionBindings(
    uniqueCollectionActionBindings(
      resolveProjectedCollectionChromeSlots(collection)
        .filter((slot) => isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.rowActions))
        .flatMap((slot) => slot.RowActions),
    ),
  )
}

function resolveCollectionChromeSelectionActions(
  collection: NonNullable<ViewDefinition['Collection']>,
) {
  return sortCollectionActionBindings(
    uniqueCollectionActionBindings(
      resolveProjectedCollectionChromeSlots(collection)
        .filter((slot) =>
          isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.selectionActions))
        .flatMap((slot) => slot.SelectionActions),
    ),
  )
}

function uniqueCollectionActionBindings<TAction extends { readonly Id: string }>(
  actions: readonly TAction[],
) {
  const seen = new Set<string>()
  return actions.filter((action) => {
    if (seen.has(action.Id)) {
      return false
    }

    seen.add(action.Id)
    return true
  })
}

function sortCollectionActionBindings<TAction extends { readonly Order: number }>(
  actions: readonly TAction[],
) {
  return actions.slice().sort((left, right) => left.Order - right.Order)
}

function projectCollectionChromeCompatibilityDiagnostics(
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
  collection: NonNullable<ViewDefinition['Collection']>,
  slots: readonly CollectionChromeSlotDefinition[],
): readonly PresentationProjectionDiagnostic[] {
  const diagnostics: PresentationProjectionDiagnostic[] = []
  const declaredSlots = collection.Chrome?.Slots ?? []
  const projectionMode = readCollectionChromeProjectionMode(collection.Chrome)
  const synthesizedSlots = slots.filter((slot) =>
    isCollectionChromeSlotProjectionMode(
      slot,
      collectionChromeProjectionModes.compatibilitySynthesized,
    ))

  if (
    projectionMode === collectionChromeProjectionModes.compatibilitySynthesized ||
    synthesizedSlots.length > 0
  ) {
    diagnostics.push(createViewDiagnostic({
      category: 'incomplete-projection',
      details: {
        declaredSlotIds: declaredSlots.map((slot) => slot.Id),
        projectionMode:
          projectionMode ?? collectionChromeProjectionModes.compatibilitySynthesized,
        synthesizedSlotIds: synthesizedSlots.map((slot) => slot.Id),
      },
      interpretation: {
        status: 'unsupported',
        target: 'projected-collection-runtime.chrome',
      },
      message:
        `Collection view '${view.Name}' uses compatibility-synthesized collection chrome; ` +
        'the runtime no longer synthesizes chrome from legacy collection fields.',
      reason: 'synthesized-collection-chrome',
      severity: 'warning',
      source: 'presentation-collection-chrome-coverage',
      suggestedNextStep:
        'Declare Collection.Chrome slots on the backend, including Body, QueryForm, Summary, RowActions, Detail, SelectionActions, and Pagination as needed.',
      view: viewTrace,
    }))
  }

  if (declaredSlots.length === 0) {
    diagnostics.push(createViewDiagnostic({
      category: 'incomplete-projection',
      details: {
        declaredSlotIds: declaredSlots.map((slot) => slot.Id),
        projectionMode: projectionMode ?? null,
      },
      interpretation: {
        status: 'unbound',
        target: 'projected-collection-runtime.chrome',
      },
      message:
        `Collection view '${view.Name}' does not declare Collection.Chrome slots; ` +
        'collection rendering now requires declared chrome, including a Body slot.',
      reason: 'missing-collection-chrome',
      severity: 'error',
      source: 'presentation-collection-chrome-coverage',
      suggestedNextStep:
        'Declare Collection.Chrome slots on the backend, including Body, QueryForm, Summary, RowActions, Detail, SelectionActions, and Pagination as needed.',
      view: viewTrace,
    }))
  }

  if (projectionMode === collectionChromeProjectionModes.mixed) {
    diagnostics.push(createViewDiagnostic({
      category: 'incomplete-projection',
      details: {
        declaredSlotIds: declaredSlots.map((slot) => slot.Id),
        projectionMode,
      },
      interpretation: {
        status: 'locally-interpreted',
        target: 'projected-collection-runtime.chrome.mixed',
      },
      message:
        `Collection view '${view.Name}' declares mixed collection chrome projection; ` +
        'some collection surface behavior may still rely on frontend compatibility interpretation.',
      reason: 'mixed-collection-chrome-projection',
      severity: 'info',
      source: 'presentation-collection-chrome-coverage',
      suggestedNextStep:
        'Replace mixed collection chrome with fully declared slots once the remaining compatibility behavior is modeled.',
      view: viewTrace,
    }))
  }

  if (
    declaredSlots.length > 0 &&
    projectionMode !== collectionChromeProjectionModes.compatibilitySynthesized &&
    !declaredSlots.some((slot) =>
      isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.body))
  ) {
    const isMixedProjection = projectionMode === collectionChromeProjectionModes.mixed
    diagnostics.push(createViewDiagnostic({
      category: 'incomplete-projection',
      details: {
        declaredSlotIds: declaredSlots.map((slot) => slot.Id),
        projectionMode: projectionMode ?? null,
      },
      interpretation: {
        status: 'unbound',
        target: 'projected-collection-view.body-slot',
      },
      message:
        `Collection view '${view.Name}' declares collection chrome without a Body slot; ` +
        'the collection body will not render until Body is declared.',
      reason: 'missing-collection-body-slot',
      severity: isMixedProjection ? 'warning' : 'error',
      source: 'presentation-collection-chrome-coverage',
      suggestedNextStep:
        'Add a Collection.Chrome Body slot so the collection body is projected through the chrome slot interpreter.',
      view: viewTrace,
    }))
  }

  return diagnostics
}

function projectCollectionChromeRequiredSlotDiagnostics(
  module: PresentationModuleDefinition,
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
  collection: NonNullable<ViewDefinition['Collection']>,
): readonly PresentationProjectionDiagnostic[] {
  const diagnostics: PresentationProjectionDiagnostic[] = []
  const chrome = createCollectionChromeRuntime(collection)

  if (chrome.bodySlot && chrome.bodySlot.DataSourceIds.length === 0) {
    diagnostics.push(createCollectionChromeSlotDiagnostic({
      details: { slotId: chrome.bodySlot.Id },
      interpretation: {
        status: 'unbound',
        target: 'projected-collection-runtime.body-data-source',
      },
      message:
        `Collection view '${view.Name}' declares Body chrome slot ` +
        `'${chrome.bodySlot.Id}', but the slot does not declare a data source.`,
      reason: `missing-collection-body-slot-data-source.${chrome.bodySlot.Id}`,
      severity: 'error',
      slot: chrome.bodySlot,
      suggestedNextStep:
        'Declare the collection row data source id on the Body chrome slot.',
      view: viewTrace,
    }))
  }

  for (const queryFormSlot of chrome.findSlots(collectionChromeSlotKinds.queryForm)) {
    if (!queryFormSlot.QueryFormId) {
      diagnostics.push(createCollectionChromeSlotDiagnostic({
        details: { slotId: queryFormSlot.Id },
        interpretation: {
          status: 'unbound',
          target: 'projected-collection-runtime.query-form-slot',
        },
        message:
          `Collection view '${view.Name}' declares QueryForm chrome slot ` +
          `'${queryFormSlot.Id}', but the slot does not declare QueryFormId.`,
        reason: `missing-collection-query-form-slot-query-form.${queryFormSlot.Id}`,
        severity: 'error',
        slot: queryFormSlot,
        suggestedNextStep:
          'Declare QueryFormId on the QueryForm chrome slot.',
        view: viewTrace,
      }))
    }
  }

  for (const paginationSlot of chrome.findSlots(collectionChromeSlotKinds.pagination)) {
    if (paginationSlot.DataSourceIds.length === 0) {
      diagnostics.push(createCollectionChromeSlotDiagnostic({
        details: { slotId: paginationSlot.Id },
        interpretation: {
          status: 'unbound',
          target: 'projected-collection-runtime.pagination-data-source',
        },
        message:
          `Collection view '${view.Name}' declares Pagination chrome slot ` +
          `'${paginationSlot.Id}', but the slot does not declare a paginated data source.`,
        reason: `missing-collection-pagination-slot-data-source.${paginationSlot.Id}`,
        severity: 'error',
        slot: paginationSlot,
        suggestedNextStep:
          'Declare the paginated collection-query data source id on the Pagination chrome slot.',
        view: viewTrace,
      }))
      continue
    }

    for (const dataSourceId of paginationSlot.DataSourceIds) {
      const dataSource = findPresentationDataSource(module, dataSourceId)
      if (!dataSource) {
        continue
      }

      if (!isCollectionQueryDataSource(dataSource)) {
        diagnostics.push(createCollectionChromeSlotDiagnostic({
          details: {
            dataSourceId,
            dataSourceKind: dataSource.Kind,
            slotId: paginationSlot.Id,
          },
          interpretation: {
            status: 'unsupported',
            target: 'projected-collection-runtime.pagination-data-source',
          },
          message:
            `Collection view '${view.Name}' declares Pagination chrome slot ` +
            `'${paginationSlot.Id}' for data source '${dataSourceId}', but that data source is not a collection query.`,
          reason: `unsupported-collection-pagination-data-source-kind.${paginationSlot.Id}.${dataSourceId}`,
          severity: 'error',
          slot: paginationSlot,
          suggestedNextStep:
            'Point the Pagination chrome slot at a collection-query data source with pagination metadata.',
          view: viewTrace,
        }))
        continue
      }

      if (!dataSource.Query?.Pagination) {
        diagnostics.push(createCollectionChromeSlotDiagnostic({
          details: { dataSourceId, slotId: paginationSlot.Id },
          interpretation: {
            status: 'unbound',
            target: 'projected-collection-runtime.pagination-window',
          },
          message:
            `Collection view '${view.Name}' declares Pagination chrome slot ` +
            `'${paginationSlot.Id}' for '${dataSourceId}', but the data source does not declare query pagination.`,
          reason: `missing-collection-pagination-data-source-policy.${paginationSlot.Id}.${dataSourceId}`,
          severity: 'error',
          slot: paginationSlot,
          suggestedNextStep:
            'Declare DataSource.Query.Pagination so the collection pagination window can be interpreted.',
          view: viewTrace,
        }))
      }
    }
  }

  for (const detailSlot of chrome.findSlots(collectionChromeSlotKinds.detail)) {
    if (!detailSlot.DetailViewId) {
      diagnostics.push(createCollectionChromeSlotDiagnostic({
        details: { slotId: detailSlot.Id },
        interpretation: {
          status: 'unbound',
          target: 'projected-collection-runtime.detail-slot',
        },
        message:
          `Collection view '${view.Name}' declares Detail chrome slot ` +
          `'${detailSlot.Id}', but the slot does not declare DetailViewId.`,
        reason: `missing-collection-detail-slot-view.${detailSlot.Id}`,
        severity: 'error',
        slot: detailSlot,
        suggestedNextStep:
          'Declare DetailViewId on the Detail chrome slot.',
        view: viewTrace,
      }))
      continue
    }

    if (
      detailSlot.DetailActivation == null ||
      isCollectionDetailActivationNone(detailSlot.DetailActivation)
    ) {
      diagnostics.push(createCollectionChromeSlotDiagnostic({
        details: {
          detailActivation: detailSlot.DetailActivation ?? null,
          detailViewId: detailSlot.DetailViewId,
          slotId: detailSlot.Id,
        },
        interpretation: {
          status: 'unbound',
          target: 'projected-collection-runtime.detail-activation',
        },
        message:
          `Collection view '${view.Name}' declares Detail chrome slot ` +
          `'${detailSlot.Id}', but the slot does not declare a detail activation mode.`,
        reason: `missing-collection-detail-slot-activation.${detailSlot.Id}`,
        severity: 'error',
        slot: detailSlot,
        suggestedNextStep:
          'Declare DetailActivation on the Detail chrome slot so the frontend can supply row context.',
        view: viewTrace,
      }))
    }

    if (
      isCollectionDetailActivationStateful(detailSlot.DetailActivation) &&
      !detailSlot.StateId &&
      !chrome.bodySlot?.StateId
    ) {
      diagnostics.push(createCollectionChromeSlotDiagnostic({
        details: {
          detailActivation: detailSlot.DetailActivation,
          detailViewId: detailSlot.DetailViewId,
          slotId: detailSlot.Id,
        },
        interpretation: {
          status: 'unbound',
          target: 'projected-collection-runtime.detail-state',
        },
        message:
          `Collection view '${view.Name}' declares stateful Detail chrome slot ` +
          `'${detailSlot.Id}', but neither the Detail nor Body slot declares StateId.`,
        reason: `missing-collection-detail-slot-state.${detailSlot.Id}`,
        severity: 'warning',
        slot: detailSlot,
        suggestedNextStep:
          'Declare StateId on the Body or Detail chrome slot so detail context can be coordinated with selection state.',
        view: viewTrace,
      }))
    }
  }

  return diagnostics
}

function projectCollectionChromeSlotReferenceDiagnostics(
  module: PresentationModuleDefinition,
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
  slot: CollectionChromeSlotDefinition,
): readonly PresentationProjectionDiagnostic[] {
  const diagnostics: PresentationProjectionDiagnostic[] = []

  for (const dataSourceId of slot.DataSourceIds) {
    if (!findPresentationDataSource(module, dataSourceId)) {
      diagnostics.push(createCollectionChromeSlotDiagnostic({
        details: { dataSourceId, slotId: slot.Id },
        message:
          `Collection chrome slot '${slot.Id}' on '${view.Name}' references ` +
          `data source '${dataSourceId}', but that data source is not present in the presentation module.`,
        reason: `missing-collection-chrome-slot-data-source.${slot.Id}.${dataSourceId}`,
        severity: 'error',
        slot,
        view: viewTrace,
      }))
    }
  }

  if (slot.QueryFormId && !findPresentationQueryForm(module, slot.QueryFormId)) {
    diagnostics.push(createCollectionChromeSlotDiagnostic({
      details: { queryFormId: slot.QueryFormId, slotId: slot.Id },
      message:
        `Collection chrome slot '${slot.Id}' on '${view.Name}' references ` +
        `query form '${slot.QueryFormId}', but that query form is not present in the presentation module.`,
      reason: `missing-collection-chrome-slot-query-form.${slot.Id}.${slot.QueryFormId}`,
      severity: 'error',
      slot,
      view: viewTrace,
    }))
  }

  if (slot.DetailViewId && !findPresentationView(module, slot.DetailViewId)) {
    diagnostics.push(createCollectionChromeSlotDiagnostic({
      details: { detailViewId: slot.DetailViewId, slotId: slot.Id },
      message:
        `Collection chrome slot '${slot.Id}' on '${view.Name}' references ` +
        `detail view '${slot.DetailViewId}', but that view is not present in the presentation module.`,
      reason: `missing-collection-chrome-slot-detail-view.${slot.Id}.${slot.DetailViewId}`,
      severity: 'error',
      slot,
      view: viewTrace,
    }))
  }

  for (const fieldId of slot.FieldIds) {
    if (!findPresentationField(module, fieldId)) {
      diagnostics.push(createCollectionChromeSlotDiagnostic({
        details: { fieldId, slotId: slot.Id },
        message:
          `Collection chrome slot '${slot.Id}' on '${view.Name}' references ` +
          `field '${fieldId}', but that field is not present in the presentation module.`,
        reason: `missing-collection-chrome-slot-field.${slot.Id}.${fieldId}`,
        severity: 'error',
        slot,
        view: viewTrace,
      }))
    }
  }

  for (const column of slot.Columns) {
    if (!findPresentationField(module, column.FieldId)) {
      diagnostics.push(createCollectionChromeSlotDiagnostic({
        details: {
          columnId: column.Id,
          fieldId: column.FieldId,
          slotId: slot.Id,
        },
        message:
          `Collection chrome slot '${slot.Id}' on '${view.Name}' declares ` +
          `column '${column.Id}' with field '${column.FieldId}', but that field is not present in the presentation module.`,
        reason: `missing-collection-chrome-slot-column-field.${slot.Id}.${column.Id}.${column.FieldId}`,
        severity: 'error',
        slot,
        view: viewTrace,
      }))
    }
  }

  return diagnostics
}

function projectCollectionChromeSlotInterpretationDiagnostics(
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
  slot: CollectionChromeSlotDefinition,
  collectionChromeSlotRendererKeys: readonly string[],
): readonly PresentationProjectionDiagnostic[] {
  const candidateRendererKeys = getCollectionChromeSlotRendererCandidateKeys(slot)
  if (hasCollectionChromeSlotRendererBinding(collectionChromeSlotRendererKeys, slot)) {
    return [
      createCollectionChromeSlotDiagnostic({
        category: 'local-interpretation',
        details: {
          candidateRendererKeys,
          slotId: slot.Id,
          slotKind: slot.Kind,
          slotPlacement: slot.Placement,
        },
        interpretation: {
          status: 'bound',
          target: 'collection-chrome-slot-renderer-registry',
        },
        message:
          `Collection chrome slot '${slot.Id}' on '${view.Name}' is bound ` +
          'by the collection chrome slot renderer registry.',
        reason: `bound-collection-chrome-slot-renderer.${slot.Id}`,
        severity: 'info',
        slot,
        view: viewTrace,
      }),
    ]
  }

  if (isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.custom)) {
    return [
      createCollectionChromeSlotDiagnostic({
        category: 'missing-binding',
        details: {
          slotId: slot.Id,
          slotKind: slot.Kind,
          slotPlacement: slot.Placement,
        },
        interpretation: {
          status: 'unbound',
          target: 'projected-collection-runtime.chrome.custom-slot',
        },
        message:
          `Collection chrome slot '${slot.Id}' on '${view.Name}' is Custom, ` +
          'but no frontend collection chrome-slot interpreter is registered for it.',
        reason: `unbound-collection-chrome-custom-slot.${slot.Id}`,
        severity: 'warning',
        slot,
        suggestedNextStep:
          'Register a collection chrome-slot renderer for this custom slot or model it with a first-class slot kind.',
        view: viewTrace,
      }),
    ]
  }

  if (
    isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.pagination) &&
    !isCollectionChromeSlotPlacement(slot, collectionChromeSlotPlacements.footer)
  ) {
    return [
      createCollectionChromeSlotDiagnostic({
        category: 'missing-binding',
        details: {
          slotId: slot.Id,
          slotKind: slot.Kind,
          slotPlacement: slot.Placement,
        },
        interpretation: {
          status: 'unbound',
          target: 'projected-collection-runtime.chrome.pagination-slot',
        },
        message:
          `Collection chrome slot '${slot.Id}' on '${view.Name}' places pagination ` +
          'outside the footer, but the current collection chrome interpreter only binds footer pagination.',
        reason: `unbound-collection-pagination-slot-placement.${slot.Id}`,
        severity: 'warning',
        slot,
        suggestedNextStep:
          'Add a collection chrome-slot renderer for this pagination placement or use Footer placement.',
        view: viewTrace,
      }),
    ]
  }

  if (isCollectionChromeSlotRendererExpected(slot)) {
    return [
      createCollectionChromeSlotDiagnostic({
        category: 'missing-binding',
        details: {
          candidateRendererKeys,
          slotId: slot.Id,
          slotKind: slot.Kind,
          slotPlacement: slot.Placement,
        },
        interpretation: {
          status: 'unbound',
          target: 'collection-chrome-slot-renderer-registry',
        },
        message:
          `Collection chrome slot '${slot.Id}' on '${view.Name}' has no ` +
          'registered frontend renderer for its kind and placement.',
        reason: `unbound-collection-chrome-slot-renderer.${slot.Id}`,
        severity: 'warning',
        slot,
        suggestedNextStep:
          'Register a collection chrome-slot renderer or add a generic interpreter for this slot kind.',
        view: viewTrace,
      }),
    ]
  }

  return []
}

function isCollectionChromeSlotRendererExpected(
  slot: CollectionChromeSlotDefinition,
) {
  return isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.queryForm) ||
    isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.pagination) ||
    isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.selectionActions) ||
    isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.rowActions) ||
    isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.detail) ||
    isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.summary) ||
    isCollectionChromeSlotKind(slot, collectionChromeSlotKinds.body)
}

function createCollectionChromeSlotDiagnostic({
  category,
  details,
  interpretation,
  message,
  reason,
  severity,
  slot,
  suggestedNextStep,
  view,
}: {
  readonly category?: PresentationProjectionDiagnostic['category']
  readonly details?: Readonly<Record<string, unknown>>
  readonly interpretation?: PresentationProjectionDiagnostic['interpretation']
  readonly message: string
  readonly reason: string
  readonly severity: PresentationProjectionDiagnostic['severity']
  readonly slot: CollectionChromeSlotDefinition
  readonly suggestedNextStep?: string
  readonly view: PresentationProjectionTraceView
}) {
  return createViewDiagnostic({
    category,
    details: {
      collectionChromeSlotId: slot.Id,
      collectionChromeSlotKind: slot.Kind,
      collectionChromeSlotPlacement: slot.Placement,
      ...details,
    },
    interpretation,
    message,
    reason,
    severity,
    source: 'presentation-collection-chrome-coverage',
    suggestedNextStep,
    view,
  })
}

function projectCollectionRowActionPredicateCoverageDiagnostics(
  rowAction: CollectionRowActionDefinition,
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
): readonly PresentationProjectionDiagnostic[] {
  return [
    ...projectCollectionRowActionPredicateDiagnostic({
      predicate: rowAction.IsEnabled,
      predicateName: 'IsEnabled',
      rowAction,
      view,
      viewTrace,
    }),
    ...projectCollectionRowActionPredicateDiagnostic({
      predicate: rowAction.IsVisible,
      predicateName: 'IsVisible',
      rowAction,
      view,
      viewTrace,
    }),
  ]
}

function projectCollectionSelectionActionPredicateCoverageDiagnostics(
  selectionAction: CollectionSelectionActionDefinition,
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
): readonly PresentationProjectionDiagnostic[] {
  return [
    ...projectCollectionSelectionActionPredicateDiagnostic({
      predicate: selectionAction.IsEnabled,
      predicateName: 'IsEnabled',
      selectionAction,
      view,
      viewTrace,
    }),
    ...projectCollectionSelectionActionPredicateDiagnostic({
      predicate: selectionAction.IsVisible,
      predicateName: 'IsVisible',
      selectionAction,
      view,
      viewTrace,
    }),
  ]
}

function projectCollectionRowActionPredicateDiagnostic({
  predicate,
  predicateName,
  rowAction,
  view,
  viewTrace,
}: {
  readonly predicate: PresentationValueDefinition | null | undefined
  readonly predicateName: 'IsEnabled' | 'IsVisible'
  readonly rowAction: CollectionRowActionDefinition
  readonly view: ViewDefinition
  readonly viewTrace: PresentationProjectionTraceView
}): readonly PresentationProjectionDiagnostic[] {
  if (!predicate || isInterpretableCollectionRowActionPredicate(predicate)) {
    return []
  }

  return [
    createViewDiagnostic({
      category: 'missing-binding',
      details: {
        actionId: rowAction.ActionId,
        expression: predicate.Expression,
        field: predicate.Field,
        predicateKind: predicate.Kind,
        predicateName,
        rowActionId: rowAction.Id,
        stateId: predicate.StateId,
      },
      interpretation: {
        status: 'unbound',
        target: 'collection-row-action-predicate',
      },
      message:
        `Collection row action '${rowAction.Id}' on '${view.Name}' declares ` +
        `${predicateName}, but the frontend collection interpreter only binds literal and row-field predicates.`,
      reason: `unbound-collection-row-action-predicate.${rowAction.Id}.${predicateName}`,
      severity: 'warning',
      source: 'presentation-collection-coverage',
      suggestedNextStep:
        'Bind this predicate through a target expression interpreter or express it as a literal/row-field predicate.',
      view: viewTrace,
    }),
  ]
}

function projectCollectionSelectionActionPredicateDiagnostic({
  predicate,
  predicateName,
  selectionAction,
  view,
  viewTrace,
}: {
  readonly predicate: PresentationValueDefinition | null | undefined
  readonly predicateName: 'IsEnabled' | 'IsVisible'
  readonly selectionAction: CollectionSelectionActionDefinition
  readonly view: ViewDefinition
  readonly viewTrace: PresentationProjectionTraceView
}): readonly PresentationProjectionDiagnostic[] {
  if (!predicate || isInterpretableCollectionRowActionPredicate(predicate)) {
    return []
  }

  return [
    createViewDiagnostic({
      category: 'missing-binding',
      details: {
        actionId: selectionAction.ActionId,
        expression: predicate.Expression,
        field: predicate.Field,
        predicateKind: predicate.Kind,
        predicateName,
        selectionActionId: selectionAction.Id,
        stateId: predicate.StateId,
      },
      interpretation: {
        status: 'unbound',
        target: 'collection-selection-action-predicate',
      },
      message:
        `Collection selection action '${selectionAction.Id}' on '${view.Name}' declares ` +
        `${predicateName}, but the frontend collection interpreter only binds literal and selected-row-field predicates.`,
      reason: `unbound-collection-selection-action-predicate.${selectionAction.Id}.${predicateName}`,
      severity: 'warning',
      source: 'presentation-collection-coverage',
      suggestedNextStep:
        'Bind this predicate through a target expression interpreter or express it as a literal/selected-row-field predicate.',
      view: viewTrace,
    }),
  ]
}

function isCollectionSelectionEnabled(selectionMode: unknown) {
  return selectionMode === collectionSelectionModes.single ||
    selectionMode === collectionSelectionModes.multiple ||
    String(selectionMode).toLocaleLowerCase() === 'single' ||
    String(selectionMode).toLocaleLowerCase() === 'multiple'
}

function isInterpretableCollectionRowActionPredicate(predicate: PresentationValueDefinition) {
  return predicate.Kind === presentationValueKinds.literal ||
    predicate.Kind === presentationValueKinds.field ||
    String(predicate.Kind).toLocaleLowerCase() === 'literal' ||
    String(predicate.Kind).toLocaleLowerCase() === 'field'
}

function isSelectedRowValueSelectionActionParameter(source: unknown) {
  return source === collectionSelectionActionParameterSources.selectedRowValue ||
    source === collectionSelectionActionParameterSources.selectedRowValueList ||
    String(source).toLocaleLowerCase() === 'selectedrowvalue' ||
    String(source).toLocaleLowerCase() === 'selectedrowvaluelist'
}

function isCollectionDetailActivationUnsupported(detailActivation: unknown) {
  return detailActivation === collectionDetailActivations.none ||
    detailActivation === collectionDetailActivations.hover ||
    String(detailActivation).toLocaleLowerCase() === 'none' ||
    String(detailActivation).toLocaleLowerCase() === 'hover'
}

function isCollectionDetailActivationNone(detailActivation: unknown) {
  return detailActivation === collectionDetailActivations.none ||
    String(detailActivation).toLocaleLowerCase() === 'none'
}

function isCollectionDetailActivationSelection(detailActivation: unknown) {
  return detailActivation === collectionDetailActivations.selection ||
    String(detailActivation).toLocaleLowerCase() === 'selection'
}

function isCollectionDetailActivationStateful(detailActivation: unknown) {
  return isCollectionDetailActivationSelection(detailActivation) ||
    detailActivation === collectionDetailActivations.rowActivation ||
    String(detailActivation).toLocaleLowerCase() === 'rowactivation'
}

function isPrimaryCollectionRowAction(rowAction: CollectionRowActionDefinition) {
  return rowAction.Kind === collectionRowActionKinds.primary ||
    String(rowAction.Kind).toLocaleLowerCase() === 'primary'
}

function isCollectionQueryDataSource(
  dataSource: PresentationModuleDefinition['DataSources'][number],
) {
  return dataSource.Kind === dataSourceKinds.collectionQuery ||
    String(dataSource.Kind).toLocaleLowerCase() === 'collectionquery'
}

function resolveViewProjectedFieldRefs(view: ViewDefinition) {
  const refs = getPresentationViewProjectedFieldRefs(view)
  const seen = new Set<string>()
  return refs.filter((ref) => {
    const key = `${ref.source}:${ref.fieldId}`
    if (seen.has(key)) {
      return false
    }

    seen.add(key)
    return true
  })
}

function projectActionCoverageDiagnostics(
  module: PresentationModuleDefinition,
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
): readonly PresentationProjectionDiagnostic[] {
  return getPresentationViewProjectedActions(view).flatMap((actionRef) =>
    projectActionPlacementCoverageDiagnostics(module, actionRef.placement, viewTrace, {
      actionKind: actionRef.kind,
      actionContextKind: actionRef.contextKind,
      actionSource: actionRef.source,
      collectionSlotId: actionRef.slotId ?? null,
    }),
  )
}

function projectActionPlacementCoverageDiagnostics(
  module: PresentationModuleDefinition,
  placement: ActionPlacementDefinition,
  viewTrace: PresentationProjectionTraceView,
  details?: Readonly<Record<string, unknown>>,
): readonly PresentationProjectionDiagnostic[] {
  const action = findPresentationAction<ActionDefinition>(module, placement.ActionId)
  if (!action) {
    return [
      createViewDiagnostic({
        details: {
          actionId: placement.ActionId,
          ...details,
          placementRegion: placement.Region,
        },
        message: `View '${viewTrace.name}' places action '${placement.ActionId}', but the action is not present in the action catalog.`,
        reason: `missing-action.${placement.ActionId}`,
        severity: 'error',
        source: 'presentation-action-coverage',
        view: viewTrace,
      }),
    ]
  }

  const diagnostics: PresentationProjectionDiagnostic[] = []
  const flowId = action.Preparation?.FlowId
  if (flowId && !findPresentationFlow(module, flowId)) {
    diagnostics.push(createViewDiagnostic({
      details: {
        actionId: action.Id,
        flowId,
      },
      message: `Action '${action.Name}' references flow '${flowId}', but that flow is not present in the presentation module.`,
      reason: `missing-action-flow.${action.Id}.${flowId}`,
      severity: 'error',
      source: 'presentation-action-coverage',
      view: viewTrace,
    }))
  }

  for (const request of action.EndpointRequests) {
    if (
      request.DataSourceId &&
      !findPresentationDataSource(module, request.DataSourceId)
    ) {
      diagnostics.push(createViewDiagnostic({
        details: {
          actionId: action.Id,
          dataSourceId: request.DataSourceId,
        },
        message: `Action '${action.Name}' references data source '${request.DataSourceId}', but that data source is not present in the presentation module.`,
        reason: `missing-action-data-source.${action.Id}.${request.DataSourceId}`,
        severity: 'error',
        source: 'presentation-action-coverage',
        view: viewTrace,
      }))
    }
  }

  if (
    isLocalStateAction(action) &&
    !action.Semantics &&
    !action.Annotations.some((annotation) =>
      String(annotation.Name).toLocaleLowerCase().includes('frontend'))
  ) {
    diagnostics.push(createViewDiagnostic({
      category: 'local-interpretation',
      details: {
        actionId: action.Id,
        bindingKind: action.Binding.Kind,
      },
      interpretation: {
        status: 'locally-interpreted',
        target: 'local-action-runtime',
      },
      message: `Local action '${action.Name}' has no first-class semantics; it requires an implicit frontend binding.`,
      reason: `local-action-without-semantics.${action.Id}`,
      severity: 'info',
      source: 'presentation-action-coverage',
      suggestedNextStep:
        'Declare action semantics in the IR and leave only the component-specific interpretation in the frontend adapter.',
      view: viewTrace,
    }))
  }

  return diagnostics
}

function projectInputFormCoverageDiagnostics(
  module: PresentationModuleDefinition,
  view: ViewDefinition,
  viewTrace: PresentationProjectionTraceView,
  queryFormStateAdapterIds: readonly string[],
): readonly PresentationProjectionDiagnostic[] {
  const inputFormRefs = resolveViewInputFormRefs(module, view)
  return inputFormRefs.flatMap(({ form, formId, queryFormId, source }) => {
    if (!form) {
      return [
        createViewDiagnostic({
          details: { formId, source },
          message: `View '${view.Name}' references input form '${formId}', but that form is not present in the presentation module.`,
          reason: `missing-input-form.${formId}`,
          severity: 'error',
          source: 'presentation-input-form-coverage',
          view: viewTrace,
        }),
      ]
    }

    return [
      ...projectInputFormFieldCoverageDiagnostics(module, form, viewTrace),
      ...projectInputFormActionCoverageDiagnostics(module, form, viewTrace),
      ...projectQueryFormStateAdapterCoverageDiagnostics({
        queryFormId,
        queryFormStateAdapterIds,
        viewTrace,
      }),
    ]
  })
}

function resolveViewInputFormRefs(
  module: PresentationModuleDefinition,
  view: ViewDefinition,
): readonly {
  readonly form: InputFormDefinition | null
  readonly formId: string
  readonly queryFormId: string | null
  readonly source: string
}[] {
  const directInputFormId = view.Subject.InputFormId
  if (directInputFormId) {
    return [{
      form: findPresentationInputForm<InputFormDefinition>(module, directInputFormId),
      formId: directInputFormId,
      queryFormId: null,
      source: 'view-subject',
    }]
  }

  const queryFormRefs = [
    ...(view.Subject.QueryFormId
      ? [{ queryFormId: view.Subject.QueryFormId, source: 'view-subject' }]
      : []),
    ...createCollectionChromeRuntime(view.Collection)
      .findSlots(collectionChromeSlotKinds.queryForm)
      .flatMap((slot) =>
        slot.QueryFormId
          ? [{
              queryFormId: slot.QueryFormId,
              source: `collection-chrome-slot:${slot.Id}`,
            }]
          : []),
  ]
  const seenQueryFormIds = new Set<string>()

  return queryFormRefs.flatMap(({ queryFormId, source }) => {
    if (seenQueryFormIds.has(queryFormId)) {
      return []
    }
    seenQueryFormIds.add(queryFormId)

    const queryForm = findPresentationQueryForm<QueryFormDefinition>(module, queryFormId)
    if (!queryForm) {
      return [{
        form: null,
        formId: queryFormId,
        queryFormId,
        source,
      }]
    }

    return [{
      form: findPresentationInputForm<InputFormDefinition>(module, queryForm.FormId),
      formId: queryForm.FormId,
      queryFormId,
      source,
    }]
  })
}

function projectInputFormFieldCoverageDiagnostics(
  module: PresentationModuleDefinition,
  form: InputFormDefinition,
  viewTrace: PresentationProjectionTraceView,
): readonly PresentationProjectionDiagnostic[] {
  const inputFieldIds = new Set(form.Fields.map((field) => field.Id))
  const diagnostics = form.Fields.flatMap((field) => {
    const presentationField = findPresentationField(module, field.FieldId)
    const choiceDataSourceId = field.ChoiceSource?.DataSourceId
    return [
      ...(presentationField
        ? []
        : [
            createViewDiagnostic({
              details: {
                fieldId: field.FieldId,
                formId: form.Id,
                inputFieldId: field.Id,
              },
              message: `Input form '${form.Name}' references field '${field.FieldId}', but that field is not present in the presentation module.`,
              reason: `missing-input-form-field.${form.Id}.${field.Id}`,
              severity: 'error' as const,
              source: 'presentation-input-form-coverage',
              view: viewTrace,
            }),
          ]),
      ...(choiceDataSourceId && !findPresentationDataSource(module, choiceDataSourceId)
        ? [
            createViewDiagnostic({
              details: {
                choiceDataSourceId,
                formId: form.Id,
                inputFieldId: field.Id,
              },
              message: `Input form '${form.Name}' field '${field.Name}' references choice data source '${choiceDataSourceId}', but that data source is not present in the presentation module.`,
              reason: `missing-input-form-choice-data-source.${form.Id}.${field.Id}`,
              severity: 'error' as const,
              source: 'presentation-input-form-coverage',
              view: viewTrace,
            }),
          ]
        : []),
    ]
  })

  return [
    ...diagnostics,
    ...form.Groups.flatMap((group) =>
      group.FieldIds.filter((fieldId) => !inputFieldIds.has(fieldId)).map((fieldId) =>
        createViewDiagnostic({
          details: {
            formId: form.Id,
            groupId: group.Id,
            inputFieldId: fieldId,
          },
          message: `Input form '${form.Name}' group '${group.Name}' references field '${fieldId}', but that input field is not present in the form.`,
          reason: `missing-input-form-group-field.${form.Id}.${group.Id}.${fieldId}`,
          severity: 'error',
          source: 'presentation-input-form-coverage',
          view: viewTrace,
        }),
      ),
    ),
  ]
}

function projectInputFormActionCoverageDiagnostics(
  module: PresentationModuleDefinition,
  form: InputFormDefinition,
  viewTrace: PresentationProjectionTraceView,
): readonly PresentationProjectionDiagnostic[] {
  return form.Actions.flatMap((placement) =>
    findPresentationAction(module, placement.ActionId)
      ? []
      : [
          createViewDiagnostic({
            details: {
              actionId: placement.ActionId,
              formId: form.Id,
              placementRegion: placement.Region,
            },
            message: `Input form '${form.Name}' places action '${placement.ActionId}', but the action is not present in the action catalog.`,
            reason: `missing-input-form-action.${form.Id}.${placement.ActionId}`,
            severity: 'error',
            source: 'presentation-input-form-coverage',
            view: viewTrace,
          }),
        ],
  )
}

function projectQueryFormStateAdapterCoverageDiagnostics({
  queryFormId,
  queryFormStateAdapterIds,
  viewTrace,
}: {
  readonly queryFormId: string | null
  readonly queryFormStateAdapterIds: readonly string[]
  readonly viewTrace: PresentationProjectionTraceView
}): readonly PresentationProjectionDiagnostic[] {
  if (!queryFormId || queryFormStateAdapterIds.includes(queryFormId)) {
    return []
  }

  return [
    createViewDiagnostic({
      category: 'missing-binding',
      details: { queryFormId },
      interpretation: {
        status: 'unbound',
        target: 'query-form-state-adapter',
      },
      message:
        `Query form '${queryFormId}' is projected into view '${viewTrace.name}', ` +
        'but no frontend state adapter is registered for it.',
      reason: `missing-query-form-state-adapter.${queryFormId}`,
      severity: 'warning',
      source: 'presentation-query-form-coverage',
      suggestedNextStep:
        'Register a query-form state adapter or project a generic relation-query state runtime for this form.',
      view: viewTrace,
    }),
  ]
}

function isLocalStateAction(action: ActionDefinition) {
  return (
    action.Kind === actionKinds.localStateAction ||
    String(action.Kind).toLocaleLowerCase() === 'localstateaction' ||
    action.Binding.Kind === presentationBindingKinds.localState ||
    String(action.Binding.Kind).toLocaleLowerCase() === 'localstate'
  )
}

function isUnboundDataSourceBinding(binding: PresentationDataSourceBinding) {
  const authorization = binding.authorization
  const blockedLabel =
    authorization.kind === 'required'
      ? authorization.blockedLabel ?? binding.blockedLabel
      : binding.blockedLabel

  return (
    binding.kind === presentationDataSourceBindingKinds.localValue &&
    authorization.kind === 'required' &&
    authorization.isAuthorized === false &&
    Boolean(blockedLabel?.startsWith('No frontend binding is registered'))
  )
}

function createViewDiagnostic({
  category,
  details,
  interpretation,
  message,
  reason,
  severity,
  source,
  suggestedNextStep,
  view,
}: {
  readonly category?: PresentationProjectionDiagnostic['category']
  readonly details?: Readonly<Record<string, unknown>>
  readonly interpretation?: PresentationProjectionDiagnostic['interpretation']
  readonly message: string
  readonly reason: string
  readonly severity: PresentationProjectionDiagnostic['severity']
  readonly source: string
  readonly suggestedNextStep?: string
  readonly view: PresentationProjectionTraceView
}) {
  return createDiagnostic({
    category,
    details,
    id: `view.${view.id}.${reason}`,
    interpretation,
    message,
    severity,
    source,
    subject: {
      id: view.id,
      kind: 'view',
      name: view.name,
    },
    suggestedNextStep,
  })
}

function createDiagnostic({
  category,
  details,
  id,
  interpretation,
  message,
  severity,
  source,
  subject,
  suggestedNextStep,
}: PresentationProjectionDiagnostic): PresentationProjectionDiagnostic {
  return {
    category,
    details,
    id,
    interpretation,
    message,
    severity,
    source,
    subject,
    suggestedNextStep,
  }
}

function normalizeCoverageModule(
  module: PresentationProjectionCoverageModule,
): PresentationModuleDefinition {
  return {
    Actions: [...(module.Actions ?? [])],
    Annotations: [],
    DataSources: [...(module.DataSources ?? [])],
    DesignSystems: [],
    Expressions: [],
    Fields: [...(module.Fields ?? [])],
    Flows: [...(module.Flows ?? [])],
    Id: 'projection-coverage',
    InputForms: [...(module.InputForms ?? [])],
    Name: 'Projection Coverage',
    Navigation: [],
    QueryForms: [...(module.QueryForms ?? [])],
    Targets: [],
    Version: null,
    Views: [...module.Views],
    Workspaces: [],
  }
}
