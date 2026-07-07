import type {
  ActionDefinition,
  ActionPlacementDefinition,
  DesignIntent,
  InputFormGroupDefinition,
  NavigationShellSlotDefinition,
} from '@cohesive/presentation-contracts'
import {
  inputFormGroupKinds,
  navigationShellSlotKinds,
} from '@cohesive/presentation-contracts'
import type {
  PresentationDesignIntentFieldName,
} from '@cohesive/presentation-core'

export interface PresentationDesignSystemDesignIntentInterpretation {
  readonly ignoredFields: readonly PresentationDesignIntentFieldName[]
  readonly interpretedFields: readonly PresentationDesignIntentFieldName[]
}

export interface PresentationDesignSystemDiagnostics {
  readonly navigationShellFrame: PresentationDesignSystemDesignIntentInterpretation
  readonly navigationShellSlot: PresentationDesignSystemDesignIntentInterpretation
  readonly routedSurfaceLayout: PresentationDesignSystemDesignIntentInterpretation
}

export interface PresentationRoutedSurfaceClassNameContext {
  readonly design: DesignIntent | null | undefined
}

export interface PresentationToneClassNameContext {
  readonly tone: string | null | undefined
}

export interface PresentationDocumentWorkspaceNodeClassNameContext {
  readonly isActiveSearchMatch?: boolean
  readonly isSearchMatch?: boolean
}

export interface PresentationDocumentWorkspaceToneClassNameContext {
  readonly label?: string | null | undefined
  readonly tone: string | null | undefined
}

export interface PresentationSurfaceChromeClassNameContext {
  readonly design?: DesignIntent | null | undefined
}

export interface PresentationInputFormGroupClassNameContext {
  readonly group: InputFormGroupDefinition
}

export interface PresentationNavigationShellClassNameContext {
  readonly design: DesignIntent | null | undefined
}

export interface PresentationNavigationShellSlotClassNameContext {
  readonly slot: NavigationShellSlotDefinition
}

export interface PresentationActionButtonContext {
  readonly action: ActionDefinition | null
  readonly placement: ActionPlacementDefinition
}

export type PresentationActionButtonSize =
  'default' | 'icon' | 'icon-lg' | 'icon-sm' | 'icon-xs' | 'lg' | 'sm' | 'xs'

export type PresentationActionButtonVariant =
  'default' | 'destructive' | 'ghost' | 'link' | 'outline' | 'secondary'

