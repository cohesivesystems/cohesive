import type { ReactNode } from 'react'

import type {
  FlowDefinition,
  FlowStateDefinition,
  ViewDefinition,
} from '@cohesive/presentation-core'
import type {
  PresentationFlowInstance,
  PresentationFlowRuntimeSnapshot,
} from './presentation-flow-runtime'

export interface ProjectedPresentationFlowHostProps {
  readonly fallback?: ReactNode
  readonly renderView: (context: ProjectedPresentationFlowViewRenderContext) => ReactNode
  readonly runtime: PresentationFlowRuntimeSnapshot
}

export interface ProjectedPresentationFlowViewRenderContext {
  readonly flow: FlowDefinition
  readonly instance: PresentationFlowInstance
  readonly state: FlowStateDefinition
  readonly view: ViewDefinition
}

export function ProjectedPresentationFlowHost({
  fallback = null,
  renderView,
  runtime,
}: ProjectedPresentationFlowHostProps) {
  const { activeFlow, activeInstance, activeState, activeView } = runtime
  if (!activeFlow || !activeInstance || !activeState || !activeView) {
    return <>{fallback}</>
  }

  return (
    <>
      {renderView({
        flow: activeFlow,
        instance: activeInstance,
        state: activeState,
        view: activeView,
      })}
    </>
  )
}
