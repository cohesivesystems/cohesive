import {
  projectPresentationActionIconDiagnostics as projectCorePresentationActionIconDiagnostics,
  projectPresentationIconDiagnostics as projectCorePresentationIconDiagnostics,
  type PresentationActionIconPlacement,
  type PresentationIconDiagnosticSubject,
  type ProjectPresentationActionIconDiagnosticsOptions as CoreProjectPresentationActionIconDiagnosticsOptions,
  type ProjectPresentationIconDiagnosticsOptions as CoreProjectPresentationIconDiagnosticsOptions,
} from '@cohesivesystems/presentation-core'

import {
  standardLucidePresentationIconRegistry,
  type PresentationIconRegistry,
} from './presentation-icon-registry'

export type {
  PresentationActionIconPlacement,
  PresentationIconDiagnosticSubject,
  PresentationIconModuleProjection,
} from '@cohesivesystems/presentation-core'

export interface ProjectPresentationIconDiagnosticsOptions<
  TSubject extends PresentationIconDiagnosticSubject,
> extends Omit<CoreProjectPresentationIconDiagnosticsOptions<TSubject>, 'registry'> {
  readonly registry?: PresentationIconRegistry<TSubject> | null
}

export interface ProjectPresentationActionIconDiagnosticsOptions<
  TPlacement extends PresentationActionIconPlacement,
> extends Omit<CoreProjectPresentationActionIconDiagnosticsOptions<TPlacement>, 'registry'> {
  readonly registry?: PresentationIconRegistry<TPlacement> | null
}

export function projectPresentationIconDiagnostics<
  TSubject extends PresentationIconDiagnosticSubject,
>(options: ProjectPresentationIconDiagnosticsOptions<TSubject>) {
  return projectCorePresentationIconDiagnostics({
    ...options,
    registry:
      options.registry ??
      (standardLucidePresentationIconRegistry as PresentationIconRegistry<TSubject>),
  })
}

export function projectPresentationActionIconDiagnostics<
  TPlacement extends PresentationActionIconPlacement,
>(options: ProjectPresentationActionIconDiagnosticsOptions<TPlacement>) {
  return projectCorePresentationActionIconDiagnostics({
    ...options,
    registry:
      options.registry ??
      (standardLucidePresentationIconRegistry as PresentationIconRegistry<TPlacement>),
  })
}
