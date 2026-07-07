/* eslint-disable react-refresh/only-export-components */

import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useState,
  type ReactNode,
  type SetStateAction,
} from 'react'

import {
  findPresentationDocumentProfile,
  findPresentationView,
  findPresentationWorkspace,
  type DocumentProfileDefinition,
  type PresentationModuleDefinition,
  type ProjectionDefinition,
  type ViewDefinition,
  type WorkspaceDefinition,
  type WorkspaceRefDefinition,
} from '@cohesivesystems/presentation-core'
import { usePresentationModule } from './presentation-module-context'
import {
  createPresentationEnumDiscriminator,
  findPresentationComponentBinding,
  matchesPresentationEnum,
} from '@cohesivesystems/presentation-core'
import {
  coordinationActionKinds,
  coordinationTriggerKinds,
  presentationBindingKinds,
  presentationTargetKinds,
  projectionKindLabels,
  projectionSubjectKindLabels,
  semanticReferenceKindLabels,
} from '@cohesivesystems/presentation-contracts'
import type { CoordinationDefinition } from '@cohesivesystems/presentation-contracts'

/**
 * Client-side display mode for a document workspace. The available modes are
 * declared by the IR, but the active mode is runtime state owned by the
 * projection boundary.
 */
export type DocumentWorkspaceLayout = 'single' | 'split'

/**
 * A semantic path is a renderer-neutral addressing for a document location. Tree
 * renderers publish these paths so sibling projections can coordinate without
 * depending on each other's concrete component state.
 */
export interface DocumentWorkspaceSemanticPathSegment {
  readonly Kind?: string | number | null
  readonly Id: string
  readonly Label?: string | null
}

export interface DocumentWorkspaceSemanticPath {
  readonly Segments: readonly DocumentWorkspaceSemanticPathSegment[]
}

export interface DocumentWorkspaceSemanticReference {
  readonly Kind?: string | number | null
  readonly Id: string
  readonly Path?: DocumentWorkspaceSemanticPath | null
}

export interface DocumentWorkspaceSemanticSelection {
  readonly Target: DocumentWorkspaceSemanticReference
  readonly SourceProjectionId?: string | null
  readonly SemanticPath?: DocumentWorkspaceSemanticPath | null
}

export interface DocumentWorkspaceSemanticTreeNode {
  readonly itemId: string
  readonly relatedSemanticReferences?: readonly DocumentWorkspaceSemanticReference[]
  readonly semanticReference: DocumentWorkspaceSemanticReference
}

export interface DocumentWorkspaceNavigationHistory {
  readonly entries: readonly string[]
  readonly index: number
}

export interface DocumentWorkspaceTreeSearchState {
  readonly activeMatchIndex: number
  readonly isOpen: boolean
  readonly query: string
}

export interface DocumentWorkspaceTreeStateSnapshot {
  readonly expandedItemIds: readonly string[]
  readonly navigationHistory: DocumentWorkspaceNavigationHistory
  readonly search: DocumentWorkspaceTreeSearchState
}

export interface DocumentWorkspaceRuntimeState {
  /**
   * Cross-projection selections, such as a structure-tree item that another
   * document projection may highlight or navigate to.
   */
  readonly semanticSelections: readonly DocumentWorkspaceSemanticSelection[]
  /**
   * Projection-local interaction state keyed by semantic projection id. Keeping
   * this here lets tree projections remount without losing expansion/search
   * state while the same document workspace instance is active.
   */
  readonly treeStates: Readonly<Record<string, DocumentWorkspaceTreeStateSnapshot>>
}

/**
 * Fully resolved runtime contract for a semantic document workspace. Route
 * hosts should treat this as the projection boundary: React components receive
 * this snapshot after the IR has resolved the page, workspace, document profile,
 * initial projections, and active client state.
 */
export interface DocumentWorkspaceRuntimeSnapshot {
  readonly activeLayoutModeId: string
  readonly activeProjection: ProjectionDefinition | null
  readonly activeProjectionId: string | null
  readonly activeViewId: string | null
  readonly documentProfile: DocumentProfileDefinition
  readonly initialProjectionIds: readonly string[]
  /**
   * Stable key for state that belongs to one opened workspace/document pair.
   * Changing this key intentionally resets local interaction state.
   */
  readonly instanceKey: string
  readonly layout: DocumentWorkspaceLayout
  /**
   * The route/page-level semantic view. This is distinct from workspaceView:
   * pageView owns page chrome such as document summary metrics and actions.
   */
  readonly pageView: ViewDefinition
  /**
   * View ids corresponding to the active document profile projections. These
   * ids drive document tabs/switchers, while projection ids remain the semantic
   * identities used by renderers and persisted projection state.
   */
  readonly projectionViewIds: readonly string[]
  readonly projections: readonly ProjectionDefinition[]
  readonly setActiveViewId: (viewId: string) => void
  readonly setLayout: (layout: DocumentWorkspaceLayout) => void
  readonly state: DocumentWorkspaceRuntimeState
  readonly workspace: WorkspaceDefinition
  readonly workspaceRef: WorkspaceRefDefinition
  /**
   * The semantic workspace surface hosted by the page. This view owns workspace
   * chrome such as view switching, layout switching, and heading trailing state.
   */
  readonly workspaceView: ViewDefinition
}

/**
 * Context passed to concrete projection renderers after semantic projection
 * resolution. `extra` is intentionally supplied by the app-level binding so the
 * runtime can stay independent from domain-specific document data.
 */
export interface DocumentWorkspaceProjectionRendererContext<
  TExtra,
  TComponentSystem = unknown,
  TDesignSystem = unknown,
> {
  readonly componentSystem: TComponentSystem
  readonly designSystem: TDesignSystem
  readonly extra: TExtra
  readonly projection: ProjectionDefinition
  readonly runtime: DocumentWorkspaceRuntimeSnapshot
}