export interface PresentationDesignSystemClassNames {
  readonly badge: {
    readonly tone: (context: PresentationToneClassNameContext) => string
  }
  readonly documentWorkspace: {
    readonly actionRow: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly actionIcon: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly codeCell: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly codeTable: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly constraintBlock: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly constraintList: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly contentPane: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly dataTableContainer: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly detailContent: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly detailEmpty: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly detailHeader: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly detailHeaderContent: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly detailPanel: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly detailSubtitle: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly detailTable: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly detailTableLabelCell: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly detailTableRow: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly detailTableValueCell: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly detailTableValueText: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly detailTitle: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly errorMessage: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly iconTone: (context: PresentationDocumentWorkspaceToneClassNameContext) => string
    readonly inlineMeta: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly loadingMessage: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly nodeBadge: (context: PresentationDocumentWorkspaceToneClassNameContext) => string
    readonly nodeLabel: (context: PresentationDocumentWorkspaceNodeClassNameContext) => string
    readonly nodeLabelText: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly root: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly section: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly sectionBody: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly sectionHeading: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly sectionHeadingRow: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly splitter: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly splitterHandle: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly status: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly tableCell: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly tableHeader: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly tableHeaderCell: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly treePane: (context: PresentationSurfaceChromeClassNameContext) => string
  }
  readonly formSurface: {
    readonly actionRow: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly content: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly field: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly group: (context: PresentationInputFormGroupClassNameContext) => string
    readonly groups: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly rangeGroup: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly root: (context: PresentationSurfaceChromeClassNameContext) => string
  }
  readonly metricDashboard: {
    readonly root: (context: PresentationSurfaceChromeClassNameContext) => string
  }
  readonly navigationShell: {
    readonly brandBadge: (context: PresentationNavigationShellClassNameContext) => string
    readonly brandSubtitle: (context: PresentationNavigationShellClassNameContext) => string
    readonly brandTitle: (context: PresentationNavigationShellClassNameContext) => string
    readonly header: (context: PresentationNavigationShellClassNameContext) => string
    readonly headerContent: (context: PresentationNavigationShellClassNameContext) => string
    readonly headerNavigation: (context: PresentationNavigationShellClassNameContext) => string
    readonly navigationItem: (context: PresentationNavigationShellClassNameContext) => string
    readonly root: (context: PresentationNavigationShellClassNameContext) => string
    readonly slotRoot: (context: PresentationNavigationShellSlotClassNameContext) => string
  }
  readonly routedSurface: {
    readonly content: (context: PresentationRoutedSurfaceClassNameContext) => string
    readonly root: (context: PresentationRoutedSurfaceClassNameContext) => string
  }
  readonly recordDetail: {
    readonly root: (context: PresentationSurfaceChromeClassNameContext) => string
  }
  readonly statusNotice: {
    readonly tone: (context: PresentationToneClassNameContext) => string
  }
  readonly viewSurface: {
    readonly content: (context: PresentationSurfaceChromeClassNameContext) => string
    readonly root: (context: PresentationSurfaceChromeClassNameContext) => string
  }
  readonly toggle: {
    readonly tone: (context: PresentationToneClassNameContext) => string
  }
}

export interface PresentationDesignSystemComponents {
  readonly actionButton: {
    readonly size: (context: PresentationActionButtonContext) => PresentationActionButtonSize
    readonly variant: (context: PresentationActionButtonContext) => PresentationActionButtonVariant
  }
}

export interface PresentationDesignSystem {
  readonly classNames: PresentationDesignSystemClassNames
  readonly components: PresentationDesignSystemComponents
  readonly diagnostics: PresentationDesignSystemDiagnostics
  readonly id: string
  readonly target: string
}

const workspaceRoleTokens = new Set([
  'detail',
  'details',
  'execution-details',
])

const fullWidthRoleTokens = new Set([
  'catalog',
  'collection',
  'document-workspace',
  'editor',
  'list',
  'table',
  'workspace',
])

const wideRoleTokens = new Set([
  'catalog',
  'collection',
  'list',
  'table',
])

const workspaceSizeHints = new Set([
  '2xl',
  'full',
  'max',
  'screen',
  'xl',
])

const wideSizeHints = new Set([
  'wide',
])

