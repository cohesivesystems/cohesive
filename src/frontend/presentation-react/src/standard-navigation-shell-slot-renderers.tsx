import type { ReactNode } from 'react'

import {
  navigationShellSlotKinds,
  type NavigationDefinition,
  type NavigationNodeDefinition,
  type NavigationShellRegionDefinition,
  type NavigationShellSlotDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  createProjectedNavigationShellItems,
  isProjectedNavigationShellItemActive,
  resolveNavigationShellSlotRegions,
  type ProjectedNavigationShellItem,
} from '@cohesivesystems/presentation-core'
import {
  createNavigationShellSlotRendererRegistry,
  getNavigationShellSlotRendererRegistryKeys,
  resolveNavigationShellSlotRenderer,
  type ProjectedNavigationShellSlotRenderContext,
} from './navigation-shell-slot-renderer-registry'

export type StandardNavigationShellBadgeVariant =
  | 'default'
  | 'destructive'
  | 'ghost'
  | 'link'
  | 'outline'
  | 'secondary'

export interface StandardNavigationShellBadgeProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly variant?: StandardNavigationShellBadgeVariant
}

export interface StandardNavigationShellNavigationLinkProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly isActive?: boolean
  readonly to: string
}

export interface StandardNavigationShellComponentSystem {
  readonly badges: {
    readonly Badge: (props: StandardNavigationShellBadgeProps) => ReactNode
  }
  readonly navigation: {
    readonly NavigationLink: (
      props: StandardNavigationShellNavigationLinkProps
    ) => ReactNode
  }
}

export interface StandardNavigationShellSlotLayout {
  readonly brandBadgeClassName: string
  readonly brandSubtitleClassName: string
  readonly brandTitleClassName: string
  readonly navigationItemClassName: string
  readonly rootClassName: string
}

export type StandardNavigationShellSlotRenderContext<
  TComponentSystem extends StandardNavigationShellComponentSystem =
    StandardNavigationShellComponentSystem,
> = ProjectedNavigationShellSlotRenderContext<
  StandardNavigationShellSlotLayout,
  TComponentSystem
>

export const standardNavigationShellSlotRenderers =
  createNavigationShellSlotRendererRegistry<
    StandardNavigationShellSlotLayout,
    StandardNavigationShellComponentSystem
  >([
    {
      kind: navigationShellSlotKinds.brand,
      render: renderStandardNavigationShellBrand,
    },
    {
      kind: navigationShellSlotKinds.primaryNavigation,
      render: renderStandardNavigationShellPrimaryNavigation,
    },
    {
      kind: navigationShellSlotKinds.utilityActions,
      render: renderStandardNavigationShellRegionSlot,
    },
    {
      kind: navigationShellSlotKinds.systemNotices,
      render: renderStandardNavigationShellRegionSlot,
    },
    {
      kind: navigationShellSlotKinds.routedContent,
      render: renderStandardNavigationShellRoutedContent,
    },
  ])

export const standardNavigationShellSlotRendererKeys =
  getNavigationShellSlotRendererRegistryKeys(standardNavigationShellSlotRenderers)

export function renderStandardNavigationShellSlot(
  context: StandardNavigationShellSlotRenderContext,
) {
  const renderer = resolveNavigationShellSlotRenderer(
    standardNavigationShellSlotRenderers,
    context.slot,
  )

  return renderer?.(context) ?? null
}

export function StandardNavigationShellBrandSlot({
  brandLabel,
  componentSystem,
  renderShellIcon,
  shellIcon,
  slotLayout,
  subtitle,
  title,
}: {
  readonly brandLabel: string | null
  readonly componentSystem: StandardNavigationShellComponentSystem
  readonly renderShellIcon?: (icon: string) => ReactNode
  readonly shellIcon: string | null
  readonly slotLayout: StandardNavigationShellSlotLayout
  readonly subtitle: string | null
  readonly title: string | null
}) {
  const Badge = componentSystem.badges.Badge

  return (
    <div className={slotLayout.rootClassName}>
      {shellIcon && renderShellIcon ? renderShellIcon(shellIcon) : null}
      {brandLabel ? (
        <Badge className={slotLayout.brandBadgeClassName} variant="outline">
          {brandLabel}
        </Badge>
      ) : null}
      {title ? (
        <span className={slotLayout.brandTitleClassName}>{title}</span>
      ) : null}
      {subtitle ? (
        <span className={slotLayout.brandSubtitleClassName}>{subtitle}</span>
      ) : null}
    </div>
  )
}