/**
 * React renderer for one semantic document workspace projection.
 *
 * Implementations should interpret the resolved `projection` from the supplied
 * context and render only that projection's view/content. Cross-projection
 * coordination, active workspace state, component-system bindings, and
 * app-specific document data are provided through the context so projection
 * renderers can remain ordinary pure React adapters.
 *
 * @typeParam TExtra - App-specific document data and callbacks supplied by the host.
 * @typeParam TComponentSystem - Concrete component-system type used by the renderer.
 * @typeParam TDesignSystem - Concrete design-system type used by the renderer.
 * @param context - Fully resolved projection rendering context.
 * @returns React content for the projection, or `null` when the projection cannot render.
 */
export type DocumentWorkspaceProjectionRenderer<
  TExtra,
  TComponentSystem = unknown,
  TDesignSystem = unknown,
> = (
  context: DocumentWorkspaceProjectionRendererContext<
    TExtra,
    TComponentSystem,
    TDesignSystem
  >,
) => ReactNode

/**
 * Registry of frontend interpretations for document projections. Resolution is
 * target-first: ordinary projections bind through ProjectionRenderer target
 * bindings declared by the backend IR and interpreted by a frontend component
 * pack. Semantic-kind lookup is retained as a diagnostic fallback for older
 * projections whose target component semantics have not been declared yet.
 */
export interface DocumentWorkspaceProjectionRendererRegistry<
  TExtra,
  TComponentSystem = unknown,
  TDesignSystem = unknown,
> {
  /** Renderers keyed by semantic component roles from ProjectionRenderer target bindings. */
  readonly byComponentRole?: Readonly<
    Record<string, DocumentWorkspaceProjectionRenderer<TExtra, TComponentSystem, TDesignSystem>>
  >
  /** Concrete component-key overrides from ProjectionRenderer target bindings. */
  readonly byComponentKey?: Readonly<
    Record<string, DocumentWorkspaceProjectionRenderer<TExtra, TComponentSystem, TDesignSystem>>
  >
  /** Compatibility renderers keyed by projection kind enum value or label. */
  readonly byProjectionKind?: Readonly<
    Record<string, DocumentWorkspaceProjectionRenderer<TExtra, TComponentSystem, TDesignSystem>>
  >
  /** Compatibility renderers keyed by projection subject kind enum value or label. */
  readonly bySubjectKind?: Readonly<
    Record<string, DocumentWorkspaceProjectionRenderer<TExtra, TComponentSystem, TDesignSystem>>
  >
  /** Compatibility renderers keyed by coordinate semantic-reference kind enum value or label. */
  readonly bySemanticReferenceKind?: Readonly<
    Record<string, DocumentWorkspaceProjectionRenderer<TExtra, TComponentSystem, TDesignSystem>>
  >
  /** Last-resort escape hatch keyed by projection id. */
  readonly byProjectionId?: Readonly<
    Record<string, DocumentWorkspaceProjectionRenderer<TExtra, TComponentSystem, TDesignSystem>>
  >
  /** Last-resort escape hatch keyed by projection view id. */
  readonly byViewId?: Readonly<
    Record<string, DocumentWorkspaceProjectionRenderer<TExtra, TComponentSystem, TDesignSystem>>
  >
  /** Renderer used when no semantic or explicit binding matches. */
  readonly fallback?: DocumentWorkspaceProjectionRenderer<
    TExtra,
    TComponentSystem,
    TDesignSystem
  >
  /** Frontend projection capabilities used by IR coordination diagnostics. */
  readonly capabilities?: DocumentWorkspaceProjectionCapabilityRegistry
}

export interface DocumentWorkspaceProjectionCapabilityRegistry {
  readonly revealSemanticSelection?: DocumentWorkspaceProjectionCapabilityBindings
}

export interface DocumentWorkspaceProjectionCapabilityBindings {
  /** Capability keyed by semantic component roles from ProjectionRenderer target bindings. */
  readonly byComponentRole?: Readonly<Record<string, boolean>>
  /** Capability keyed by concrete component-key overrides from ProjectionRenderer target bindings. */
  readonly byComponentKey?: Readonly<Record<string, boolean>>
  /** Capability keyed by projection kind enum value or label. */
  readonly byProjectionKind?: Readonly<Record<string, boolean>>
  /** Capability keyed by projection subject kind enum value or label. */
  readonly bySubjectKind?: Readonly<Record<string, boolean>>
  /** Capability keyed by coordinate semantic-reference kind enum value or label. */
  readonly bySemanticReferenceKind?: Readonly<Record<string, boolean>>
  /** Last-resort escape hatch keyed by projection id. */
  readonly byProjectionId?: Readonly<Record<string, boolean>>
  /** Last-resort escape hatch keyed by projection view id. */
  readonly byViewId?: Readonly<Record<string, boolean>>
}

export type DocumentWorkspaceProjectionRendererResolutionSource =
  | 'component-key'
  | 'component-role'
  | 'fallback'
  | 'projection-id'
  | 'projection-kind'
  | 'semantic-reference-kind'
  | 'subject-kind'
  | 'view-id'

export interface DocumentWorkspaceProjectionRendererResolution<
  TExtra,
  TComponentSystem = unknown,
  TDesignSystem = unknown,
> {
  readonly componentKey: string | null
  readonly componentRole: string | null
  readonly projection: ProjectionDefinition | null
  readonly renderer:
    | DocumentWorkspaceProjectionRenderer<TExtra, TComponentSystem, TDesignSystem>
    | null
  readonly rendererKey: string | null
  readonly resolutionSource: DocumentWorkspaceProjectionRendererResolutionSource | null
}

export interface RenderDocumentWorkspaceProjectionOptions<
  TComponentSystem = unknown,
  TDesignSystem = unknown,
> {
  readonly componentSet?: string | null
  readonly componentSystem: TComponentSystem
  readonly designSystem: TDesignSystem
  readonly module?: PresentationModuleDefinition | null
}