export const tailwindPresentationDesignSystem: PresentationDesignSystem = {
  classNames: {
    badge: {
      tone: ({ tone }) => badgeClassByTone[coercePresentationTone(tone)],
    },
    documentWorkspace: {
      actionRow: () => 'mt-3',
      actionIcon: () => 'size-3.5',
      codeCell: () => 'border-b border-slate-950/8 px-3 py-2 font-mono text-[0.72rem] font-semibold text-slate-900',
      codeTable: () => 'w-full border-separate border-spacing-0 text-left text-xs',
      constraintBlock: () => 'overflow-auto px-3 py-2 text-xs text-slate-700',
      constraintList: () => 'grid gap-2',
      contentPane: () => 'h-full overflow-auto px-2 py-2',
      dataTableContainer: () =>
        'mt-2 max-h-86 overflow-auto rounded-md border border-slate-950/8 bg-white/72',
      detailContent: () => 'min-h-0 flex-1 overflow-auto px-4 py-3',
      detailEmpty: () =>
        'flex h-full items-center justify-center px-4 py-3 text-sm text-slate-500',
      detailHeader: () => 'border-b border-slate-950/8 px-4 py-3',
      detailHeaderContent: () => 'flex min-w-0 items-center gap-2',
      detailPanel: () => 'flex h-full min-w-0 flex-col overflow-hidden bg-slate-50/55',
      detailSubtitle: () =>
        'text-[0.68rem] font-semibold uppercase tracking-[0.18em] text-slate-500',
      detailTable: () => 'w-full border-separate border-spacing-0 text-left text-sm',
      detailTableLabelCell: () =>
        'w-36 border-b border-slate-950/8 py-2 pr-3 text-xs font-semibold uppercase tracking-[0.12em] text-slate-500',
      detailTableRow: () => 'align-top',
      detailTableValueCell: () => 'border-b border-slate-950/8 py-2 text-slate-800',
      detailTableValueText: () => 'wrap-break-word',
      detailTitle: () => 'truncate text-sm font-semibold text-slate-950',
      errorMessage: () =>
        'mt-4 rounded-md border border-red-300/60 bg-red-50 px-3 py-2 text-sm text-red-800',
      iconTone: ({ tone }) => documentWorkspaceIconClassByTone[coerceDocumentWorkspaceTone(tone)],
      inlineMeta: () => 'mt-1 wrap-break-word font-mono text-[0.68rem] text-slate-500',
      loadingMessage: () => 'mt-4 text-sm text-slate-500',
      nodeBadge: ({ label, tone }) =>
        resolveDocumentWorkspaceNodeBadgeClassName(coerceDocumentWorkspaceTone(tone), label),
      nodeLabel: ({ isActiveSearchMatch, isSearchMatch }) =>
        [
          'flex min-w-0 flex-wrap items-center gap-1.5 rounded-md px-1 py-0.5 text-sm text-slate-700',
          isSearchMatch ? 'bg-amber-50 ring-1 ring-amber-200/70' : '',
          isActiveSearchMatch ? 'bg-amber-100 ring-amber-300/80' : '',
        ].filter(Boolean).join(' '),
      nodeLabelText: () => 'min-w-0 truncate font-medium text-slate-900',
      root: () =>
        'h-full min-h-170 overflow-hidden rounded-md border border-slate-950/10 bg-white/72',
      section: () => 'mt-4',
      sectionBody: () => 'mt-2',
      sectionHeading: () => 'text-sm font-semibold text-slate-950',
      sectionHeadingRow: () => 'flex flex-wrap items-center gap-2',
      splitter: () =>
        'group flex w-2 items-stretch justify-center bg-slate-950/4 outline-none transition-colors hover:bg-slate-950/8 focus-visible:bg-slate-950/10',
      splitterHandle: () => 'my-3 w-px rounded-full bg-slate-950/14 group-hover:bg-slate-950/25',
      status: () =>
        'rounded-md border border-slate-950/8 bg-white/65 px-4 py-3 text-sm text-slate-500',
      tableCell: () => 'border-b border-slate-950/8 px-3 py-2 text-slate-800',
      tableHeader: () => 'sticky top-0 bg-white',
      tableHeaderCell: () =>
        'border-b border-slate-950/8 px-3 py-2 font-semibold uppercase tracking-[0.12em] text-slate-500',
      treePane: () => 'h-full overflow-auto px-2 py-2',
    },
    formSurface: {
      actionRow: () => 'flex flex-wrap items-end justify-end gap-2',
      content: () => 'grid gap-4',
      field: () => 'grid min-w-0 gap-1.5',
      group: ({ group }) => resolveInputFormGroupClassName(group),
      groups: () => 'grid gap-4',
      rangeGroup: () => 'grid gap-2 sm:grid-cols-2',
      root: () => 'relative z-30 overflow-visible bg-white/76',
    },
    metricDashboard: {
      root: () => 'bg-white/76',
    },
    navigationShell: {
      brandBadge: () => 'border-teal-700/15 bg-teal-50 text-teal-700',
      brandSubtitle: () => 'text-xs text-slate-500',
      brandTitle: () => 'text-sm font-medium text-slate-950',
      header: ({ design }) => {
        const density = normalizeDesignToken(design?.Density)
        const paddingClassName = density === 'comfortable' ? 'px-4 py-3' : 'px-4 py-2'

        return [
          'sticky top-0 z-40 border-b border-slate-950/8 bg-white/86',
          paddingClassName,
          'shadow-[0_12px_30px_rgba(15,23,42,0.06)] backdrop-blur-xl',
        ].join(' ')
      },
      headerContent: ({ design }) => [
        'mx-auto flex',
        resolveNavigationShellHeaderMaxWidthClassName(design),
        'flex-wrap items-center justify-between gap-3',
      ].join(' '),
      headerNavigation: () => 'flex flex-wrap items-center gap-1',
      navigationItem: () => 'text-slate-700',
      root: () => 'min-h-screen bg-background',
      slotRoot: ({ slot }) => resolveNavigationShellSlotRootClassName(slot),
    },
    routedSurface: {
      content: ({ design }) => resolveRoutedSurfaceContentClassName(design),
      root: () =>
        'flex min-h-[calc(100vh-4rem)] flex-col bg-[radial-gradient(circle_at_top_left,rgba(255,255,255,0.92),transparent_28%),linear-gradient(180deg,#f7f5f0_0%,#f6f8fb_48%,#e8eef6_100%)] px-4 py-5 text-slate-700',
    },
    recordDetail: {
      root: () => 'bg-white/76',
    },
    statusNotice: {
      tone: ({ tone }) => statusNoticeClassByTone[coercePresentationTone(tone)],
    },
    toggle: {
      tone: ({ tone }) =>
        [
          inactiveToggleClassName,
          toggleClassByTone[coercePresentationTone(tone)],
        ].join(' '),
    },
    viewSurface: {
      content: () => 'flex min-h-0 flex-1 flex-col gap-4',
      root: () => 'min-h-155 bg-white/76',
    },
  },
  components: {
    actionButton: {
      size: ({ action }) => resolveActionButtonSize(action),
      variant: ({ action, placement }) => resolveActionButtonVariant(action, placement),
    },
  },
  diagnostics: {
    navigationShellFrame: {
      ignoredFields: [
        'Role',
        'Variant',
        'Tone',
        'Layout',
      ],
      interpretedFields: [
        'Density',
        'Size',
      ],
    },
    navigationShellSlot: {
      ignoredFields: [
        'Role',
        'Variant',
        'Tone',
        'Size',
      ],
      interpretedFields: [
        'Density',
        'Layout',
      ],
    },
    routedSurfaceLayout: {
      ignoredFields: [
        'Variant',
        'Tone',
        'Density',
      ],
      interpretedFields: [
        'Role',
        'Size',
        'Layout',
      ],
    },
  },
  id: 'tailwind-default',
  target: 'tailwind',
}