export function StandardNavigationShellPrimaryNavigationSlot({
  activePath,
  componentSystem,
  items,
  navigation,
  renderNodeIcon,
  slotLayout,
}: {
  readonly activePath: string
  readonly componentSystem: StandardNavigationShellComponentSystem
  readonly items: readonly ProjectedNavigationShellItem[]
  readonly navigation: NavigationDefinition
  readonly renderNodeIcon?: (node: NavigationNodeDefinition) => ReactNode
  readonly slotLayout: StandardNavigationShellSlotLayout
}) {
  if (items.length === 0) {
    return null
  }

  const NavigationLink = componentSystem.navigation.NavigationLink

  return (
    <nav className={slotLayout.rootClassName}>
      {items.map((item) => (
        <NavigationLink
          className={slotLayout.navigationItemClassName}
          isActive={isProjectedNavigationShellItemActive(
            activePath,
            item,
            navigation,
          )}
          key={item.node.Id}
          to={item.href}
        >
          {renderNodeIcon?.(item.node)}
          {item.label}
        </NavigationLink>
      ))}
    </nav>
  )
}

export function StandardNavigationShellRegionSlot({
  navigation,
  renderShellRegion,
  slot,
  slotLayout,
}: {
  readonly navigation: NavigationDefinition
  readonly renderShellRegion?: (region: NavigationShellRegionDefinition) => ReactNode
  readonly slot: NavigationShellSlotDefinition
  readonly slotLayout: StandardNavigationShellSlotLayout
}) {
  const regions = resolveNavigationShellSlotRegions(navigation, slot)

  return (
    <div className={slotLayout.rootClassName}>
      {regions.map((region) => (
        <ShellRegion key={region.Id} region={region} renderShellRegion={renderShellRegion} />
      ))}
    </div>
  )
}

function renderStandardNavigationShellBrand({
  componentSystem,
  navigation,
  renderShellIcon,
  slotLayout,
}: StandardNavigationShellSlotRenderContext) {
  const chrome = navigation.Shell.Chrome
  const brandLabel = chrome?.BrandLabel ?? null
  const title = chrome?.Title ?? navigation.Label
  const subtitle = chrome?.Subtitle ?? null
  const shellIcon = chrome?.Icon ?? null

  return (
    <StandardNavigationShellBrandSlot
      brandLabel={brandLabel}
      componentSystem={componentSystem}
      renderShellIcon={renderShellIcon}
      shellIcon={shellIcon}
      slotLayout={slotLayout}
      subtitle={subtitle}
      title={title}
    />
  )
}

function renderStandardNavigationShellPrimaryNavigation({
  activePath,
  componentSystem,
  navigation,
  renderNodeIcon,
  slot,
  slotLayout,
}: StandardNavigationShellSlotRenderContext) {
  return (
    <StandardNavigationShellPrimaryNavigationSlot
      activePath={activePath}
      componentSystem={componentSystem}
      items={createProjectedNavigationShellItems(navigation, slot)}
      navigation={navigation}
      renderNodeIcon={renderNodeIcon}
      slotLayout={slotLayout}
    />
  )
}

function renderStandardNavigationShellRegionSlot({
  navigation,
  renderShellRegion,
  slot,
  slotLayout,
}: StandardNavigationShellSlotRenderContext) {
  return (
    <StandardNavigationShellRegionSlot
      navigation={navigation}
      renderShellRegion={renderShellRegion}
      slot={slot}
      slotLayout={slotLayout}
    />
  )
}

function renderStandardNavigationShellRoutedContent({
  children,
  slotLayout,
}: StandardNavigationShellSlotRenderContext) {
  return <div className={slotLayout.rootClassName}>{children}</div>
}

function ShellRegion({
  region,
  renderShellRegion,
}: {
  readonly region: NavigationShellRegionDefinition
  readonly renderShellRegion?: (region: NavigationShellRegionDefinition) => ReactNode
}) {
  return renderShellRegion?.(region) ?? null
}