interface DocumentWorkspaceRuntimeContextValue {
  readonly coordination: readonly CoordinationDefinition[]
  readonly instanceKey: string
  readonly selectSemanticReference: (
    selection: DocumentWorkspaceSemanticSelection,
  ) => void
  readonly setTreeExpandedItemIds: (
    projectionId: string,
    value: SetStateAction<readonly string[]>,
  ) => void
  readonly setTreeNavigationHistory: (
    projectionId: string,
    value: SetStateAction<DocumentWorkspaceNavigationHistory>,
  ) => void
  readonly setTreeSearch: (
    projectionId: string,
    value: SetStateAction<DocumentWorkspaceTreeSearchState>,
  ) => void
  readonly state: DocumentWorkspaceRuntimeState
}

const DocumentWorkspaceRuntimeContext =
  createContext<DocumentWorkspaceRuntimeContextValue | null>(null)

export interface DocumentWorkspaceRuntimeProps {
  readonly children: (runtime: DocumentWorkspaceRuntimeSnapshot) => ReactNode
  readonly documentInstanceId?: string | null
  readonly fallback?: ReactNode
  /**
   * Route/page semantic view id. The runtime resolves the workspace hosted by
   * this page rather than assuming the page itself is the workspace surface.
   */
  readonly pageViewId: string
}

/**
 * Resolves the document workspace declared by the presentation IR and provides
 * the active client-side runtime snapshot to projection renderers. This is the
 * point where a route page becomes a semantic workspace instance.
 */
export function DocumentWorkspaceRuntime({
  children,
  documentInstanceId,
  fallback = null,
  pageViewId,
}: DocumentWorkspaceRuntimeProps) {
  const module = usePresentationModule()
  const resolution = useMemo(
    () => resolveDocumentWorkspace(module, pageViewId),
    [module, pageViewId],
  )
  const documentProfile = resolution?.documentProfile ?? null
  const projections = useMemo(
    () => documentProfile?.Projections ?? [],
    [documentProfile],
  )
  const initialProjectionIds = useMemo(
    () => resolveInitialProjectionIds(resolution?.workspaceRef ?? null, projections),
    [projections, resolution?.workspaceRef],
  )
  const projectionViewIds = useMemo(
    () =>
      initialProjectionIds
        .map((projectionId) => projections.find((projection) => projection.Id === projectionId))
        .map((projection) => projection?.ViewId ?? projection?.Id ?? null)
        .filter((viewId): viewId is string => Boolean(viewId)),
    [initialProjectionIds, projections],
  )
  const defaultProjectionId = initialProjectionIds[0] ?? projections[0]?.Id ?? null
  // Scope runtime state to the semantic workspace/profile and the concrete
  // document instance so navigating between documents does not leak state.
  const instanceKey = [
    resolution?.workspaceRef.WorkspaceId ?? pageViewId,
    resolution?.documentProfile.Id ?? 'default',
    documentInstanceId ?? 'transient',
  ].join(':')
  const [activeProjectionState, setActiveProjectionState] = useState<{
    readonly instanceKey: string
    readonly projectionId: string | null
  }>(() => ({ instanceKey, projectionId: defaultProjectionId }))
  const [layoutState, setLayoutState] = useState<{
    readonly instanceKey: string
    readonly value: DocumentWorkspaceLayout
  }>(() => ({
    instanceKey,
    value: resolveDefaultLayout(resolution?.documentProfile.Layout ?? null),
  }))
  const [runtimeState, setRuntimeState] = useState<{
    readonly instanceKey: string
    readonly value: DocumentWorkspaceRuntimeState
  }>(() => ({ instanceKey, value: createInitialRuntimeState() }))

  const runtimeStateValue =
    runtimeState.instanceKey === instanceKey
      ? runtimeState.value
      : createInitialRuntimeState()
  const coordinationDefinitions = resolution?.documentProfile.Coordination
  const coordination = useMemo(
    () => coordinationDefinitions ?? [],
    [coordinationDefinitions],
  )

  const setTreeExpandedItemIds = useCallback(
    (projectionId: string, value: SetStateAction<readonly string[]>) => {
      setRuntimeState((current) =>
        updateRuntimeTreeState(current, instanceKey, projectionId, (treeState) => ({
          ...treeState,
          expandedItemIds: applySetStateAction(value, treeState.expandedItemIds),
        })),
      )
    },
    [instanceKey],
  )
  const setTreeNavigationHistory = useCallback(
    (
      projectionId: string,
      value: SetStateAction<DocumentWorkspaceNavigationHistory>,
    ) => {
      setRuntimeState((current) =>
        updateRuntimeTreeState(current, instanceKey, projectionId, (treeState) => ({
          ...treeState,
          navigationHistory: applySetStateAction(value, treeState.navigationHistory),
        })),
      )
    },
    [instanceKey],
  )
  const setTreeSearch = useCallback(
    (projectionId: string, value: SetStateAction<DocumentWorkspaceTreeSearchState>) => {
      setRuntimeState((current) =>
        updateRuntimeTreeState(current, instanceKey, projectionId, (treeState) => ({
          ...treeState,
          search: applySetStateAction(value, treeState.search),
        })),
      )
    },
    [instanceKey],
  )
  const selectSemanticReference = useCallback(
    (selection: DocumentWorkspaceSemanticSelection) => {
      setRuntimeState((current) => {
        const base =
          current.instanceKey === instanceKey
            ? current.value
            : createInitialRuntimeState()

        return {
          instanceKey,
          value: {
            ...base,
            semanticSelections: createCoordinatedSemanticSelections(selection, coordination),
          },
        }
      })
    },
    [coordination, instanceKey],
  )
  const contextValue = useMemo<DocumentWorkspaceRuntimeContextValue>(
    () => ({
      coordination,
      instanceKey,
      selectSemanticReference,
      setTreeExpandedItemIds,
      setTreeNavigationHistory,
      setTreeSearch,
      state: runtimeStateValue,
    }),
    [
      coordination,
      instanceKey,
      runtimeStateValue,
      selectSemanticReference,
      setTreeExpandedItemIds,
      setTreeNavigationHistory,
      setTreeSearch,
    ],
  )

  if (!resolution || !defaultProjectionId) {
    return <>{fallback}</>
  }

  const activeProjectionId =
    activeProjectionState.instanceKey === instanceKey &&
    activeProjectionState.projectionId &&
    initialProjectionIds.includes(activeProjectionState.projectionId)
      ? activeProjectionState.projectionId
      : defaultProjectionId
  const activeProjection =
    projections.find((projection) => projection.Id === activeProjectionId) ?? null
  const activeViewId = activeProjection?.ViewId ?? activeProjection?.Id ?? null
  const layout =
    layoutState.instanceKey === instanceKey
      ? layoutState.value
      : resolveDefaultLayout(resolution.documentProfile.Layout ?? null)
  const activeLayoutModeId = resolveActiveLayoutModeId(
    layout,
    resolution.documentProfile.Layout ?? null,
  )

  function setActiveViewId(viewId: string) {
    const projection = projections.find(
      (candidate) => candidate.ViewId === viewId || candidate.Id === viewId,
    )
    if (!projection) {
      return
    }

    setActiveProjectionState({ instanceKey, projectionId: projection.Id })
  }

  function setLayout(value: DocumentWorkspaceLayout) {
    setLayoutState({ instanceKey, value })
  }

  const snapshot: DocumentWorkspaceRuntimeSnapshot = {
    activeLayoutModeId,
    activeProjection,
    activeProjectionId,
    activeViewId,
    documentProfile: resolution.documentProfile,
    initialProjectionIds,
    instanceKey,
    layout,
    pageView: resolution.pageView,
    projectionViewIds,
    projections,
    setActiveViewId,
    setLayout,
    state: runtimeStateValue,
    workspace: resolution.workspace,
    workspaceRef: resolution.workspaceRef,
    workspaceView: resolution.workspaceView,
  }

  return (
    <DocumentWorkspaceRuntimeContext.Provider value={contextValue}>
      {children(snapshot)}
    </DocumentWorkspaceRuntimeContext.Provider>
  )
}