const presentationToneValues = [
  'accent',
  'danger',
  'info',
  'muted',
  'neutral',
  'success',
  'warning',
] as const

type PresentationTone = (typeof presentationToneValues)[number]
type PresentationDocumentWorkspaceTone =
  | PresentationTone
  | 'cardinality'
  | 'data-type'
  | 'enum'
  | 'field'
  | 'graph'
  | 'loop'
  | 'nullability'
  | 'presence'
  | 'repeat'
  | 'requirement'
  | 'scalar'
  | 'segment'
  | 'sequence'
  | 'shape'
  | 'structural-type'
  | 'type'
  | 'union'

const presentationTones = new Set<string>(presentationToneValues)
const documentWorkspaceTones = new Set<string>([
  ...presentationToneValues,
  'cardinality',
  'data-type',
  'enum',
  'field',
  'graph',
  'loop',
  'nullability',
  'presence',
  'repeat',
  'requirement',
  'scalar',
  'segment',
  'sequence',
  'shape',
  'structural-type',
  'type',
  'union',
])

const badgeClassByTone = {
  accent: 'border-indigo-700/15 bg-indigo-50 text-indigo-700',
  danger: 'border-red-700/15 bg-red-50 text-red-700',
  info: 'border-sky-700/15 bg-sky-50 text-sky-700',
  muted: 'border-slate-950/10 bg-slate-100 text-slate-700',
  neutral: 'border-slate-950/10 bg-slate-100 text-slate-700',
  success: 'border-teal-700/15 bg-teal-50 text-teal-700',
  warning: 'border-amber-700/15 bg-amber-50 text-amber-800',
} satisfies Readonly<Record<PresentationTone, string>>

