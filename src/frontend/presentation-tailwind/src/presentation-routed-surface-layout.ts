import type {
  DesignIntent,
} from '@cohesivesystems/presentation-contracts'
import type { PresentationDesignSystem } from './presentation-design-system'
import {
  projectPresentationDesignIntentDiagnostics,
  type PresentationProjectionDiagnostic,
  type PresentationSurface,
} from '@cohesivesystems/presentation-core'

export interface PresentationRoutedSurfaceLayoutContext {
  readonly designSystem: PresentationDesignSystem
  readonly surface: PresentationSurface | null
}

export interface PresentationRoutedSurfaceLayout {
  readonly className: string
  readonly contentClassName: string
}

export interface PresentationRoutedSurfaceLayoutDiagnosticsOptions {
  readonly designSystem: PresentationDesignSystem
  readonly sourceId: string
  readonly surface: PresentationSurface | null
}

/**
 * Resolves standard route-level layout chrome from presentation design intent.
 */
export function resolvePresentationRoutedSurfaceLayout({
  designSystem,
  surface,
}: PresentationRoutedSurfaceLayoutContext): PresentationRoutedSurfaceLayout {
  return {
    className: resolvePresentationRoutedSurfaceClassName(
      surface?.rootView?.Design,
      designSystem,
    ),
    contentClassName: resolvePresentationRoutedSurfaceContentClassName(
      surface?.rootView?.Design,
      designSystem,
    ),
  }
}

export function resolvePresentationRoutedSurfaceClassName(
  design: DesignIntent | null | undefined,
  designSystem: PresentationDesignSystem,
): string {
  return designSystem.classNames.routedSurface.root({ design })
}

export function resolvePresentationRoutedSurfaceContentClassName(
  design: DesignIntent | null | undefined,
  designSystem: PresentationDesignSystem,
): string {
  return designSystem.classNames.routedSurface.content({ design })
}

export function projectPresentationRoutedSurfaceLayoutDiagnostics({
  designSystem,
  sourceId,
  surface,
}: PresentationRoutedSurfaceLayoutDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  const rootView = surface?.rootView
  if (!rootView) {
    return []
  }

  return projectPresentationDesignIntentDiagnostics({
    design: rootView.Design,
    ignoredFields: designSystem.diagnostics.routedSurfaceLayout.ignoredFields,
    interpretedFields: designSystem.diagnostics.routedSurfaceLayout.interpretedFields,
    message:
      `Routed surface root view '${rootView.Name}' has design intent fields ` +
      'that the standard routed-surface layout interpreter does not yet project.',
    source: sourceId,
    subject: {
      id: rootView.Id,
      kind: 'ViewDefinition',
      name: rootView.Name,
    },
    suggestedNextStep:
      'Extend the routed-surface layout interpreter for these design fields or move the policy into the design system.',
    target: 'react-routed-surface-layout',
  })
}