export function renderDocumentWorkspaceProjection<
  TExtra,
  TComponentSystem,
  TDesignSystem,
>(
  runtime: DocumentWorkspaceRuntimeSnapshot,
  renderers: DocumentWorkspaceProjectionRendererRegistry<
    TExtra,
    TComponentSystem,
    TDesignSystem
  >,
  rendererContext: TExtra,
  viewId: string,
  options: RenderDocumentWorkspaceProjectionOptions<TComponentSystem, TDesignSystem>,
) {
  const resolution = resolveDocumentWorkspaceProjectionRenderer({
    componentSet: options.componentSet,
    module: options.module ?? null,
    registry: renderers,
    runtime,
    viewId,
  })

  if (!resolution.projection || !resolution.renderer) {
    return null
  }

  return resolution.renderer({
    componentSystem: options.componentSystem,
    designSystem: options.designSystem,
    extra: rendererContext,
    projection: resolution.projection,
    runtime,
  })
}

export function resolveDocumentWorkspaceProjectionRenderer<
  TExtra,
  TComponentSystem = unknown,
  TDesignSystem = unknown,
>({
  componentSet,
  module,
  registry,
  runtime,
  viewId,
}: {
  readonly componentSet?: string | null
  readonly module?: PresentationModuleDefinition | null
  readonly registry: DocumentWorkspaceProjectionRendererRegistry<
    TExtra,
    TComponentSystem,
    TDesignSystem
  >
  readonly runtime: DocumentWorkspaceRuntimeSnapshot
  readonly viewId: string
}): DocumentWorkspaceProjectionRendererResolution<TExtra, TComponentSystem, TDesignSystem> {
  // View ids are used by workspace chrome, while projection ids and renderer
  // keys are semantic/runtime binding points. Accept either id at this boundary.
  const projection = runtime.projections.find(
    (candidate) => candidate.ViewId === viewId || candidate.Id === viewId,
  )
  if (!projection) {
    return createDocumentProjectionRendererResolution(null)
  }

  const componentBinding = resolveDocumentWorkspaceProjectionComponentBinding(
    module ?? null,
    projection,
    componentSet,
  )
  const componentKey = componentBinding?.componentKey ?? null
  const componentRole = componentBinding?.componentRole ?? null
  const rendererKey = projection.RendererKey ?? null

  const componentRoleRenderer = componentRole
    ? registry.byComponentRole?.[componentRole]
    : undefined
  if (componentRoleRenderer) {
    return {
      componentKey,
      componentRole,
      projection,
      renderer: componentRoleRenderer,
      rendererKey,
      resolutionSource: 'component-role',
    }
  }

  const componentRenderer = componentKey
    ? registry.byComponentKey?.[componentKey]
    : undefined
  if (componentRenderer) {
    return {
      componentKey,
      componentRole,
      projection,
      renderer: componentRenderer,
      rendererKey,
      resolutionSource: 'component-key',
    }
  }

  const semanticReferenceRenderer = findDiscriminatorRenderer(
    registry.bySemanticReferenceKind,
    projection.Coordinates?.SemanticReferenceKind,
    semanticReferenceKindLabels,
  )
  if (semanticReferenceRenderer) {
    return {
      componentKey,
      componentRole,
      projection,
      renderer: semanticReferenceRenderer,
      rendererKey,
      resolutionSource: 'semantic-reference-kind',
    }
  }

  const subjectKindRenderer = findDiscriminatorRenderer(
    registry.bySubjectKind,
    projection.Subject.Kind,
    projectionSubjectKindLabels,
  )
  if (subjectKindRenderer) {
    return {
      componentKey,
      componentRole,
      projection,
      renderer: subjectKindRenderer,
      rendererKey,
      resolutionSource: 'subject-kind',
    }
  }

  const projectionKindRenderer = findDiscriminatorRenderer(
    registry.byProjectionKind,
    projection.Kind,
    projectionKindLabels,
  )
  if (projectionKindRenderer) {
    return {
      componentKey,
      componentRole,
      projection,
      renderer: projectionKindRenderer,
      rendererKey,
      resolutionSource: 'projection-kind',
    }
  }

  const projectionIdRenderer = registry.byProjectionId?.[projection.Id]
  if (projectionIdRenderer) {
    return {
      componentKey,
      componentRole,
      projection,
      renderer: projectionIdRenderer,
      rendererKey,
      resolutionSource: 'projection-id',
    }
  }

  const viewIdRenderer = projection.ViewId ? registry.byViewId?.[projection.ViewId] : undefined
  if (viewIdRenderer) {
    return {
      componentKey,
      componentRole,
      projection,
      renderer: viewIdRenderer,
      rendererKey,
      resolutionSource: 'view-id',
    }
  }

  if (registry.fallback) {
    return {
      componentKey,
      componentRole,
      projection,
      renderer: registry.fallback,
      rendererKey,
      resolutionSource: 'fallback',
    }
  }

  return {
    componentKey,
    componentRole,
    projection,
    renderer: null,
    rendererKey,
    resolutionSource: null,
  }
}