const statusNoticeClassByTone = {
  accent: 'border-indigo-300/50 bg-indigo-50 text-indigo-800',
  danger: 'border-red-300/60 bg-red-50 text-red-800',
  info: 'border-sky-300/50 bg-sky-50 text-sky-800',
  muted: 'border-slate-950/10 bg-slate-100 text-slate-700',
  neutral: 'border-slate-950/10 bg-slate-100 text-slate-700',
  success: 'border-teal-300/50 bg-teal-50 text-teal-800',
  warning: 'border-amber-300/50 bg-amber-50 text-amber-900',
} satisfies Readonly<Record<PresentationTone, string>>

const toggleClassByTone = {
  accent: 'data-[state=on]:border-indigo-700/15 data-[state=on]:bg-indigo-50 data-[state=on]:text-indigo-700',
  danger: 'data-[state=on]:border-red-700/15 data-[state=on]:bg-red-50 data-[state=on]:text-red-700',
  info: 'data-[state=on]:border-sky-700/15 data-[state=on]:bg-sky-50 data-[state=on]:text-sky-700',
  muted: 'data-[state=on]:border-slate-950/10 data-[state=on]:bg-slate-100 data-[state=on]:text-slate-700',
  neutral: 'data-[state=on]:border-slate-950/10 data-[state=on]:bg-slate-100 data-[state=on]:text-slate-700',
  success: 'data-[state=on]:border-teal-700/15 data-[state=on]:bg-teal-50 data-[state=on]:text-teal-700',
  warning: 'data-[state=on]:border-amber-700/15 data-[state=on]:bg-amber-50 data-[state=on]:text-amber-800',
} satisfies Readonly<Record<PresentationTone, string>>

const inactiveToggleClassName =
  'data-[state=off]:border-slate-950/10 data-[state=off]:bg-white data-[state=off]:text-slate-500'

const documentWorkspaceIconClassByTone = {
  accent: 'text-indigo-700',
  cardinality: 'text-violet-700',
  danger: 'text-red-700',
  'data-type': 'text-teal-700',
  enum: 'text-amber-700',
  field: 'text-indigo-700',
  graph: 'text-slate-700',
  info: 'text-sky-700',
  loop: 'text-violet-700',
  muted: 'text-slate-600',
  neutral: 'text-slate-700',
  nullability: 'text-amber-700',
  presence: 'text-sky-700',
  repeat: 'text-violet-700',
  requirement: 'text-amber-700',
  scalar: 'text-teal-700',
  segment: 'text-sky-700',
  sequence: 'text-sky-700',
  shape: 'text-sky-700',
  structural: 'text-sky-700',
  'structural-type': 'text-sky-700',
  success: 'text-teal-700',
  type: 'text-teal-700',
  union: 'text-violet-700',
  warning: 'text-amber-700',
} satisfies Readonly<Record<PresentationDocumentWorkspaceTone | 'structural', string>>

