import type {
  ProjectedNavigationShellLayout as CoreProjectedNavigationShellLayout,
} from '@cohesive/presentation-core'
import {
  projectNavigationShellLayoutDiagnostics as projectCoreNavigationShellLayoutDiagnostics,
  projectPresentationDesignIntentDiagnostics,
  resolveProjectedNavigationShellLayout as resolveCoreProjectedNavigationShellLayout,
  type PresentationProjectionDiagnostic,
} from '@cohesive/presentation-core'
import type { PresentationDesignSystem } from './presentation-design-system'
import type {
  NavigationDefinition,
  NavigationShellSlotDefinition,
} from '@cohesive/presentation-contracts'

export type ProjectedNavigationShellLayout =
  CoreProjectedNavigationShellLayout<
    ProjectedNavigationShellFrameLayout,
    ProjectedNavigationShellSlotLayout
  >

export interface ProjectedNavigationShellFrameLayout {
  readonly headerClassName: string
  readonly headerContentClassName: string
  readonly headerNavigationClassName: string
  readonly rootClassName: string
}

export interface ProjectedNavigationShellSlotLayout {
  readonly brandBadgeClassName: string
  readonly brandSubtitleClassName: string
  readonly brandTitleClassName: string
  readonly navigationItemClassName: string
  readonly rootClassName: string
}

export interface NavigationShellLayoutDiagnosticsOptions {
  readonly designSystem: PresentationDesignSystem
  readonly navigation: NavigationDefinition
  readonly shellFrameRendererKeys?: readonly string[]
  readonly shellSlots: readonly NavigationShellSlotDefinition[]
}

export function resolveProjectedNavigationShellLayout(
  navigation: NavigationDefinition,
  shellSlots: readonly NavigationShellSlotDefinition[],
  designSystem: PresentationDesignSystem,
): ProjectedNavigationShellLayout {
  return resolveCoreProjectedNavigationShellLayout({
    navigation,
    resolveFrameLayout: () => resolveProjectedNavigationShellFrameLayout(navigation, designSystem),
    resolveSlotLayout: (slot) => resolveProjectedNavigationShellSlotLayout(slot, designSystem),
    shellSlots,
  })
}

export function resolveProjectedNavigationShellFrameLayout(
  navigation: NavigationDefinition,
  designSystem: PresentationDesignSystem,
): ProjectedNavigationShellFrameLayout {
  const design = navigation.Shell.Design
  return {
    headerClassName: designSystem.classNames.navigationShell.header({ design }),
    headerContentClassName: designSystem.classNames.navigationShell.headerContent({ design }),
    headerNavigationClassName: designSystem.classNames.navigationShell.headerNavigation({ design }),
    rootClassName: designSystem.classNames.navigationShell.root({ design }),
  }
}

export function resolveProjectedNavigationShellSlotLayout(
  slot: NavigationShellSlotDefinition,
  designSystem: PresentationDesignSystem,
): ProjectedNavigationShellSlotLayout {
  const design = slot.Design
  return {
    brandBadgeClassName: designSystem.classNames.navigationShell.brandBadge({ design }),
    brandSubtitleClassName: designSystem.classNames.navigationShell.brandSubtitle({ design }),
    brandTitleClassName: designSystem.classNames.navigationShell.brandTitle({ design }),
    navigationItemClassName: designSystem.classNames.navigationShell.navigationItem({ design }),
    rootClassName: designSystem.classNames.navigationShell.slotRoot({ slot }),
  }
}

export function projectNavigationShellLayoutDiagnostics({
  designSystem,
  navigation,
  shellFrameRendererKeys,
  shellSlots,
}: NavigationShellLayoutDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  const diagnostics: PresentationProjectionDiagnostic[] = [
    ...projectPresentationDesignIntentDiagnostics({
      design: navigation.Shell.Design,
      ignoredFields: designSystem.diagnostics.navigationShellFrame.ignoredFields,
      interpretedFields: designSystem.diagnostics.navigationShellFrame.interpretedFields,
      message:
        `Navigation shell '${navigation.Shell.Id}' has design intent fields ` +
        'that the standard shell frame layout interpreter does not yet project.',
      source: `navigation-shell:${navigation.Shell.Id}`,
      subject: {
        id: navigation.Shell.Id,
        kind: 'NavigationShellDefinition',
      },
      suggestedNextStep:
        'Extend the shell frame layout interpreter for these design fields or remove the unused design intent.',
      target: 'react-navigation-shell-frame-layout',
    }),
    ...shellSlots.flatMap((slot) =>
      projectPresentationDesignIntentDiagnostics({
        design: slot.Design,
        ignoredFields: designSystem.diagnostics.navigationShellSlot.ignoredFields,
        interpretedFields: designSystem.diagnostics.navigationShellSlot.interpretedFields,
        message:
          `Navigation shell slot '${slot.Id}' has design intent fields ` +
          'that the standard shell slot layout interpreter does not yet project.',
        semanticInputs: [
          'Kind',
          'Placement',
        ],
        source: `navigation-shell:${navigation.Shell.Id}`,
        subject: {
          id: slot.Id,
          kind: 'NavigationShellSlotDefinition',
        },
        suggestedNextStep:
          'Extend the shell slot layout interpreter for these design fields or remove the unused design intent.',
        target: 'react-navigation-shell-slot-layout',
      })),
    ...projectCoreNavigationShellLayoutDiagnostics({
      navigation,
      shellFrameRendererKeys,
    }),
  ]

  return diagnostics
}