export function resolveDocumentWorkspaceProjectionCapability<
  TExtra,
  TComponentSystem = unknown,
  TDesignSystem = unknown,
>({
  capability,
  componentSet,
  module,
  projection,
  registry,
}: {
  readonly capability: keyof DocumentWorkspaceProjectionCapabilityRegistry
  readonly componentSet?: string | null
  readonly module?: PresentationModuleDefinition | null
  readonly projection: ProjectionDefinition
  readonly registry: DocumentWorkspaceProjectionRendererRegistry<
    TExtra,
    TComponentSystem,
    TDesignSystem
  >
}) {
  const bindings = registry.capabilities?.[capability]
  if (!bindings) {
    return false
  }

  const componentBinding = resolveDocumentWorkspaceProjectionComponentBinding(
    module ?? null,
    projection,
    componentSet,
  )
  const componentKey = componentBinding?.componentKey ?? null
  const componentRole = componentBinding?.componentRole ?? null

  if (componentRole && bindings.byComponentRole?.[componentRole]) {
    return true
  }

  if (componentKey && bindings.byComponentKey?.[componentKey]) {
    return true
  }

  if (findDiscriminatorRenderer(
    bindings.bySemanticReferenceKind,
    projection.Coordinates?.SemanticReferenceKind,
    semanticReferenceKindLabels,
  )) {
    return true
  }

  if (
    findDiscriminatorRenderer(
      bindings.bySubjectKind,
      projection.Subject.Kind,
      projectionSubjectKindLabels,
    )
  ) {
    return true
  }

  if (
    findDiscriminatorRenderer(
      bindings.byProjectionKind,
      projection.Kind,
      projectionKindLabels,
    )
  ) {
    return true
  }

  return Boolean(
    bindings.byProjectionId?.[projection.Id] ||
      (projection.ViewId ? bindings.byViewId?.[projection.ViewId] : false),
  )
}

/**
 * Resolves the frontend renderer role and optional concrete component override
 * declared by a presentation target binding for a document projection.
 */
function resolveDocumentWorkspaceProjectionComponentBinding(
  module: PresentationModuleDefinition | null,
  projection: ProjectionDefinition,
  componentSet?: string | null,
) {
  if (!module) {
    return null
  }

  const binding = findPresentationComponentBinding(module, {
    bindingKind: createPresentationEnumDiscriminator(
      presentationBindingKinds,
      'projectionRenderer',
      'ProjectionRenderer',
    ),
    componentSet,
    id: projection.Id,
    targetKind: createPresentationEnumDiscriminator(
      presentationTargetKinds,
      'react',
      'React',
    ),
  })

  if (!binding) {
    return null
  }

  return {
    componentKey: binding.ComponentKey ?? null,
    componentRole: binding.ComponentRole ?? null,
  }
}

/**
 * Shared tree-state hook for document projections. It uses workspace runtime
 * state when mounted under DocumentWorkspaceRuntime, and falls back to local
 * component state when a tree renderer is tested or rendered in isolation.
 */