function resolveRoutedSurfaceContentClassName(
  design: DesignIntent | null | undefined,
): string {
  if (hasAnyDesignToken(design?.Role, fullWidthRoleTokens)) {
    return 'flex min-h-0 w-full max-w-none flex-1 flex-col gap-5'
  }

  if (hasAnyDesignToken(design?.Role, workspaceRoleTokens)) {
    return 'mx-auto flex min-h-0 w-full max-w-7xl flex-1 flex-col gap-5'
  }

  if (hasAnyDesignToken(design?.Role, wideRoleTokens)) {
    return 'mx-auto flex min-h-0 w-full max-w-420 flex-1 flex-col gap-5'
  }

  if (hasAnyDesignToken(design?.Layout, workspaceRoleTokens)) {
    return 'mx-auto flex min-h-0 w-full max-w-7xl flex-1 flex-col gap-5'
  }

  if (hasAnyDesignToken(design?.Layout, wideRoleTokens)) {
    return 'mx-auto flex min-h-0 w-full max-w-420 flex-1 flex-col gap-5'
  }

  const size = normalizeDesignToken(design?.Size)
  if (size && workspaceSizeHints.has(size)) {
    return 'mx-auto flex min-h-0 w-full max-w-7xl flex-1 flex-col gap-5'
  }

  if (size && wideSizeHints.has(size)) {
    return 'mx-auto flex min-h-0 w-full max-w-420 flex-1 flex-col gap-5'
  }

  return 'mx-auto flex min-h-0 w-full max-w-360 flex-1 flex-col gap-5'
}

function resolveActionButtonVariant(
  action: ActionDefinition | null,
  placement: ActionPlacementDefinition,
): PresentationActionButtonVariant {
  const designVariant = normalizeDesignToken(action?.Design?.Variant)
  const tone = normalizeDesignToken(action?.Design?.Tone)
  const intent = normalizeDesignToken(placement.Intent)
  if (
    tone === 'warning' ||
    tone === 'danger' ||
    intent === 'warning' ||
    intent === 'danger' ||
    intent === 'destructive'
  ) {
    return 'destructive'
  }

  if (intent === 'primary' || designVariant === 'primary') {
    return 'default'
  }

  if (designVariant === 'ghost' && placement.Region.startsWith('row')) {
    return 'ghost'
  }

  return 'outline'
}

function resolveActionButtonSize(
  action: ActionDefinition | null,
): PresentationActionButtonSize {
  switch (normalizeDesignToken(action?.Design?.Size)) {
    case 'xs':
      return 'xs'
    case 'lg':
      return 'lg'
    case 'sm':
    case 'md':
    default:
      return 'sm'
  }
}

function coercePresentationTone(value: string | null | undefined): PresentationTone {
  const normalized = normalizeDesignToken(value)
  return normalized && presentationTones.has(normalized)
    ? normalized as PresentationTone
    : 'info'
}

function coerceDocumentWorkspaceTone(
  value: string | null | undefined,
): PresentationDocumentWorkspaceTone {
  const normalized = normalizeDesignToken(value)
  return normalized && documentWorkspaceTones.has(normalized)
    ? normalized as PresentationDocumentWorkspaceTone
    : 'info'
}

function resolveDocumentWorkspaceNodeBadgeClassName(
  tone: PresentationDocumentWorkspaceTone,
  label: string | null | undefined,
) {
  if (tone === 'requirement') {
    return label?.toUpperCase() === 'M'
      ? 'h-4 rounded-md border-amber-700/15 bg-amber-50 px-1.5 text-[0.67rem] text-amber-700'
      : 'h-4 rounded-md border-slate-950/10 bg-slate-100 px-1.5 text-[0.67rem] text-slate-600'
  }

  const toneClassName = badgeClassByTone[coerceDocumentWorkspaceBadgeTone(tone)]
  const maxWidthClassName = tone === 'type' ? 'max-w-80 truncate' : ''
  return ['h-4 rounded-md px-1.5 text-[0.67rem]', maxWidthClassName, toneClassName]
    .filter(Boolean)
    .join(' ')
}

function coerceDocumentWorkspaceBadgeTone(
  tone: PresentationDocumentWorkspaceTone,
): PresentationTone {
  switch (tone) {
    case 'cardinality':
    case 'repeat':
    case 'union':
      return 'accent'
    case 'data-type':
    case 'scalar':
    case 'success':
    case 'type':
      return 'success'
    case 'enum':
    case 'nullability':
    case 'requirement':
    case 'warning':
      return 'warning'
    case 'field':
    case 'shape':
    case 'info':
    case 'presence':
    case 'sequence':
      return 'info'
    case 'danger':
      return 'danger'
    case 'muted':
    case 'neutral':
    case 'graph':
    case 'loop':
    case 'segment':
    case 'structural-type':
      return 'neutral'
    case 'accent':
      return 'accent'
  }
}

