import {
  createPresentationEnumDiscriminator,
  findPresentationView,
  matchesPresentationEnum,
  presentationDataSourceBindings,
  resolvePresentationViewDataSourceIds,
  type PresentationDataSourceAuthorizationRequirement,
  type PresentationDataSourceBinding,
  type PresentationModuleDefinition,
  type ViewDefinition,
} from '@cohesive/presentation-core'
import type { PresentationFlowRuntimeEntry } from './presentation-flow-runtime'
import { viewSubjectKinds } from '@cohesive/presentation-contracts'

export interface LocalFlowStateDataSourceState {
  readonly data?: unknown
  readonly error?: unknown
  readonly isFetching?: boolean
  readonly isPending?: boolean
}

export function createLocalFlowStateDataSourceBindings({
  activeEntries,
  authorization,
  module,
  state,
  statesByFlowId,
}: {
  readonly activeEntries: readonly PresentationFlowRuntimeEntry[]
  readonly authorization: PresentationDataSourceAuthorizationRequirement
  readonly module: PresentationModuleDefinition | null
  readonly state?: LocalFlowStateDataSourceState | null
  readonly statesByFlowId?: Readonly<Record<string, LocalFlowStateDataSourceState | null | undefined>>
}): readonly PresentationDataSourceBinding[] {
  return activeEntries.flatMap((entry) => {
    const flowState = statesByFlowId?.[entry.flow.Id] ?? state
    const dataSourceIds = resolveLocalFlowStateDataSourceIds({
      flowId: entry.flow.Id,
      module,
      view: entry.view,
    })

    return dataSourceIds.map((dataSourceId) =>
      presentationDataSourceBindings.localValue({
        authorization,
        data: flowState?.data ?? null,
        dataSourceId,
        error: flowState?.error,
        isFetching: flowState?.isFetching,
        isPending: flowState?.isPending,
      }),
    )
  })
}

export function resolveLocalFlowStateDataSourceIds({
  flowId,
  module,
  view,
}: {
  readonly flowId: string
  readonly module: PresentationModuleDefinition | null
  readonly view: ViewDefinition | null
}) {
  if (!module || !view) {
    return []
  }

  const dataSourceIds = new Set<string>()
  const visitedViewIds = new Set<string>()
  collectLocalFlowStateDataSourceIds({
    dataSourceIds,
    flowId,
    module,
    view,
    visitedViewIds,
  })
  return Array.from(dataSourceIds)
}

function collectLocalFlowStateDataSourceIds({
  dataSourceIds,
  flowId,
  module,
  view,
  visitedViewIds,
}: {
  readonly dataSourceIds: Set<string>
  readonly flowId: string
  readonly module: PresentationModuleDefinition
  readonly view: ViewDefinition
  readonly visitedViewIds: Set<string>
}) {
  if (visitedViewIds.has(view.Id)) {
    return
  }

  visitedViewIds.add(view.Id)
  if (isLocalFlowStateView(view, flowId)) {
    for (const dataSourceId of resolvePresentationViewDataSourceIds(view)) {
      dataSourceIds.add(dataSourceId)
    }
  }

  for (const region of view.Regions) {
    for (const viewId of region.ViewIds) {
      const childView = findPresentationView<ViewDefinition>(module, viewId)
      if (childView) {
        collectLocalFlowStateDataSourceIds({
          dataSourceIds,
          flowId,
          module,
          view: childView,
          visitedViewIds,
        })
      }
    }
  }
}

function isLocalFlowStateView(view: ViewDefinition, flowId: string) {
  return (
    view.Subject.FlowId === flowId &&
    matchesPresentationEnum(
      view.Subject.Kind,
      createPresentationEnumDiscriminator(
        viewSubjectKinds,
        'localFlowState',
        'LocalFlowState',
      ),
    )
  )
}