export function useDocumentWorkspaceTreeState({
  createSemanticSelection,
  initialExpandedItemIds,
  projectionId,
  semanticReferenceKind,
}: {
  readonly createSemanticSelection?: (
    itemId: string,
  ) => DocumentWorkspaceSemanticSelection | null
  readonly initialExpandedItemIds: readonly string[]
  readonly projectionId?: string | null
  readonly semanticReferenceKind?: string | number | null
}) {
  const runtime = useContext(DocumentWorkspaceRuntimeContext)
  const runtimeState = runtime?.state
  const selectSemanticReference = runtime?.selectSemanticReference
  const setRuntimeTreeExpandedItemIds = runtime?.setTreeExpandedItemIds
  const setRuntimeTreeNavigationHistory = runtime?.setTreeNavigationHistory
  const setRuntimeTreeSearch = runtime?.setTreeSearch
  const initialExpandedItemKey = initialExpandedItemIds.join('\u0000')
  const [localExpandedItemState, setLocalExpandedItemState] = useState(() => ({
    expandedItemIds: initialExpandedItemIds,
    initialExpandedItemKey,
    isUserControlled: false,
  }))
  const [localNavigationHistory, setLocalNavigationHistory] =
    useState<DocumentWorkspaceNavigationHistory>(createInitialNavigationHistory)
  const [localSearch, setLocalSearch] =
    useState<DocumentWorkspaceTreeSearchState>(createInitialTreeSearchState)
  const persistedTreeState =
    runtimeState && projectionId ? runtimeState.treeStates[projectionId] : undefined
  const treeState =
    persistedTreeState ??
    (runtimeState && projectionId ? createInitialTreeState() : null)
  const localExpandedItemIds =
    localExpandedItemState.isUserControlled ||
      localExpandedItemState.initialExpandedItemKey === initialExpandedItemKey
      ? localExpandedItemState.expandedItemIds
      : initialExpandedItemIds
  const expandedItemIds =
    persistedTreeState?.expandedItemIds ??
    (runtimeState && projectionId
      ? initialExpandedItemIds
      : localExpandedItemIds)
  const navigationHistory = treeState?.navigationHistory ?? localNavigationHistory
  const search = treeState?.search ?? localSearch

  const setExpandedItemIds = useCallback(
    (value: SetStateAction<readonly string[]>) => {
      if (setRuntimeTreeExpandedItemIds && projectionId) {
        setRuntimeTreeExpandedItemIds(projectionId, value)
      } else {
        setLocalExpandedItemState((current) => ({
          expandedItemIds: applySetStateAction(
            value,
            current.isUserControlled ||
              current.initialExpandedItemKey === initialExpandedItemKey
              ? current.expandedItemIds
              : initialExpandedItemIds,
          ),
          initialExpandedItemKey,
          isUserControlled: true,
        }))
      }
    },
    [
      initialExpandedItemIds,
      initialExpandedItemKey,
      projectionId,
      setRuntimeTreeExpandedItemIds,
    ],
  )
  const setNavigationHistory = useCallback(
    (value: SetStateAction<DocumentWorkspaceNavigationHistory>) => {
      if (setRuntimeTreeNavigationHistory && projectionId) {
        setRuntimeTreeNavigationHistory(projectionId, value)
      } else {
        setLocalNavigationHistory(value)
      }
    },
    [projectionId, setRuntimeTreeNavigationHistory],
  )
  const setSearch = useCallback(
    (value: SetStateAction<DocumentWorkspaceTreeSearchState>) => {
      if (setRuntimeTreeSearch && projectionId) {
        setRuntimeTreeSearch(projectionId, value)
      } else {
        setLocalSearch(value)
      }
    },
    [projectionId, setRuntimeTreeSearch],
  )
  const publishSelectedItem = useCallback(
    (itemId: string | null) => {
      if (!selectSemanticReference || !projectionId || !itemId) {
        return
      }

      selectSemanticReference(
        normalizeTreeSemanticSelection(
          createSemanticSelection?.(itemId) ??
            createDefaultTreeSemanticSelection(itemId, projectionId, semanticReferenceKind),
          projectionId,
          semanticReferenceKind,
        ),
      )
    },
    [createSemanticSelection, projectionId, selectSemanticReference, semanticReferenceKind],
  )
  const semanticSelections = useMemo(
    () =>
      filterSemanticSelectionsForProjection({
        coordination: runtime?.coordination ?? [],
        projectionId,
        selections: runtimeState?.semanticSelections ?? [],
      }),
    [projectionId, runtime?.coordination, runtimeState?.semanticSelections],
  )

  return {
    expandedItemIds,
    navigationHistory,
    onSelectedItemIdChange: publishSelectedItem,
    search,
    semanticSelections,
    setExpandedItemIds,
    setNavigationHistory,
    setSearch,
  }
}

export function useDocumentWorkspaceSemanticSelections({
  projectionId,
}: {
  readonly projectionId?: string | null
}) {
  const runtime = useContext(DocumentWorkspaceRuntimeContext)
  const runtimeState = runtime?.state
  return useMemo(
    () =>
      filterSemanticSelectionsForProjection({
        coordination: runtime?.coordination ?? [],
        projectionId,
        selections: runtimeState?.semanticSelections ?? [],
      }),
    [projectionId, runtime?.coordination, runtimeState?.semanticSelections],
  )
}

export function useDocumentWorkspaceSemanticSelectionPublisher({
  projectionId,
  semanticReferenceKind,
}: {
  readonly projectionId?: string | null
  readonly semanticReferenceKind?: string | number | null
}) {
  const runtime = useContext(DocumentWorkspaceRuntimeContext)
  const selectSemanticReference = runtime?.selectSemanticReference
  return useCallback(
    (selection: DocumentWorkspaceSemanticSelection) => {
      if (!projectionId || !selectSemanticReference) {
        return
      }

      selectSemanticReference(
        normalizeTreeSemanticSelection(
          selection,
          projectionId,
          semanticReferenceKind,
        ),
      )
    },
    [projectionId, selectSemanticReference, semanticReferenceKind],
  )
}

export function createDocumentWorkspaceSemanticPath(
  segments: readonly DocumentWorkspaceSemanticPathSegment[],
): DocumentWorkspaceSemanticPath {
  return { Segments: segments }
}

export function createDocumentWorkspaceSemanticReference({
  id,
  kind,
  label,
  path,
}: {
  readonly id: string
  readonly kind?: string | number | null
  readonly label?: string | null
  readonly path?: DocumentWorkspaceSemanticPath | null
}): DocumentWorkspaceSemanticReference {
  return {
    Id: id,
    Kind: kind,
    Path:
      path ??
      createDocumentWorkspaceSemanticPath([
        {
          Id: id,
          Kind: kind,
          Label: label ?? id,
        },
      ]),
  }
}

export function documentWorkspaceSemanticReferenceMatchesSelection(
  reference: DocumentWorkspaceSemanticReference,
  selection: DocumentWorkspaceSemanticSelection,
) {
  return (
    semanticReferencesMatch(reference, selection.Target) ||
    semanticPathMatches(reference.Path ?? null, selection.SemanticPath ?? null) ||
    semanticPathMatches(reference.Path ?? null, selection.Target.Path ?? null) ||
    semanticPathContainsReference(selection.SemanticPath ?? null, reference) ||
    semanticPathContainsReference(selection.Target.Path ?? null, reference)
  )
}

export function findDocumentWorkspaceSemanticTreeItemId(
  selection: DocumentWorkspaceSemanticSelection,
  nodes: Iterable<DocumentWorkspaceSemanticTreeNode>,
) {
  for (const node of nodes) {
    const references = [
      node.semanticReference,
      ...(node.relatedSemanticReferences ?? []),
    ]
    if (
      references.some((reference) =>
        documentWorkspaceSemanticReferenceMatchesSelection(reference, selection),
      )
    ) {
      return node.itemId
    }
  }

  return null
}