function resolveNavigationShellHeaderMaxWidthClassName(
  design: DesignIntent | null | undefined,
) {
  switch (normalizeDesignToken(design?.Size)) {
    case '2xl':
    case 'full':
    case 'max':
    case 'screen':
    case 'xl':
      return 'max-w-7xl'
    case 'lg':
    case 'wide':
    default:
      return 'max-w-420'
  }
}

function resolveNavigationShellSlotRootClassName(
  slot: NavigationShellSlotDefinition,
): string {
  const density = normalizeDesignToken(slot.Design?.Density)
  const gapClassName = density === 'comfortable' ? 'gap-2' : 'gap-1'

  if (isNavigationShellSlotKind(slot, navigationShellSlotKinds.brand, 'Brand')) {
    return 'flex items-center gap-2'
  }

  if (
    isNavigationShellSlotKind(
      slot,
      navigationShellSlotKinds.primaryNavigation,
      'PrimaryNavigation',
    )
  ) {
    return `flex flex-wrap items-center ${gapClassName}`
  }

  if (
    isNavigationShellSlotKind(
      slot,
      navigationShellSlotKinds.utilityActions,
      'UtilityActions',
    )
  ) {
    return 'flex items-center gap-2'
  }

  if (
    isNavigationShellSlotKind(
      slot,
      navigationShellSlotKinds.systemNotices,
      'SystemNotices',
    ) ||
    isNavigationShellSlotKind(
      slot,
      navigationShellSlotKinds.routedContent,
      'RoutedContent',
    )
  ) {
    return 'contents'
  }

  switch (normalizeDesignToken(slot.Design?.Layout ?? slot.Placement)) {
    case 'topcenter':
    case 'top-center':
      return `flex flex-wrap items-center ${gapClassName}`
    case 'topright':
    case 'top-right':
      return 'flex items-center gap-2'
    case 'belownavigation':
    case 'below-navigation':
    case 'main':
      return 'contents'
    default:
      return `flex flex-wrap items-center ${gapClassName}`
  }
}

function resolveInputFormGroupClassName(group: InputFormGroupDefinition) {
  const kind = String(group.Kind).toLocaleLowerCase()
  if (group.Id === 'identity' || kind === String(inputFormGroupKinds.identity).toLocaleLowerCase()) {
    return 'grid gap-3 lg:grid-cols-[1fr_1fr_minmax(18rem,1.25fr)]'
  }

  if (group.Id === 'lifecycle' || kind === String(inputFormGroupKinds.lifecycle).toLocaleLowerCase()) {
    return 'grid gap-3'
  }

  if (group.Display.Orientation === 'horizontal') {
    return 'flex flex-wrap items-end gap-3'
  }

  return 'grid gap-3 lg:grid-cols-2'
}

function isNavigationShellSlotKind(
  slot: NavigationShellSlotDefinition,
  numericKind: unknown,
  label: string,
) {
  const normalizedKind = String(slot.Kind).replace(/[-_\s]/g, '').toLowerCase()
  const normalizedLabel = label.replace(/[-_\s]/g, '').toLowerCase()

  return slot.Kind === numericKind || normalizedKind === normalizedLabel
}

function hasAnyDesignToken(
  value: string | null | undefined,
  tokens: ReadonlySet<string>,
): boolean {
  return getDesignTokenCandidates(value).some((token) => tokens.has(token))
}

function getDesignTokenCandidates(value: string | null | undefined) {
  const normalized = normalizeDesignToken(value)
  if (!normalized) {
    return []
  }

  const parts = normalized
    .split(/[^a-z0-9]+/u)
    .filter(Boolean)
  return [
    normalized,
    ...parts,
    ...parts.flatMap((part, index) =>
      parts.slice(index + 1).map((next) => `${part}-${next}`),
    ),
  ]
}

function normalizeDesignToken(value: string | null | undefined) {
  return value?.trim().toLowerCase() || null
}
