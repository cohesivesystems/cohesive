import {
  navigationShellKindLabels,
  navigationShellSlotKinds,
  type NavigationDefinition,
  type NavigationShellSlotDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  hasNavigationShellFrameRendererBinding,
} from './navigation-shell-frame-renderer-registry'
import {
  findNavigationShellSlot,
  isNavigationShellSlotKind,
} from './navigation-shell-runtime'
import {
  createPresentationProjectionDiagnostic,
  type PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'

export interface ProjectedNavigationShellLayout<
  TFrameLayout = unknown,
  TSlotLayout = unknown,
> {
  readonly brandSlot: NavigationShellSlotDefinition | null
  readonly frame: TFrameLayout
  readonly primaryNavigationSlot: NavigationShellSlotDefinition | null
  readonly routedContentSlot: NavigationShellSlotDefinition | null
  readonly slotLayouts: Readonly<Record<string, TSlotLayout>>
  readonly shellSlots: readonly NavigationShellSlotDefinition[]
  readonly systemNoticeSlots: readonly NavigationShellSlotDefinition[]
  readonly utilityActionSlots: readonly NavigationShellSlotDefinition[]
}

export interface ResolveProjectedNavigationShellLayoutOptions<
  TFrameLayout,
  TSlotLayout,
> {
  readonly navigation: NavigationDefinition
  readonly resolveFrameLayout: (navigation: NavigationDefinition) => TFrameLayout
  readonly resolveSlotLayout: (slot: NavigationShellSlotDefinition) => TSlotLayout
  readonly shellSlots: readonly NavigationShellSlotDefinition[]
}

export interface NavigationShellLayoutDiagnosticsOptions {
  readonly navigation: NavigationDefinition
  readonly shellFrameRendererKeys?: readonly string[]
}

/**
 * Groups semantic shell slots and lets adapters attach their own layout
 * interpretation without making core depend on CSS, React, or Tailwind.
 */
export function resolveProjectedNavigationShellLayout<
  TFrameLayout,
  TSlotLayout,
>({
  navigation,
  resolveFrameLayout,
  resolveSlotLayout,
  shellSlots,
}: ResolveProjectedNavigationShellLayoutOptions<
  TFrameLayout,
  TSlotLayout
>): ProjectedNavigationShellLayout<TFrameLayout, TSlotLayout> {
  return {
    brandSlot: findNavigationShellSlot(shellSlots, navigationShellSlotKinds.brand, 'Brand'),
    frame: resolveFrameLayout(navigation),
    primaryNavigationSlot: findNavigationShellSlot(
      shellSlots,
      navigationShellSlotKinds.primaryNavigation,
      'PrimaryNavigation',
    ),
    routedContentSlot: findNavigationShellSlot(
      shellSlots,
      navigationShellSlotKinds.routedContent,
      'RoutedContent',
    ),
    slotLayouts: Object.fromEntries(
      shellSlots.map((slot) => [slot.Id, resolveSlotLayout(slot)]),
    ),
    shellSlots,
    systemNoticeSlots: shellSlots.filter((slot) =>
      isNavigationShellSlotKind(slot, navigationShellSlotKinds.systemNotices, 'SystemNotices')),
    utilityActionSlots: shellSlots.filter((slot) =>
      isNavigationShellSlotKind(slot, navigationShellSlotKinds.utilityActions, 'UtilityActions')),
  }
}

export function projectNavigationShellLayoutDiagnostics({
  navigation,
  shellFrameRendererKeys,
}: NavigationShellLayoutDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  if (
    !shellFrameRendererKeys ||
    hasNavigationShellFrameRendererBinding(shellFrameRendererKeys, navigation.Shell.Kind)
  ) {
    return []
  }

  const kindLabel =
    navigationShellKindLabels[navigation.Shell.Kind] ?? String(navigation.Shell.Kind)

  return [
    createPresentationProjectionDiagnostic({
      category: 'missing-binding',
      details: {
        kind: navigation.Shell.Kind,
        kindLabel,
      },
      id: `navigation-shell.${navigation.Shell.Id}.frame.missing-renderer`,
      interpretation: {
        status: 'unbound',
        target: 'navigation-shell',
      },
      message: `Navigation shell '${navigation.Shell.Id}' has no frontend frame renderer for kind '${kindLabel}'.`,
      severity: 'warning',
      source: `navigation-shell:${navigation.Shell.Id}`,
      subject: {
        id: navigation.Shell.Id,
        kind: 'NavigationShellDefinition',
      },
      suggestedNextStep: 'Register a shell frame renderer for this shell kind.',
    }),
  ]
}