function resolveDocumentWorkspace(
  module: PresentationModuleDefinition | null,
  pageViewId: string,
) {
  // A route addresses a page view. The workspace may be the page itself or a
  // hosted child surface within one of the page regions.
  const pageView = findPresentationView(module, pageViewId)
  if (!pageView) {
    return null
  }

  const workspaceView = findWorkspaceHostView(module, pageView)
  const workspaceRef = workspaceView?.Workspace ?? null
  if (!workspaceView || !workspaceRef) {
    return null
  }

  const workspace = findPresentationWorkspace(module, workspaceRef.WorkspaceId)
  if (!workspace) {
    return null
  }

  const documentProfileId =
    workspaceRef.DocumentProfileId ??
    workspace.DefaultDocumentProfileId ??
    workspace.DocumentProfiles?.[0]?.Id ??
    null
  const documentProfile = documentProfileId
    ? findPresentationDocumentProfile(workspace, documentProfileId)
    : null
  if (!documentProfile) {
    return null
  }

  return { documentProfile, pageView, workspace, workspaceRef, workspaceView }
}

function findWorkspaceHostView(
  module: PresentationModuleDefinition | null,
  pageView: ViewDefinition | null,
) {
  if (pageView?.Workspace) {
    return pageView
  }

  for (const region of pageView?.Regions ?? []) {
    for (const viewId of region.ViewIds ?? []) {
      const view = findPresentationView(module, viewId)
      if (view?.Workspace) {
        return view
      }
    }
  }

  return null
}

function resolveInitialProjectionIds(
  workspaceRef: WorkspaceRefDefinition | null,
  projections: readonly ProjectionDefinition[],
) {
  // Workspace refs can narrow the active profile to a preferred projection set.
  // If that declaration is absent or stale, expose every profile projection.
  const projectionIds = new Set(projections.map((projection) => projection.Id))
  const initialProjectionIds = (workspaceRef?.InitialProjectionIds ?? []).filter(
    (projectionId) => projectionIds.has(projectionId),
  )

  return initialProjectionIds.length > 0
    ? initialProjectionIds
    : projections.map((projection) => projection.Id)
}

function resolveDefaultLayout(
  layout: { readonly DefaultModeId?: string | null } | null,
): DocumentWorkspaceLayout {
  return layout?.DefaultModeId === 'split' ? 'split' : 'single'
}

function resolveActiveLayoutModeId(
  layout: DocumentWorkspaceLayout,
  layoutDefinition: {
    readonly DefaultModeId?: string | null
    readonly Modes?: readonly { readonly Id: string }[]
  } | null,
) {
  if (layout === 'split') {
    return layoutDefinition?.Modes?.find(
      (mode) => mode.Id.toLocaleLowerCase() === 'split',
    )?.Id ?? 'split'
  }

  return layoutDefinition?.DefaultModeId ?? layoutDefinition?.Modes?.[0]?.Id ?? 'single'
}

function createInitialRuntimeState(): DocumentWorkspaceRuntimeState {
  return {
    semanticSelections: [],
    treeStates: {},
  }
}

function createInitialTreeState(): DocumentWorkspaceTreeStateSnapshot {
  return {
    expandedItemIds: [],
    navigationHistory: createInitialNavigationHistory(),
    search: createInitialTreeSearchState(),
  }
}

function createInitialNavigationHistory(): DocumentWorkspaceNavigationHistory {
  return { entries: [], index: -1 }
}

function createInitialTreeSearchState(): DocumentWorkspaceTreeSearchState {
  return { activeMatchIndex: 0, isOpen: false, query: '' }
}

function createDefaultTreeSemanticSelection(
  itemId: string,
  projectionId: string,
  semanticReferenceKind?: string | number | null,
): DocumentWorkspaceSemanticSelection {
  const path = createDocumentWorkspaceSemanticPath([
    { Id: projectionId, Kind: semanticReferenceKind, Label: projectionId },
    { Id: itemId, Kind: semanticReferenceKind, Label: itemId },
  ])
  return {
    SourceProjectionId: projectionId,
    SemanticPath: path,
    Target: {
      Id: itemId,
      Kind: semanticReferenceKind,
      Path: path,
    },
  }
}

function normalizeTreeSemanticSelection(
  selection: DocumentWorkspaceSemanticSelection,
  projectionId: string,
  semanticReferenceKind?: string | number | null,
): DocumentWorkspaceSemanticSelection {
  const targetPath =
    selection.Target.Path ??
    selection.SemanticPath ??
    createDocumentWorkspaceSemanticPath([
      {
        Id: selection.Target.Id,
        Kind: selection.Target.Kind ?? semanticReferenceKind,
        Label: selection.Target.Id,
      },
    ])
  return {
    SourceProjectionId: selection.SourceProjectionId ?? projectionId,
    SemanticPath: selection.SemanticPath ?? targetPath,
    Target: {
      ...selection.Target,
      Kind: selection.Target.Kind ?? semanticReferenceKind,
      Path: targetPath,
    },
  }
}

function createCoordinatedSemanticSelections(
  selection: DocumentWorkspaceSemanticSelection,
  coordination: readonly CoordinationDefinition[],
): readonly DocumentWorkspaceSemanticSelection[] {
  if (!hasSemanticSelectionCoordination(coordination, selection.SourceProjectionId ?? null)) {
    return [selection]
  }

  return [selection]
}

function filterSemanticSelectionsForProjection({
  coordination,
  projectionId,
  selections,
}: {
  readonly coordination: readonly CoordinationDefinition[]
  readonly projectionId?: string | null
  readonly selections: readonly DocumentWorkspaceSemanticSelection[]
}): readonly DocumentWorkspaceSemanticSelection[] {
  if (!projectionId) {
    return selections
  }

  return selections.filter((selection) =>
    canRevealSemanticSelectionForProjection(coordination, projectionId, selection),
  )
}

function hasSemanticSelectionCoordination(
  coordination: readonly CoordinationDefinition[],
  sourceProjectionId: string | null,
) {
  return coordination.some(
    (definition) =>
      matchesCoordinationTrigger(definition, sourceProjectionId) &&
      matchesPresentationEnum(definition.Action.Kind, setSemanticSelectionActionKind),
  )
}

function canRevealSemanticSelectionForProjection(
  coordination: readonly CoordinationDefinition[],
  projectionId: string,
  selection: DocumentWorkspaceSemanticSelection,
) {
  const revealDefinitions = coordination.filter(
    (definition) =>
      matchesCoordinationTrigger(definition, selection.SourceProjectionId ?? null) &&
      matchesPresentationEnum(definition.Action.Kind, revealSemanticSelectionActionKind),
  )
  if (revealDefinitions.length === 0) {
    return true
  }

  return revealDefinitions.some((definition) =>
    definition.Action.TargetProjectionIds.includes(projectionId),
  )
}

function matchesCoordinationTrigger(
  definition: CoordinationDefinition,
  sourceProjectionId: string | null,
) {
  return (
    matchesPresentationEnum(definition.Trigger.Kind, selectionChangedTriggerKind) &&
    (!definition.Trigger.SourceProjectionId ||
      definition.Trigger.SourceProjectionId === sourceProjectionId)
  )
}

function semanticReferencesMatch(
  left: DocumentWorkspaceSemanticReference,
  right: DocumentWorkspaceSemanticReference,
) {
  return (
    left.Id === right.Id &&
    semanticReferenceKindsMatch(left.Kind ?? null, right.Kind ?? null)
  )
}

function semanticPathMatches(
  left: DocumentWorkspaceSemanticPath | null,
  right: DocumentWorkspaceSemanticPath | null,
) {
  if (!left || !right || left.Segments.length !== right.Segments.length) {
    return false
  }

  return left.Segments.every((leftSegment, index) => {
    const rightSegment = right.Segments[index]
    return (
      leftSegment.Id === rightSegment.Id &&
      semanticReferenceKindsMatch(leftSegment.Kind ?? null, rightSegment.Kind ?? null)
    )
  })
}

function semanticPathContainsReference(
  path: DocumentWorkspaceSemanticPath | null,
  reference: DocumentWorkspaceSemanticReference,
) {
  return (
    path?.Segments.some(
      (segment) =>
        segment.Id === reference.Id &&
        semanticReferenceKindsMatch(segment.Kind ?? null, reference.Kind ?? null),
    ) ?? false
  )
}

function semanticReferenceKindsMatch(
  left: string | number | null,
  right: string | number | null,
) {
  if (left === null || right === null) {
    return true
  }

  const leftKeys = createDiscriminatorKeys(left, semanticReferenceKindLabels)
  const rightKeys = createDiscriminatorKeys(right, semanticReferenceKindLabels)
  for (const key of leftKeys) {
    if (rightKeys.has(key)) {
      return true
    }
  }

  return false
}

function createDocumentProjectionRendererResolution<
  TExtra,
  TComponentSystem = unknown,
  TDesignSystem = unknown,
>(
  projection: ProjectionDefinition | null,
): DocumentWorkspaceProjectionRendererResolution<TExtra, TComponentSystem, TDesignSystem> {
  return {
    componentKey: null,
    componentRole: null,
    projection,
    renderer: null,
    rendererKey: projection?.RendererKey ?? null,
    resolutionSource: null,
  }
}

const selectionChangedTriggerKind = createPresentationEnumDiscriminator(
  coordinationTriggerKinds,
  'selectionChanged',
  'SelectionChanged',
)
const setSemanticSelectionActionKind = createPresentationEnumDiscriminator(
  coordinationActionKinds,
  'setSemanticSelection',
  'SetSemanticSelection',
)
const revealSemanticSelectionActionKind = createPresentationEnumDiscriminator(
  coordinationActionKinds,
  'revealSemanticSelection',
  'RevealSemanticSelection',
)

function findDiscriminatorRenderer<TValue>(
  renderers: Readonly<Record<string, TValue>> | undefined,
  value: string | number | null | undefined,
  labels: Readonly<Record<string | number, string>>,
) {
  if (!renderers || value === null || value === undefined) {
    return undefined
  }

  for (const key of createDiscriminatorKeys(value, labels)) {
    const renderer = renderers[key]
    if (renderer) {
      return renderer
    }
  }

  return undefined
}

function createDiscriminatorKeys(
  value: string | number,
  labels: Readonly<Record<string | number, string>>,
) {
  const keys = new Set<string>([String(value)])
  const directLabel = labels[value]
  if (directLabel) {
    keys.add(directLabel)
  }

  if (typeof value === 'string') {
    const normalizedValue = value.toLocaleLowerCase()
    for (const [enumValue, label] of Object.entries(labels)) {
      if (label.toLocaleLowerCase() === normalizedValue) {
        keys.add(enumValue)
        keys.add(label)
      }
    }
  }

  return keys
}

function updateRuntimeTreeState(
  current: {
    readonly instanceKey: string
    readonly value: DocumentWorkspaceRuntimeState
  },
  instanceKey: string,
  projectionId: string,
  update: (
    state: DocumentWorkspaceTreeStateSnapshot,
  ) => DocumentWorkspaceTreeStateSnapshot,
) {
  // Treat an instance-key change as a new workspace session. This prevents tree
  // state from one document/profile from being reused by another.
  const base =
    current.instanceKey === instanceKey ? current.value : createInitialRuntimeState()
  const currentTreeState = base.treeStates[projectionId] ?? createInitialTreeState()

  return {
    instanceKey,
    value: {
      ...base,
      treeStates: {
        ...base.treeStates,
        [projectionId]: update(currentTreeState),
      },
    },
  }
}

function applySetStateAction<T>(action: SetStateAction<T>, current: T) {
  return typeof action === 'function'
    ? (action as (current: T) => T)(current)
    : action
}
