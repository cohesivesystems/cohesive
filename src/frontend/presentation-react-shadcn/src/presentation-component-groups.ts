import { clsx, type ClassValue } from 'clsx'
import {
  type ChangeEvent,
  createElement,
  type ExoticComponent,
  Fragment,
  Suspense,
  type ComponentType,
  type CSSProperties,
  type MouseEventHandler,
  type ReactNode,
} from 'react'
import { twMerge } from 'tailwind-merge'

import type {
  InputFormDefinition,
  InputFormFieldDefinition,
  InputFormGroupDefinition,
  ViewChromeSlotDefinition,
  ViewRegionDefinition,
} from '@cohesive/presentation-contracts'
import {
  createPresentationTestAttributes,
} from '@cohesive/presentation-core'
import type {
  PresentationActionButtonSize,
  PresentationActionButtonVariant,
} from '@cohesive/presentation-tailwind'

/** Props accepted by action buttons projected from presentation actions. */
export interface PresentationActionButtonProps {
  readonly 'aria-label'?: string
  readonly 'aria-pressed'?: boolean
  readonly 'data-presentation-action-id'?: string
  readonly 'data-presentation-collection-slot-id'?: string
  readonly 'data-presentation-view-id'?: string
  readonly children?: ReactNode
  readonly className?: string
  readonly disabled?: boolean
  readonly onClick?: MouseEventHandler<HTMLButtonElement>
  readonly size?: PresentationActionButtonSize
  readonly title?: string
  readonly type?: 'button' | 'reset' | 'submit'
  readonly variant?: PresentationActionButtonVariant
}

/** Action component group used by renderers that need command or intent buttons. */
export interface PresentationActionComponentSystemComponents {
  readonly ActionButton: (props: PresentationActionButtonProps) => ReactNode
}

/** Visual variants supported by badge primitives in the shadcn adapter layer. */
export type PresentationBadgeVariant =
  'default' | 'destructive' | 'ghost' | 'link' | 'outline' | 'secondary'

/** Props accepted by semantic badge renderers. */
export interface PresentationBadgeProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly variant?: PresentationBadgeVariant
}

/** Badge component group shared by presentation renderers that need compact labels. */
export interface PresentationBadgeComponentSystemComponents {
  readonly Badge: (props: PresentationBadgeProps) => ReactNode
}

/** Props for navigation links after route targets have been projected to frontend paths. */
export interface PresentationNavigationLinkProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly isActive?: boolean
  readonly to: string
}

/** Navigation component group used to render projected route links. */
export interface PresentationNavigationComponentSystemComponents {
  readonly NavigationLink: (props: PresentationNavigationLinkProps) => ReactNode
}

/** Minimal router link contract required by the navigation adapter. */
export interface PresentationNavigationRouterLinkProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly to: string
}

/** Button style options used to derive class names for navigation links. */
export interface PresentationNavigationButtonClassNameOptions {
  readonly size: 'sm'
  readonly variant: 'ghost' | 'secondary'
}

/** Primitive dependencies required to build the action component group. */
export interface CreateShadcnActionComponentsOptions {
  readonly Button: ComponentType<PresentationActionButtonProps>
}

/** Primitive dependencies required to build the badge component group. */
export interface CreateShadcnBadgeComponentsOptions {
  readonly Badge: ComponentType<PresentationBadgeProps>
}

/** Primitive dependencies required to build the navigation component group. */
export interface CreateShadcnNavigationComponentsOptions {
  readonly Link: ComponentType<PresentationNavigationRouterLinkProps>
  readonly buttonClassName: (options: PresentationNavigationButtonClassNameOptions) => string
}

/** Adapter contract for the underlying tabs root primitive. */
export interface PresentationTabsRootPrimitiveProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly 'data-presentation-view-id'?: string
  readonly onValueChange: (value: string) => void
  readonly value: string
}

/** Adapter contract for the underlying tabs list primitive. */
export interface PresentationTabsListPrimitiveProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly 'data-presentation-view-id'?: string
}

/** Adapter contract for an underlying tabs content primitive. */
export interface PresentationTabsContentPrimitiveProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly 'data-presentation-region-id'?: string
  readonly 'data-presentation-view-id'?: string
  readonly forceMount?: true
  readonly value: string
}

/** Adapter contract for an underlying tabs trigger primitive. */
export interface PresentationTabsTriggerPrimitiveProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly 'data-presentation-region-id'?: string
  readonly 'data-presentation-view-id'?: string
  readonly value: string
}

/** Primitive dependencies required to build semantic tab components. */
export interface CreateShadcnTabsComponentsOptions {
  readonly Tabs: ComponentType<PresentationTabsRootPrimitiveProps>
  readonly TabsContent: ComponentType<PresentationTabsContentPrimitiveProps>
  readonly TabsList: ComponentType<PresentationTabsListPrimitiveProps>
  readonly TabsTrigger: ComponentType<PresentationTabsTriggerPrimitiveProps>
}

/** Props for a semantic multi-choice toggle group control. */
export interface PresentationChoiceToggleGroupProps {
  readonly 'aria-label'?: string
  readonly children?: ReactNode
  readonly className?: string
  readonly onValueChange?: (value: string[]) => void
  readonly value?: readonly string[]
}

/** Props for an individual semantic choice toggle option. */
export interface PresentationChoiceToggleItemProps {
  readonly 'aria-label'?: string
  readonly children?: ReactNode
  readonly className?: string
  readonly value: string
}

/** Props for a boolean input control projected from a presentation field. */
export interface PresentationCheckboxControlProps {
  readonly 'aria-label': string
  readonly checked: boolean
  readonly className?: string
  readonly onCheckedChange: (checked: boolean) => void
}

/** Props for date-time filter controls exposed to presentation form renderers. */
export interface PresentationDateTimeFilterControlProps<
  TDateTimeFilterValue = unknown,
> {
  readonly emptyLabel?: string
  readonly incrementMinutes?: number
  readonly label?: string
  readonly onValueChange: (value: TDateTimeFilterValue) => void
  readonly showTimezone?: boolean
  readonly value: TDateTimeFilterValue
}

/** Adapter contract for the date-time primitive consumed by form controls. */
export interface PresentationDateTimeFilterPrimitiveProps<
  TDateTimeFilterValue,
> {
  readonly emptyLabel?: string
  readonly incrementMinutes?: number
  readonly label?: string
  readonly onChange: (value: TDateTimeFilterValue) => void
  readonly showTimezone?: boolean
  readonly value: TDateTimeFilterValue
}

/** Props for labels rendered next to projected form fields. */
export interface PresentationFormFieldLabelProps {
  readonly children?: ReactNode
  readonly className?: string
}

/** Props for the root element of an input form projected from backend presentation IR. */
export interface PresentationInputFormProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly form: InputFormDefinition
  readonly viewId?: string | null
}

/** Props for a form action row bound to a projected input form. */
export interface PresentationInputFormActionRowProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly form: InputFormDefinition
}

/** Props for grouping one field's control content. */
export interface PresentationInputFormControlGroupProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly field: InputFormFieldDefinition
}

/** Props for a slot that receives a projected form control. */
export interface PresentationInputFormControlSlotProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly field: InputFormFieldDefinition
}

/** Props for the composed label, control, and message of one input field. */
export interface PresentationInputFormFieldProps {
  readonly className?: string
  readonly control: ReactNode
  readonly field: InputFormFieldDefinition
  readonly label: ReactNode
  readonly message?: ReactNode
}

/** Props for validation or helper text associated with a projected field. */
export interface PresentationInputFormFieldMessageProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly field: InputFormFieldDefinition
  readonly tone?: 'default' | 'error' | 'warning'
}

/** Props for a named group within a projected input form. */
export interface PresentationInputFormGroupProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly group: InputFormGroupDefinition
}

/** Props for the container that renders all groups in a projected input form. */
export interface PresentationInputFormGroupsProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly form: InputFormDefinition
}

/** Option contract for select controls projected from presentation choices. */
export interface PresentationSelectControlOption {
  readonly label: string
  readonly value: string
}

/** Props for a select control bound to a string presentation field value. */
export interface PresentationSelectControlProps {
  readonly 'aria-label': string
  readonly className?: string
  readonly disabled?: boolean
  readonly onValueChange: (value: string) => void
  readonly options: readonly PresentationSelectControlOption[]
  readonly value: string
}

/** Props for a text-like input control bound to a presentation field value. */
export interface PresentationTextInputControlProps {
  readonly 'aria-label': string
  readonly className?: string
  readonly onValueChange: (value: string) => void
  readonly placeholder?: string
  readonly type?: string
  readonly value: string
}

/** Adapter contract for the underlying multi-select toggle group primitive. */
export interface PresentationChoiceToggleGroupPrimitiveProps {
  readonly 'aria-label'?: string
  readonly children?: ReactNode
  readonly className?: string
  readonly onValueChange?: (value: string[]) => void
  readonly type: 'multiple'
  readonly value: string[]
}

/** Adapter contract for the underlying text input primitive. */
export interface PresentationInputPrimitiveProps {
  readonly 'aria-label': string
  readonly className?: string
  readonly onChange: (event: ChangeEvent<HTMLInputElement>) => void
  readonly placeholder?: string
  readonly type?: string
  readonly value: string
}

/** Primitive dependencies required to build semantic form components. */
export interface CreateShadcnFormComponentsOptions<
  TDateTimeFilterValue = unknown,
> {
  readonly Button: ComponentType<PresentationActionButtonProps>
  readonly DateTimeFilter: ComponentType<
    PresentationDateTimeFilterPrimitiveProps<TDateTimeFilterValue>
  >
  readonly Input: ComponentType<PresentationInputPrimitiveProps>
  readonly Label: ComponentType<PresentationFormFieldLabelProps>
  readonly ToggleGroup: ComponentType<PresentationChoiceToggleGroupPrimitiveProps>
  readonly ToggleGroupItem: ComponentType<PresentationChoiceToggleItemProps>
}

/** Props for collection chrome regions such as summaries, details, and query slots. */
export interface PresentationCollectionChromeSlotProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly slotId?: string
  readonly viewId?: string | null
}

/** Props for collection pagination state and navigation actions. */
export interface PresentationCollectionPaginationBarProps {
  readonly canGoNextPage: boolean
  readonly canGoPreviousPage: boolean
  readonly className?: string
  readonly firstIcon?: ReactNode
  readonly isFetching: boolean
  readonly loadingIcon?: ReactNode
  readonly nextIcon?: ReactNode
  readonly onFirstPage: () => void
  readonly onNextPage: () => void
  readonly onPreviousPage: () => void
  readonly pageLabel: string
  readonly pageSizeLabel: string
  readonly previousIcon?: ReactNode
  readonly shownLabel: string
  readonly slotId?: string | null
  readonly totalLabel: string
  readonly viewId?: string | null
}

/** Props for row-level action containers in collection projections. */
export interface PresentationCollectionRowActionsProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly rowLabel?: string | null
  readonly slotId?: string
}

/** Props for action toolbars shown when collection rows are selected. */
export interface PresentationCollectionSelectionActionToolbarProps {
  readonly actions?: ReactNode
  readonly className?: string
  readonly selectedCount: number
  readonly selectedLabel: string
  readonly slotId?: string | null
}

/** Collection chrome component group for projected query, summary, body, detail, and action slots. */
export interface PresentationCollectionChromeComponentSystemComponents {
  readonly CollectionBodySlot: (props: PresentationCollectionChromeSlotProps) => ReactNode
  readonly CollectionDetailSlot: (props: PresentationCollectionChromeSlotProps) => ReactNode
  readonly CollectionPaginationBar: (props: PresentationCollectionPaginationBarProps) => ReactNode
  readonly CollectionQueryFormSlot: (props: PresentationCollectionChromeSlotProps) => ReactNode
  readonly CollectionRowActions: (props: PresentationCollectionRowActionsProps) => ReactNode
  readonly CollectionSelectionActionToolbar: (
    props: PresentationCollectionSelectionActionToolbarProps
  ) => ReactNode
  readonly CollectionSummarySlot: (props: PresentationCollectionChromeSlotProps) => ReactNode
}

/** Primitive dependencies required to build collection chrome components. */
export interface CreateShadcnCollectionChromeComponentsOptions {
  readonly Button: ComponentType<PresentationActionButtonProps>
}

/** Supported layout modes for rendering collection tables with optional detail content. */
export type PresentationCollectionDetailLayoutMode =
  'drawer' | 'none' | 'side-panel' | 'stack'

/** Props for arranging a collection table with its projected detail view. */
export interface PresentationCollectionDetailLayoutProps {
  readonly detail: ReactNode
  readonly mode: PresentationCollectionDetailLayoutMode
  readonly table: ReactNode
}

/** Generic data table renderer contract supplied by collection adapters. */
export type PresentationDataTableRenderer = <TData extends object>(
  props: never & { readonly __data?: TData },
) => ReactNode

/** Props for a row action menu wrapper and trigger. */
export interface PresentationRowActionMenuProps {
  readonly children?: ReactNode
  readonly trigger: ReactNode
}

/** Props for the interactive trigger of a row action menu. */
export interface PresentationRowActionMenuTriggerProps {
  readonly 'aria-label': string
  readonly children?: ReactNode
}

/** Props for one executable item in a row action menu. */
export interface PresentationRowActionMenuItemProps {
  readonly actionId?: string | null
  readonly children?: ReactNode
  readonly collectionSlotId?: string | null
  readonly disabled?: boolean
  readonly onClick?: MouseEventHandler<HTMLButtonElement>
  readonly viewId?: string | null
}

/** Style options used to derive row action menu trigger classes. */
export interface PresentationRowActionMenuTriggerClassNameOptions {
  readonly size: 'icon-sm'
  readonly variant: 'ghost'
}

/** Collection component group for tables, detail layout, and row actions. */
export interface PresentationCollectionComponentSystemComponents<
  TDataTableRenderer extends PresentationDataTableRenderer =
    PresentationDataTableRenderer,
> {
  readonly CollectionDetailLayout: (props: PresentationCollectionDetailLayoutProps) => ReactNode
  readonly DataTable: TDataTableRenderer
  readonly RowActionMenu: (props: PresentationRowActionMenuProps) => ReactNode
  readonly RowActionMenuItem: (props: PresentationRowActionMenuItemProps) => ReactNode
  readonly RowActionMenuTrigger: (props: PresentationRowActionMenuTriggerProps) => ReactNode
}

/** Primitive dependencies required to build collection components. */
export interface CreateShadcnCollectionComponentsOptions<
  TDataTableRenderer extends PresentationDataTableRenderer =
    PresentationDataTableRenderer,
> {
  readonly CollectionDetailLayout: (props: PresentationCollectionDetailLayoutProps) => ReactNode
  readonly DataTable: TDataTableRenderer
  readonly rowActionMenuTriggerClassName: (
    options: PresentationRowActionMenuTriggerClassNameOptions
  ) => string
}

/** Props for a view surface that frames projected presentation content. */
export interface PresentationViewSurfaceProps {
  readonly action?: ReactNode
  readonly children?: ReactNode
  readonly className?: string
  readonly collapsible?: boolean
  readonly collapsed?: boolean
  readonly collapseLabel?: string
  readonly contentClassName?: string
  readonly contentTopInset?: PresentationViewSurfaceContentTopInset | null
  readonly defaultCollapsed?: boolean
  readonly description?: string
  readonly eyebrow?: string
  readonly onCollapsedChange?: (collapsed: boolean) => void
  readonly title?: string
  readonly verticalResize?: boolean | PresentationViewSurfaceVerticalResizeOptions | null
  readonly viewId?: string | null
}

/** Controls the top inset applied between view surface chrome and content. */
export type PresentationViewSurfaceContentTopInset =
  | 'default'
  | 'none'

/** Defines vertical resize behavior for projected view surfaces. */
export interface PresentationViewSurfaceVerticalResizeOptions {
  /** Selects one resize affordance for changing the surface height. */
  readonly control?: PresentationViewSurfaceVerticalResizeControl
  readonly defaultHeightRem?: number
  readonly enabled?: boolean
  readonly maxHeightRem?: number
  readonly minHeightRem?: number
  readonly stepRem?: number
}

/** Mutually exclusive resize controls exposed by projected view surfaces. */
export type PresentationViewSurfaceVerticalResizeControl =
  | 'drag-handle'
  | 'header-buttons'

/** Props for chrome content placed around a view surface by semantic placement. */
export interface PresentationViewSurfaceChromePlacementProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly placement: string | number
  readonly viewId?: string | null
}

/** Props for composing main content with before, after, and footer chrome. */
export interface PresentationViewSurfaceContentProps {
  readonly afterContentChrome?: ReactNode
  readonly beforeContentChrome?: ReactNode
  readonly children?: ReactNode
  readonly footerChrome?: ReactNode
  readonly viewId?: string | null
}

/** Props for merging primary view actions with additional header chrome. */
export interface PresentationViewSurfaceHeaderActionsProps {
  readonly action?: ReactNode
  readonly chrome?: ReactNode
  readonly className?: string
  readonly viewId?: string | null
}

/** Surface component group used to frame projected views and their chrome. */
export interface PresentationSurfaceComponentSystemComponents {
  readonly ViewSurface: (props: PresentationViewSurfaceProps) => ReactNode
  readonly ViewSurfaceChromePlacement: (
    props: PresentationViewSurfaceChromePlacementProps
  ) => ReactNode
  readonly ViewSurfaceContent: (props: PresentationViewSurfaceContentProps) => ReactNode
  readonly ViewSurfaceHeaderActions: (
    props: PresentationViewSurfaceHeaderActionsProps
  ) => ReactNode
}

/** Primitive dependencies required to build view surface components. */
export interface CreateShadcnSurfaceComponentsOptions {
  readonly Surface: ComponentType<PresentationViewSurfaceProps>
}

/** Props for the document workspace detail panel shown beside a tree or surface. */
export interface PresentationDocumentWorkspaceDetailPanelProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly contentClassName?: string
  readonly emptyClassName?: string
  readonly emptyLabel?: string
  readonly headerClassName?: string
  readonly headerContentClassName?: string
  readonly icon?: ReactNode
  readonly subtitle?: string
  readonly subtitleClassName?: string
  readonly title?: string
  readonly titleClassName?: string
}

/** Props for the root document workspace shell. */
export interface PresentationDocumentWorkspaceShellProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly contentClassName?: string
  readonly viewId?: string | null
}

/** Resolved rendering options for a document workspace surface slot. */
export interface PresentationDocumentWorkspaceSurfaceSlotRenderOptions {
  readonly chromeHeaderClassName?: string
  readonly className?: string
  readonly collapsible?: boolean
  readonly contentClassName?: string
}

/** Props for a named document workspace surface slot declared by backend presentation IR. */
export interface PresentationDocumentWorkspaceSurfaceSlotProps {
  readonly children?: ReactNode
  readonly regionId?: string | null
  readonly renderSurface?: (
    options: PresentationDocumentWorkspaceSurfaceSlotRenderOptions,
  ) => ReactNode
  readonly role?: string | number | null
  readonly slot: string
  readonly viewId?: string | null
}

/** Props for a document workspace layout group projected from layout semantics. */
export interface PresentationDocumentWorkspaceLayoutGroupProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly layoutNodeId?: string | null
  readonly orientation?: string | number | null
  readonly style?: CSSProperties
  readonly workspaceViewId?: string | null
}

/** Props for one pane in a projected document workspace layout. */
export interface PresentationDocumentWorkspaceLayoutPaneProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly contentClassName?: string
  readonly layoutNodeId?: string | null
  readonly title: ReactNode
  readonly titleClassName?: string
  readonly viewId?: string | null
  readonly workspaceViewId?: string | null
}

/** Badge metadata rendered inside a document workspace node label. */
export interface PresentationDocumentWorkspaceNodeLabelBadge {
  readonly className?: string
  readonly label: string
}

/** Props for labels attached to tree or graph nodes in a document workspace. */
export interface PresentationDocumentWorkspaceNodeLabelProps {
  readonly badges?: readonly PresentationDocumentWorkspaceNodeLabelBadge[]
  readonly className?: string
  readonly icon?: ReactNode
  readonly label: string
  readonly labelClassName?: string
}

/** Props for one item in a document workspace tree. */
export interface PresentationDocumentWorkspaceTreeItemProps {
  readonly children?: ReactNode
  readonly itemId: string
  readonly label: ReactNode
}

/** Props for lightweight document workspace status content. */
export interface PresentationDocumentWorkspaceStatusProps {
  readonly children?: ReactNode
  readonly className?: string
}

/** Label-value pair rendered by the document workspace details table. */
export interface PresentationDocumentWorkspaceTableDetail {
  readonly label: string
  readonly value: ReactNode
}

/** Props for tabular document workspace metadata. */
export interface PresentationDocumentWorkspaceTableProps {
  readonly details: readonly PresentationDocumentWorkspaceTableDetail[]
  readonly labelCellClassName?: string
  readonly rowClassName?: string
  readonly tableClassName?: string
  readonly valueCellClassName?: string
  readonly valueTextClassName?: string
}

/** Props for split tree/detail layout in document workspaces. */
export interface PresentationDocumentWorkspaceTreeLayoutProps {
  readonly className?: string
  readonly detail: ReactNode
  readonly detailDefaultSize?: number | string
  readonly detailId: string
  readonly detailMinSize?: number | string
  readonly splitterClassName?: string
  readonly splitterHandleClassName?: string
  readonly tree: ReactNode
  readonly treeDefaultSize?: number | string
  readonly treeId: string
  readonly treeMinSize?: number | string
}

/** Props for controlled tree state in document workspace navigation. */
export interface PresentationDocumentWorkspaceTreeViewProps {
  readonly ariaLabel: string
  readonly children?: ReactNode
  readonly expandedItemIds: readonly string[]
  readonly onExpandedItemIdsChange: (itemIds: readonly string[]) => void
  readonly onSelectedItemIdChange: (itemId: string | null) => void
  readonly selectedItemId: string | null
}

/** Adapter contract for the split-pane group primitive used by tree layouts. */
export interface PresentationDocumentWorkspaceTreeLayoutGroupPrimitiveProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly orientation: 'horizontal'
}

/** Adapter contract for split-pane panels used by tree layouts. */
export interface PresentationDocumentWorkspaceTreeLayoutPanelPrimitiveProps {
  readonly children?: ReactNode
  readonly defaultSize: number | string
  readonly id: string
  readonly minSize: number | string
}

/** Adapter contract for the split-pane separator used by tree layouts. */
export interface PresentationDocumentWorkspaceTreeLayoutSeparatorPrimitiveProps {
  readonly children?: ReactNode
  readonly className?: string
}

/** Primitive dependencies required to build a document workspace tree layout. */
export interface CreateShadcnDocumentWorkspaceTreeLayoutOptions {
  readonly Group: ComponentType<PresentationDocumentWorkspaceTreeLayoutGroupPrimitiveProps>
  readonly Panel: ComponentType<PresentationDocumentWorkspaceTreeLayoutPanelPrimitiveProps>
  readonly Separator: ComponentType<
    PresentationDocumentWorkspaceTreeLayoutSeparatorPrimitiveProps
  >
}

/** Adapter contract for the underlying document tree item primitive. */
export interface PresentationDocumentWorkspaceTreeItemPrimitiveProps {
  readonly children?: ReactNode
  readonly itemId: string
  readonly label: ReactNode
}

/** Primitive dependency required to build document workspace tree items. */
export interface CreateShadcnDocumentWorkspaceTreeItemOptions {
  readonly TreeItem: ComponentType<PresentationDocumentWorkspaceTreeItemPrimitiveProps>
}

/** Adapter contract for the underlying controlled document tree view primitive. */
export interface PresentationDocumentWorkspaceTreeViewPrimitiveProps<TSx = unknown> {
  readonly 'aria-label': string
  readonly children?: ReactNode
  readonly expandedItems: string[]
  readonly onExpandedItemsChange: (
    event: unknown,
    itemIds: string[],
  ) => void
  readonly onSelectedItemsChange: (
    event: unknown,
    itemId: string | string[] | null,
  ) => void
  readonly selectedItems: string | null
  readonly sx?: TSx
}

/** Primitive dependency and optional style object for document workspace tree views. */
export interface CreateShadcnDocumentWorkspaceTreeViewOptions<TSx = unknown> {
  readonly TreeView: ComponentType<PresentationDocumentWorkspaceTreeViewPrimitiveProps<TSx>>
  readonly sx?: TSx
}

/** JSON document component contract that supports regular and exotic React components. */
export type PresentationDocumentWorkspaceJsonComponent<TProps extends object> =
  | ComponentType<TProps>
  | ExoticComponent<TProps>

/** Primitive dependency required to render JSON document diffs. */
export interface CreateShadcnJsonDocumentDiffOptions<TProps extends object> {
  readonly JsonDocumentDiff: PresentationDocumentWorkspaceJsonComponent<TProps>
}

/** Primitive dependency and fallback configuration for lazy JSON document editors. */
export interface CreateShadcnJsonDocumentEditorOptions<TProps extends object> {
  readonly fallbackClassName: (props: TProps) => string
  readonly fallbackLabel?: ReactNode
  readonly JsonDocumentEditor: PresentationDocumentWorkspaceJsonComponent<TProps>
}

/** Context used to resolve styling and behavior for a document workspace surface slot. */
export interface PresentationDocumentWorkspaceSurfaceSlotOptionsContext {
  readonly role?: string | number | null
  readonly slot: string
}

/** Document workspace component group for semantic document navigation, surfaces, and JSON tools. */
export interface PresentationDocumentWorkspaceComponentSystemComponents<
  TTreeControlsProps extends object = object,
  TJsonDocumentDiffProps extends object = object,
  TJsonDocumentEditorProps extends object = object,
> {
  readonly DocumentWorkspaceDetailPanel: (
    props: PresentationDocumentWorkspaceDetailPanelProps
  ) => ReactNode
  readonly DocumentWorkspaceLayoutGroup: (
    props: PresentationDocumentWorkspaceLayoutGroupProps
  ) => ReactNode
  readonly DocumentWorkspaceLayoutPane: (
    props: PresentationDocumentWorkspaceLayoutPaneProps
  ) => ReactNode
  readonly DocumentWorkspaceNodeLabel: (
    props: PresentationDocumentWorkspaceNodeLabelProps
  ) => ReactNode
  readonly DocumentWorkspaceShell: (
    props: PresentationDocumentWorkspaceShellProps
  ) => ReactNode
  readonly DocumentWorkspaceSurfaceSlot: (
    props: PresentationDocumentWorkspaceSurfaceSlotProps
  ) => ReactNode
  readonly DocumentWorkspaceTreeControls: (props: TTreeControlsProps) => ReactNode
  readonly DocumentWorkspaceTreeItem: (props: PresentationDocumentWorkspaceTreeItemProps) => ReactNode
  readonly DocumentWorkspaceStatus: (props: PresentationDocumentWorkspaceStatusProps) => ReactNode
  readonly DocumentWorkspaceTable: (props: PresentationDocumentWorkspaceTableProps) => ReactNode
  readonly DocumentWorkspaceTreeLayout: (props: PresentationDocumentWorkspaceTreeLayoutProps) => ReactNode
  readonly DocumentWorkspaceTreeView: (
    props: PresentationDocumentWorkspaceTreeViewProps
  ) => ReactNode
  readonly JsonDocumentDiff: (props: TJsonDocumentDiffProps) => ReactNode
  readonly JsonDocumentEditor: (props: TJsonDocumentEditorProps) => ReactNode
}

/** Dependencies required to build the document workspace component group. */
export interface CreateShadcnDocumentWorkspaceComponentsOptions<
  TTreeControlsProps extends object = object,
  TJsonDocumentDiffProps extends object = object,
  TJsonDocumentEditorProps extends object = object,
> {
  readonly Badge: ComponentType<PresentationBadgeProps>
  readonly DocumentWorkspaceTreeControls: (props: TTreeControlsProps) => ReactNode
  readonly DocumentWorkspaceTreeItem: (
    props: PresentationDocumentWorkspaceTreeItemProps
  ) => ReactNode
  readonly DocumentWorkspaceTreeLayout: (
    props: PresentationDocumentWorkspaceTreeLayoutProps
  ) => ReactNode
  readonly DocumentWorkspaceTreeView: (
    props: PresentationDocumentWorkspaceTreeViewProps
  ) => ReactNode
  readonly JsonDocumentDiff: (props: TJsonDocumentDiffProps) => ReactNode
  readonly JsonDocumentEditor: (props: TJsonDocumentEditorProps) => ReactNode
  readonly resolveSurfaceSlotOptions: (
    context: PresentationDocumentWorkspaceSurfaceSlotOptionsContext
  ) => PresentationDocumentWorkspaceSurfaceSlotRenderOptions
}

/** Props for status or empty-state feedback blocks. */
export interface PresentationStatusBlockProps {
  readonly className?: string
  readonly label: ReactNode
  readonly tone?: 'default' | 'error'
}

/** Feedback component group for status and error messaging. */
export interface PresentationFeedbackComponentSystemComponents {
  readonly StatusBlock: (props: PresentationStatusBlockProps) => ReactNode
}

/** Props for a single metric projected from view chrome or record data. */
export interface PresentationMetricItemProps {
  readonly className?: string
  readonly icon?: ReactNode
  readonly id: string
  readonly label: ReactNode
  readonly value: ReactNode
  readonly variant?: 'number' | 'text'
}

/** Props for a container of projected metric items. */
export interface PresentationMetricStripProps {
  readonly children?: ReactNode
  readonly className?: string
}

/** Metric component group for compact key-value summaries. */
export interface PresentationMetricComponentSystemComponents {
  readonly MetricItem: (props: PresentationMetricItemProps) => ReactNode
  readonly MetricStrip: (props: PresentationMetricStripProps) => ReactNode
}

/** Semantic color tones available to field value renderers. */
export type PresentationFieldValueTone =
  'amber' | 'red' | 'sky' | 'slate' | 'teal' | 'violet'

/** Props for rendering code-like field values. */
export interface PresentationFieldValueCodeProps {
  readonly className?: string
  readonly value: string
}

/** Props for rendering compound field values with badges and supporting lines. */
export interface PresentationFieldValueCompositeProps {
  readonly className?: string
  readonly inlineBadges?: readonly ReactNode[]
  readonly primaryValue?: ReactNode
  readonly supportingValues?: readonly ReactNode[]
}

/** Props for rendering missing or empty field values. */
export interface PresentationFieldValueEmptyProps {
  readonly className?: string
  readonly label: ReactNode
}

/** Props for rendering formatted JSON field values. */
export interface PresentationFieldValueJsonProps {
  readonly className?: string
  readonly formattedValue: string
  readonly tone?: PresentationFieldValueTone
}

/** Props for rendering scalar field values. */
export interface PresentationFieldValueScalarProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly title?: string
  readonly tone?: PresentationFieldValueTone
}

/** Props for secondary field value lines. */
export interface PresentationFieldValueSupportingValueProps {
  readonly children?: ReactNode
  readonly className?: string
}

/** Field value component group for scalar, JSON, code, empty, and composite values. */
export interface PresentationFieldValueComponentSystemComponents {
  readonly FieldValueCode: (props: PresentationFieldValueCodeProps) => ReactNode
  readonly FieldValueComposite: (
    props: PresentationFieldValueCompositeProps
  ) => ReactNode
  readonly FieldValueEmpty: (props: PresentationFieldValueEmptyProps) => ReactNode
  readonly FieldValueJson: (props: PresentationFieldValueJsonProps) => ReactNode
  readonly FieldValueScalar: (props: PresentationFieldValueScalarProps) => ReactNode
  readonly FieldValueSupportingValue: (
    props: PresentationFieldValueSupportingValueProps
  ) => ReactNode
}

/** Props for a record detail container projected from presentation record semantics. */
export interface PresentationRecordDetailsProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly viewId?: string | null
}

/** Props for empty-state content in record detail views. */
export interface PresentationRecordDetailEmptyStateProps {
  readonly className?: string
  readonly label: ReactNode
  readonly viewId?: string | null
}

/** Props for one field row in a record detail view. */
export interface PresentationRecordDetailFieldProps {
  readonly actions?: ReactNode
  readonly className?: string
  readonly fieldId: string
  readonly hideLabel?: boolean
  readonly label: ReactNode
  readonly value: ReactNode
}

/** Record component group for detail tables and field rows. */
export interface PresentationRecordComponentSystemComponents {
  readonly RecordDetailEmptyState: (
    props: PresentationRecordDetailEmptyStateProps
  ) => ReactNode
  readonly RecordDetailField: (props: PresentationRecordDetailFieldProps) => ReactNode
  readonly RecordDetails: (props: PresentationRecordDetailsProps) => ReactNode
}

/** Form component group for projected input forms and their controls. */
export interface PresentationFormComponentSystemComponents<
  TDateTimeFilterValue = unknown,
> {
  readonly CheckboxControl: (props: PresentationCheckboxControlProps) => ReactNode
  readonly ChoiceToggleGroup: (props: PresentationChoiceToggleGroupProps) => ReactNode
  readonly ChoiceToggleItem: (props: PresentationChoiceToggleItemProps) => ReactNode
  readonly DateTimeFilterControl: (
    props: PresentationDateTimeFilterControlProps<TDateTimeFilterValue>
  ) => ReactNode
  readonly FormActionButton: (props: PresentationActionButtonProps) => ReactNode
  readonly FormFieldLabel: (props: PresentationFormFieldLabelProps) => ReactNode
  readonly InputForm: (props: PresentationInputFormProps) => ReactNode
  readonly InputFormActionRow: (props: PresentationInputFormActionRowProps) => ReactNode
  readonly InputFormControlGroup: (
    props: PresentationInputFormControlGroupProps
  ) => ReactNode
  readonly InputFormControlSlot: (props: PresentationInputFormControlSlotProps) => ReactNode
  readonly InputFormField: (props: PresentationInputFormFieldProps) => ReactNode
  readonly InputFormFieldMessage: (
    props: PresentationInputFormFieldMessageProps
  ) => ReactNode
  readonly InputFormGroup: (props: PresentationInputFormGroupProps) => ReactNode
  readonly InputFormGroups: (props: PresentationInputFormGroupsProps) => ReactNode
  readonly SelectControl: (props: PresentationSelectControlProps) => ReactNode
  readonly TextInputControl: (props: PresentationTextInputControlProps) => ReactNode
}

/** Props for the semantic tabs layout projected from view regions. */
export interface PresentationTabsLayoutProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly onValueChange: (value: string) => void
  readonly value: string
  readonly viewId?: string | null
}

/** Props for the list container of projected tabs. */
export interface PresentationTabsListProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly viewId?: string | null
}

/** Props for a tab panel bound to a projected view region. */
export interface PresentationTabsPanelProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly region: ViewRegionDefinition
  readonly value: string
  readonly viewId?: string | null
}

/** Props for a tab trigger bound to a projected view region. */
export interface PresentationTabsTriggerProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly isActive?: boolean
  readonly region: ViewRegionDefinition
  readonly value: string
  readonly viewId?: string | null
}

/** Tabs component group for rendering view regions as controlled tabs. */
export interface PresentationTabsComponentSystemComponents {
  readonly TabsLayout: (props: PresentationTabsLayoutProps) => ReactNode
  readonly TabsList: (props: PresentationTabsListProps) => ReactNode
  readonly TabsPanel: (props: PresentationTabsPanelProps) => ReactNode
  readonly TabsTrigger: (props: PresentationTabsTriggerProps) => ReactNode
}

/** Props for modal prompt shells projected from presentation flows. */
export interface PresentationPromptModalProps {
  readonly ariaLabelledBy: string
  readonly children?: ReactNode
  readonly className?: string
  readonly description?: ReactNode
  readonly footer?: ReactNode
  readonly headerActions?: ReactNode
  readonly onBackdropMouseDown?: MouseEventHandler<HTMLDivElement>
  readonly role?: string
  readonly title: ReactNode
  readonly titleId: string
}

/** Props for prompt header actions and close affordances. */
export interface PresentationPromptHeaderActionsProps {
  readonly actions?: ReactNode
  readonly className?: string
  readonly closeButton?: ReactNode
  readonly viewId?: string | null
}

/** Props for prompt body content. */
export interface PresentationPromptContentProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly viewId?: string | null
}

/** Props for prompt footer content. */
export interface PresentationPromptFooterProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly viewId?: string | null
}

/** Props for a projected prompt region. */
export interface PresentationPromptRegionProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly region: ViewRegionDefinition
  readonly viewId?: string | null
}

/** Prompt component group for modal flow and prompt rendering. */
export interface PresentationPromptComponentSystemComponents {
  readonly PromptContent: (props: PresentationPromptContentProps) => ReactNode
  readonly PromptFooter: (props: PresentationPromptFooterProps) => ReactNode
  readonly PromptHeaderActions: (props: PresentationPromptHeaderActionsProps) => ReactNode
  readonly PromptModal: (props: PresentationPromptModalProps) => ReactNode
  readonly PromptRegion: (props: PresentationPromptRegionProps) => ReactNode
}

/** Props for notices describing process tasks and available actions. */
export interface PresentationProcessTaskNoticeProps {
  readonly actions?: ReactNode
  readonly className?: string
  readonly description?: ReactNode
  readonly icon?: ReactNode
  readonly title: ReactNode
}

/** Process component group for workflow/task presentation elements. */
export interface PresentationProcessComponentSystemComponents {
  readonly ProcessTaskNotice: (props: PresentationProcessTaskNoticeProps) => ReactNode
}

/** Props for a projected view chrome action slot. */
export interface PresentationViewChromeActionSlotProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly slotId?: string
  readonly viewId?: string | null
}

/** Props for badge strips projected into view chrome. */
export interface PresentationViewChromeBadgeStripProps {
  readonly badges: readonly ReactNode[]
  readonly className?: string
  readonly slotId?: string
  readonly viewId?: string | null
}

/** Props for metric strips projected into view chrome. */
export interface PresentationViewChromeMetricStripProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly message?: ReactNode
  readonly slotId?: string | null
  readonly viewId?: string | null
}

/** Props for an arbitrary projected view chrome slot. */
export interface PresentationViewChromeSlotProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly placement?: string | number | null
  readonly slot: ViewChromeSlotDefinition
  readonly viewId?: string | null
}

/** Item contract for layout and view switches rendered in chrome. */
export interface PresentationViewChromeSwitchItem {
  readonly icon?: ReactNode
  readonly id: string
  readonly isActive: boolean
  readonly label: ReactNode
  readonly onSelect: () => void
}

/** Props for controlled switch groups in view chrome. */
export interface PresentationViewChromeSwitchProps {
  readonly ariaLabel: string
  readonly className?: string
  readonly items: readonly PresentationViewChromeSwitchItem[]
  readonly slotId?: string
  readonly viewId?: string | null
}

/** View chrome component group for actions, badges, metrics, slots, and switches. */
export interface PresentationViewChromeComponentSystemComponents {
  readonly ActionSlot: (props: PresentationViewChromeActionSlotProps) => ReactNode
  readonly BadgeStrip: (props: PresentationViewChromeBadgeStripProps) => ReactNode
  readonly LayoutSwitch: (props: PresentationViewChromeSwitchProps) => ReactNode
  readonly MetricStripSlot: (props: PresentationViewChromeMetricStripProps) => ReactNode
  readonly ViewChromeSlot: (props: PresentationViewChromeSlotProps) => ReactNode
  readonly ViewSwitch: (props: PresentationViewChromeSwitchProps) => ReactNode
}

/** Props required from the button primitive used by view chrome switches. */
export interface PresentationViewChromeSwitchButtonProps {
  readonly 'aria-pressed'?: boolean
  readonly children?: ReactNode
  readonly onClick?: () => void
  readonly size?: 'sm'
  readonly type?: 'button'
  readonly variant?: 'ghost' | 'secondary'
}

/** Primitive dependency required to build view chrome components. */
export interface CreateShadcnViewChromeComponentsOptions {
  readonly Button: ComponentType<PresentationViewChromeSwitchButtonProps>
}

/** Creates the action component group from a compatible button primitive. */
export function createShadcnActionComponents({
  Button,
}: CreateShadcnActionComponentsOptions):
  PresentationActionComponentSystemComponents {
  return {
    ActionButton: (props) => createElement(Button, props),
  }
}

/** Creates the badge component group from a compatible badge primitive. */
export function createShadcnBadgeComponents({
  Badge,
}: CreateShadcnBadgeComponentsOptions):
  PresentationBadgeComponentSystemComponents {
  return {
    Badge: (props) => createElement(Badge, props),
  }
}

/** Creates navigation components that style router links as action-like buttons. */
export function createShadcnNavigationComponents({
  buttonClassName,
  Link,
}: CreateShadcnNavigationComponentsOptions):
  PresentationNavigationComponentSystemComponents {
  return {
    NavigationLink: ({ children, className, isActive = false, to }) =>
      createElement(
        Link,
        {
          className: cn(
            buttonClassName({
              size: 'sm',
              variant: isActive ? 'secondary' : 'ghost',
            }),
            className,
          ),
          to,
        },
        children,
      ),
  }
}

/** Creates feedback components with default shadcn/Tailwind styling. */
export function createShadcnFeedbackComponents():
  PresentationFeedbackComponentSystemComponents {
  return {
    StatusBlock: ({ className, label, tone = 'default' }) =>
      createElement(
        'div',
        {
          className: cn(
            tone === 'error'
              ? 'rounded-2xl border border-red-300/60 bg-red-50 px-4 py-3 text-sm text-red-800'
              : 'rounded-2xl border border-slate-950/8 bg-white/65 px-4 py-3 text-sm text-slate-500',
            className,
          ),
        },
        label,
      ),
  }
}

/** Creates metric components for compact semantic summaries. */
export function createShadcnMetricComponents():
  PresentationMetricComponentSystemComponents {
  return {
    MetricItem: ({ className, icon, id, label, value, variant }) =>
      createElement(
        'div',
        {
          className: cn(
            'grid min-w-0 gap-1 rounded-md border border-slate-950/8 bg-white/70 px-3 py-2',
            className,
          ),
          ...createPresentationTestAttributes({ fieldId: id }),
        },
        createElement(
          'dt',
          {
            className:
              'flex min-w-0 items-center gap-1.5 text-[0.68rem] font-medium uppercase tracking-[0.12em] text-slate-500',
          },
          icon,
          label,
        ),
        createElement(
          'dd',
          {
            className: variant === 'text'
              ? 'break-all text-sm font-medium text-slate-950'
              : 'text-lg font-semibold leading-6 text-slate-950',
          },
          value,
        ),
      ),
    MetricStrip: ({ children, className }) =>
      createElement(
        'dl',
        {
          className: cn(
            'grid gap-2 sm:grid-cols-[repeat(auto-fit,minmax(7.5rem,1fr))]',
            className,
          ),
        },
        children,
      ),
  }
}

/** Creates form components from shadcn-compatible form primitives. */
export function createShadcnFormComponents<TDateTimeFilterValue = unknown>({
  Button,
  DateTimeFilter,
  Input,
  Label,
  ToggleGroup,
  ToggleGroupItem,
}: CreateShadcnFormComponentsOptions<TDateTimeFilterValue>):
  PresentationFormComponentSystemComponents<TDateTimeFilterValue> {
  return {
    CheckboxControl: ({ className, onCheckedChange, ...props }) =>
      createElement('input', {
        ...props,
        className: cn('size-4 rounded border border-input', className),
        onChange: (event: ChangeEvent<HTMLInputElement>) =>
          onCheckedChange(event.target.checked),
        type: 'checkbox',
      }),
    ChoiceToggleGroup: ({ children, className, value, ...props }) =>
      createElement(
        ToggleGroup,
        {
          ...props,
          className: cn('flex flex-wrap justify-start gap-1.5', className),
          type: 'multiple',
          value: value ? [...value] : [],
        },
        children,
      ),
    ChoiceToggleItem: (props) => createElement(ToggleGroupItem, props),
    DateTimeFilterControl: ({ onValueChange, ...props }) =>
      createElement(DateTimeFilter, {
        ...props,
        onChange: onValueChange,
      }),
    FormActionButton: (props) => createElement(Button, props),
    FormFieldLabel: ({ children, className }) =>
      createElement(
        Label,
        { className: cn('text-xs font-medium text-slate-500', className) },
        children,
      ),
    InputForm: ({ children, className, form, viewId }) =>
      createElement(
        'div',
        {
          className,
          ...createPresentationTestAttributes({ formId: form.Id, viewId }),
        },
        children,
      ),
    InputFormActionRow: ({ children, className, form }) =>
      createElement(
        'div',
        {
          className,
          ...createPresentationTestAttributes({ formId: form.Id }),
        },
        children,
      ),
    InputFormControlGroup: ({ children, className, field }) =>
      createElement(
        'div',
        {
          className,
          ...createPresentationTestAttributes({ fieldId: field.FieldId ?? field.Id }),
        },
        children,
      ),
    InputFormControlSlot: ({ children, className, field }) =>
      createElement(
        'div',
        {
          className,
          ...createPresentationTestAttributes({ fieldId: field.FieldId ?? field.Id }),
        },
        children,
      ),
    InputFormField: ({ className, control, field, label, message }) =>
      createElement(
        'div',
        {
          className,
          'data-field-id': field.Id,
          ...createPresentationTestAttributes({ fieldId: field.FieldId ?? field.Id }),
        },
        label,
        control,
        message,
      ),
    InputFormFieldMessage: ({ children, className, field, tone = 'default' }) =>
      createElement(
        'div',
        {
          className: cn(
            tone === 'error'
              ? 'text-xs text-red-700'
              : tone === 'warning'
                ? 'text-xs text-amber-700'
                : 'text-xs text-slate-500',
            className,
          ),
          ...createPresentationTestAttributes({ fieldId: field.FieldId ?? field.Id }),
        },
        children,
      ),
    InputFormGroup: ({ children, className, group }) =>
      createElement(
        'div',
        {
          className,
          'data-presentation-form-group-id': group.Id,
        },
        children,
      ),
    InputFormGroups: ({ children, className, form }) =>
      createElement(
        'div',
        {
          className,
          ...createPresentationTestAttributes({ formId: form.Id }),
        },
        children,
      ),
    SelectControl: ({ className, onValueChange, options, ...props }) =>
      createElement(
        'select',
        {
          ...props,
          className: cn(
            'h-8 min-w-0 rounded-lg border border-input bg-transparent px-2.5 py-1 text-sm outline-none transition-colors focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:pointer-events-none disabled:cursor-not-allowed disabled:bg-input/50 disabled:opacity-50',
            className,
          ),
          onChange: (event: ChangeEvent<HTMLSelectElement>) =>
            onValueChange(event.target.value),
        },
        options.map((option) =>
          createElement('option', { key: option.value, value: option.value }, option.label),
        ),
      ),
    TextInputControl: ({ onValueChange, ...props }) =>
      createElement(Input, {
        ...props,
        onChange: (event: ChangeEvent<HTMLInputElement>) =>
          onValueChange(event.target.value),
      }),
  }
}

/** Creates collection chrome components for query, summary, body, detail, pagination, and selection slots. */
export function createShadcnCollectionChromeComponents({
  Button,
}: CreateShadcnCollectionChromeComponentsOptions):
  PresentationCollectionChromeComponentSystemComponents {
  return {
    CollectionBodySlot: ({ children, className, slotId, viewId }) =>
      createElement(
        'div',
        {
          className: cn('w-full min-w-0', className),
          ...createPresentationTestAttributes({ collectionSlotId: slotId, viewId }),
        },
        children,
      ),
    CollectionDetailSlot: ({ children, className, slotId, viewId }) =>
      createElement(
        'div',
        {
          className,
          ...createPresentationTestAttributes({ collectionSlotId: slotId, viewId }),
        },
        children,
      ),
    CollectionPaginationBar: ({
      canGoNextPage,
      canGoPreviousPage,
      className,
      firstIcon,
      isFetching,
      loadingIcon,
      nextIcon,
      onFirstPage,
      onNextPage,
      onPreviousPage,
      pageLabel,
      pageSizeLabel,
      previousIcon,
      shownLabel,
      slotId,
      totalLabel,
      viewId,
    }) =>
      createElement(
        'div',
        {
          className: cn(
            'flex flex-wrap items-center justify-between gap-2 text-xs text-slate-600',
            className,
          ),
          ...createPresentationTestAttributes({ collectionSlotId: slotId, viewId }),
        },
        createElement(
          'div',
          { className: 'flex min-w-0 items-center gap-2' },
          isFetching ? loadingIcon : null,
          createElement('span', { className: 'font-medium text-slate-800' }, pageLabel),
          createElement('span', null, shownLabel),
          createElement('span', null, pageSizeLabel),
          createElement('span', null, totalLabel),
        ),
        createElement(
          'div',
          { className: 'flex items-center gap-1' },
          createElement(
            Button,
            {
              disabled: isFetching || !canGoPreviousPage,
              onClick: onFirstPage,
              size: 'sm',
              type: 'button',
              variant: 'outline',
            },
            firstIcon,
            'First',
          ),
          createElement(
            Button,
            {
              disabled: isFetching || !canGoPreviousPage,
              onClick: onPreviousPage,
              size: 'sm',
              type: 'button',
              variant: 'outline',
            },
            previousIcon,
            'Previous',
          ),
          createElement(
            Button,
            {
              disabled: isFetching || !canGoNextPage,
              onClick: onNextPage,
              size: 'sm',
              type: 'button',
              variant: 'outline',
            },
            'Next',
            nextIcon,
          ),
        ),
      ),
    CollectionQueryFormSlot: ({ children, className, slotId, viewId }) =>
      createElement(
        'div',
        {
          className: cn('w-full min-w-0', className),
          ...createPresentationTestAttributes({ collectionSlotId: slotId, viewId }),
        },
        children,
      ),
    CollectionRowActions: ({ children, className, slotId }) =>
      createElement(
        'div',
        {
          className: cn('flex justify-end gap-1', className),
          ...createPresentationTestAttributes({ collectionSlotId: slotId }),
        },
        children,
      ),
    CollectionSelectionActionToolbar: ({
      actions,
      className,
      selectedLabel,
      slotId,
    }) =>
      createElement(
        'div',
        {
          className: cn(
            'flex flex-wrap items-center justify-between gap-2 rounded-lg border border-slate-950/8 bg-slate-50/80 px-3 py-2',
            className,
          ),
          ...createPresentationTestAttributes({ collectionSlotId: slotId }),
        },
        createElement('span', { className: 'text-sm font-medium text-slate-600' }, selectedLabel),
        createElement('div', { className: 'flex flex-wrap items-center gap-2' }, actions),
      ),
    CollectionSummarySlot: ({ children, className, slotId, viewId }) =>
      createElement(
        'div',
        {
          className: cn('w-full min-w-0', className),
          ...createPresentationTestAttributes({ collectionSlotId: slotId, viewId }),
        },
        children,
      ),
  }
}

/** Creates collection components around an injected data table and detail layout. */
export function createShadcnCollectionComponents<
  TDataTableRenderer extends PresentationDataTableRenderer =
    PresentationDataTableRenderer,
>({
  CollectionDetailLayout,
  DataTable,
  rowActionMenuTriggerClassName,
}: CreateShadcnCollectionComponentsOptions<TDataTableRenderer>):
  PresentationCollectionComponentSystemComponents<TDataTableRenderer> {
  return {
    CollectionDetailLayout,
    DataTable,
    RowActionMenu: ({ children, trigger }) =>
      createElement(
        'div',
        { className: 'flex justify-end' },
        createElement(
          'details',
          {
            className: 'group relative inline-flex justify-end',
            'data-row-activation-skip': true,
            onClick: (event) => {
              event.stopPropagation()
            },
          },
          trigger,
          createElement(
            'div',
            {
              className:
                'absolute right-full top-1/2 z-40 mr-1 grid min-w-44 -translate-y-1/2 gap-1 rounded-lg border border-slate-950/10 bg-white p-1 text-left shadow-xl',
            },
            children,
          ),
        ),
      ),
    RowActionMenuItem: ({
      actionId,
      children,
      collectionSlotId,
      disabled,
      onClick,
      viewId,
    }) =>
      createElement(
        'button',
        {
          className:
            'flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm text-slate-700 outline-none hover:bg-slate-100 focus-visible:bg-slate-100 disabled:pointer-events-none disabled:opacity-50',
          disabled,
          onClick,
          type: 'button',
          ...createPresentationTestAttributes({
            actionId,
            collectionSlotId,
            viewId,
          }),
        },
        children,
      ),
    RowActionMenuTrigger: ({ children, ...props }) =>
      createElement(
        'summary',
        {
          ...props,
          className: cn(
            rowActionMenuTriggerClassName({
              size: 'icon-sm',
              variant: 'ghost',
            }),
            'cursor-pointer list-none [&::-webkit-details-marker]:hidden',
          ),
        },
        children,
      ),
  }
}

/** Creates surface components that compose projected view content with chrome. */
export function createShadcnSurfaceComponents({
  Surface,
}: CreateShadcnSurfaceComponentsOptions):
  PresentationSurfaceComponentSystemComponents {
  return {
    ViewSurface: (props) => createElement(Surface, props),
    ViewSurfaceChromePlacement: ({ children, className, placement, viewId }) =>
      createElement(
        'div',
        {
          className,
          'data-presentation-view-chrome-placement': String(placement),
          ...createPresentationTestAttributes({ viewId }),
        },
        children,
      ),
    ViewSurfaceContent: ({
      afterContentChrome,
      beforeContentChrome,
      children,
      footerChrome,
    }) => {
      if (
        !isRenderableComponentNode(beforeContentChrome) &&
        !isRenderableComponentNode(afterContentChrome) &&
        !isRenderableComponentNode(footerChrome)
      ) {
        return children
      }

      return createElement(
        Fragment,
        null,
        beforeContentChrome,
        children,
        afterContentChrome,
        footerChrome,
      )
    },
    ViewSurfaceHeaderActions: ({ action, chrome, className }) => {
      if (!isRenderableComponentNode(action)) {
        return chrome
      }

      if (!isRenderableComponentNode(chrome)) {
        return action
      }

      return createElement(
        'div',
        {
          className: cn(
            'flex flex-wrap items-center justify-end gap-2',
            className,
          ),
        },
        action,
        chrome,
      )
    },
  }
}

/** Creates document workspace components from tree, layout, badge, and JSON primitives. */
export function createShadcnDocumentWorkspaceComponents<
  TTreeControlsProps extends object = object,
  TJsonDocumentDiffProps extends object = object,
  TJsonDocumentEditorProps extends object = object,
>({
  Badge,
  DocumentWorkspaceTreeControls,
  DocumentWorkspaceTreeItem,
  DocumentWorkspaceTreeLayout,
  DocumentWorkspaceTreeView,
  JsonDocumentDiff,
  JsonDocumentEditor,
  resolveSurfaceSlotOptions,
}: CreateShadcnDocumentWorkspaceComponentsOptions<
  TTreeControlsProps,
  TJsonDocumentDiffProps,
  TJsonDocumentEditorProps
>):
  PresentationDocumentWorkspaceComponentSystemComponents<
    TTreeControlsProps,
    TJsonDocumentDiffProps,
    TJsonDocumentEditorProps
  > {
  return {
    DocumentWorkspaceDetailPanel: ({
      children,
      className,
      contentClassName,
      emptyClassName,
      emptyLabel,
      headerClassName,
      headerContentClassName,
      icon,
      subtitle,
      subtitleClassName,
      title,
      titleClassName,
    }) => {
      if (!title) {
        return createElement('div', { className: emptyClassName }, emptyLabel)
      }

      return createElement(
        'aside',
        { className },
        createElement(
          'div',
          { className: headerClassName },
          createElement(
            'div',
            { className: headerContentClassName },
            icon,
            createElement(
              'div',
              { className: 'min-w-0' },
              createElement('p', { className: titleClassName }, title),
              subtitle
                ? createElement('p', { className: subtitleClassName }, subtitle)
                : null,
            ),
          ),
        ),
        createElement('div', { className: contentClassName }, children),
      )
    },
    DocumentWorkspaceLayoutGroup: ({
      children,
      className,
      layoutNodeId,
      style,
      workspaceViewId,
    }) =>
      createElement(
        'div',
        {
          className,
          'data-presentation-layout-node-id': layoutNodeId ?? undefined,
          style,
          ...createPresentationTestAttributes({ viewId: workspaceViewId }),
        },
        children,
      ),
    DocumentWorkspaceLayoutPane: ({
      children,
      className,
      contentClassName,
      layoutNodeId,
      title,
      titleClassName,
      viewId,
      workspaceViewId,
    }) =>
      createElement(
        'section',
        {
          className: cn('flex h-full min-h-0 flex-col gap-2', className),
          'data-presentation-layout-node-id': layoutNodeId ?? undefined,
          ...createPresentationTestAttributes({ viewId: viewId ?? workspaceViewId }),
        },
        createElement(
          'h3',
          {
            className: cn(
              'text-xs font-semibold uppercase tracking-[0.12em] text-slate-500',
              titleClassName,
            ),
          },
          title,
        ),
        createElement(
          'div',
          { className: cn('min-h-0 flex-1 [&>*]:!h-full [&>*]:min-h-170', contentClassName) },
          children,
        ),
      ),
    DocumentWorkspaceNodeLabel: ({
      badges = [],
      className,
      icon,
      label,
      labelClassName,
    }) =>
      createElement(
        'span',
        { className },
        icon,
        createElement('span', { className: labelClassName }, label),
        badges.map((badge) =>
          createElement(
            Badge,
            {
              className: badge.className,
              key: `${badge.label}:${badge.className ?? ''}`,
              variant: 'outline',
            },
            badge.label,
          ),
        ),
      ),
    DocumentWorkspaceShell: ({
      children,
      className,
      contentClassName,
      viewId,
    }) =>
      createElement(
        'div',
        {
          className,
          ...createPresentationTestAttributes({ viewId }),
        },
        createElement('main', { className: contentClassName }, children),
      ),
    DocumentWorkspaceSurfaceSlot: ({
      children,
      renderSurface,
      role,
      slot,
      viewId,
    }) =>
      createElement(
        'div',
        {
          'data-presentation-document-workspace-slot-id': slot,
          ...createPresentationTestAttributes({ viewId }),
        },
        renderSurface
          ? renderSurface(resolveSurfaceSlotOptions({ role, slot }))
          : children,
      ),
    DocumentWorkspaceTreeControls,
    DocumentWorkspaceTreeItem,
    DocumentWorkspaceStatus: ({ children, className }) =>
      createElement('div', { className }, children),
    DocumentWorkspaceTable: ({
      details,
      labelCellClassName,
      rowClassName,
      tableClassName,
      valueCellClassName,
      valueTextClassName,
    }) =>
      createElement(
        'table',
        { className: tableClassName },
        createElement(
          'tbody',
          null,
          details.map((detail) =>
            createElement(
              'tr',
              { className: rowClassName, key: detail.label },
              createElement('th', { className: labelCellClassName }, detail.label),
              createElement(
                'td',
                { className: valueCellClassName },
                createElement('span', { className: valueTextClassName }, detail.value),
              ),
            ),
          ),
        ),
      ),
    DocumentWorkspaceTreeLayout,
    DocumentWorkspaceTreeView,
    JsonDocumentDiff,
    JsonDocumentEditor,
  }
}

/** Creates a split tree/detail layout adapter for document workspaces. */
export function createShadcnDocumentWorkspaceTreeLayout({
  Group,
  Panel,
  Separator,
}: CreateShadcnDocumentWorkspaceTreeLayoutOptions) {
  return ({
    className,
    detail,
    detailDefaultSize = '42%',
    detailId,
    detailMinSize = '26%',
    splitterClassName,
    splitterHandleClassName,
    tree,
    treeDefaultSize = '58%',
    treeId,
    treeMinSize = '28%',
  }: PresentationDocumentWorkspaceTreeLayoutProps) =>
    createElement(
      Group,
      { className, orientation: 'horizontal' },
      createElement(
        Panel,
        { defaultSize: treeDefaultSize, id: treeId, minSize: treeMinSize },
        tree,
      ),
      createElement(
        Separator,
        { className: splitterClassName },
        createElement('div', { className: splitterHandleClassName }),
      ),
      createElement(
        Panel,
        { defaultSize: detailDefaultSize, id: detailId, minSize: detailMinSize },
        detail,
      ),
    )
}

/** Creates a document workspace tree item adapter. */
export function createShadcnDocumentWorkspaceTreeItem({
  TreeItem,
}: CreateShadcnDocumentWorkspaceTreeItemOptions) {
  return ({ children, itemId, label }: PresentationDocumentWorkspaceTreeItemProps) =>
    createElement(TreeItem, { itemId, key: itemId, label }, children)
}

/** Creates a controlled document workspace tree view adapter. */
export function createShadcnDocumentWorkspaceTreeView<TSx = unknown>({
  sx,
  TreeView,
}: CreateShadcnDocumentWorkspaceTreeViewOptions<TSx>) {
  return ({
    ariaLabel,
    children,
    expandedItemIds,
    onExpandedItemIdsChange,
    onSelectedItemIdChange,
    selectedItemId,
  }: PresentationDocumentWorkspaceTreeViewProps) =>
    createElement(
      TreeView,
      {
        'aria-label': ariaLabel,
        expandedItems: [...expandedItemIds],
        onExpandedItemsChange: (_event, itemIds) =>
          onExpandedItemIdsChange(itemIds),
        onSelectedItemsChange: (_event, itemId) =>
          onSelectedItemIdChange(Array.isArray(itemId) ? itemId[0] ?? null : itemId),
        selectedItems: selectedItemId,
        sx,
      },
      children,
    )
}

/** Wraps a JSON diff component so it fits the document workspace component contract. */
export function createShadcnJsonDocumentDiff<TProps extends object>({
  JsonDocumentDiff,
}: CreateShadcnJsonDocumentDiffOptions<TProps>) {
  return (props: TProps) =>
    createElement(JsonDocumentDiff as ComponentType<TProps>, props)
}

/** Wraps a lazy JSON editor component in Suspense with a configurable fallback. */
export function createShadcnJsonDocumentEditor<TProps extends object>({
  fallbackClassName,
  fallbackLabel = 'Loading editor...',
  JsonDocumentEditor,
}: CreateShadcnJsonDocumentEditorOptions<TProps>) {
  return (props: TProps) =>
    createElement(
      Suspense,
      {
        fallback: createElement(
          'div',
          { className: fallbackClassName(props) },
          fallbackLabel,
        ),
      },
      createElement(JsonDocumentEditor as ComponentType<TProps>, props),
    )
}

/** Creates field value components for record and collection projections. */
export function createShadcnFieldValueComponents():
  PresentationFieldValueComponentSystemComponents {
  return {
    FieldValueCode: ({ className, value }) =>
      createElement(
        'code',
        {
          className: cn(
            'break-all rounded-md bg-slate-100 px-2 py-1 text-xs text-slate-700',
            className,
          ),
        },
        value,
      ),
    FieldValueComposite: ({
      className,
      inlineBadges = [],
      primaryValue,
      supportingValues = [],
    }) =>
      createElement(
        'div',
        { className: cn('grid gap-1', className) },
        primaryValue || inlineBadges.length > 0
          ? createElement(
              'div',
              { className: 'flex flex-wrap items-center gap-1.5' },
              primaryValue
                ? createElement(
                    'span',
                    { className: 'font-medium text-slate-950' },
                    primaryValue,
                  )
                : null,
              inlineBadges,
            )
          : null,
        supportingValues,
      ),
    FieldValueEmpty: ({ className, label }) =>
      createElement('span', { className: cn('text-slate-400', className) }, label),
    FieldValueJson: ({ className, formattedValue, tone }) =>
      createElement(
        'pre',
        {
          className: cn(
            tone === 'red'
              ? 'max-h-96 overflow-auto rounded-lg border border-red-300/60 bg-red-50/40 p-3 text-xs leading-5 text-red-950'
              : 'max-h-96 overflow-auto rounded-lg border border-slate-950/8 bg-white/72 p-3 text-xs leading-5 text-slate-800',
            className,
          ),
        },
        createElement('code', null, formattedValue),
      ),
    FieldValueScalar: ({ children, className, title, tone }) =>
      createElement(
        'span',
        {
          className: cn(
            tone === 'red' ? 'wrap-break-word text-red-700' : 'wrap-break-word',
            className,
          ),
          title,
        },
        children,
      ),
    FieldValueSupportingValue: ({ children, className }) =>
      createElement(
        'div',
        { className: cn('break-all text-xs text-slate-500', className) },
        children,
      ),
  }
}

/** Creates record detail components for projected field/value tables. */
export function createShadcnRecordComponents():
  PresentationRecordComponentSystemComponents {
  return {
    RecordDetailEmptyState: ({ className, label, viewId }) =>
      createElement(
        'div',
        {
          className: cn(
            'rounded-lg border border-slate-950/8 bg-white/65 px-4 py-3 text-sm text-slate-500',
            className,
          ),
          ...createPresentationTestAttributes({ viewId }),
        },
        label,
      ),
    RecordDetailField: ({ actions, className, fieldId, hideLabel, label, value }) =>
      createElement(
        'tr',
        {
          className: cn('border-b border-slate-950/6 last:border-b-0', className),
          'data-field-id': fieldId,
          ...createPresentationTestAttributes({ fieldId }),
        },
        hideLabel
          ? null
          : createElement(
              'th',
              {
                className:
                  'w-44 bg-slate-50 px-3 py-2 text-left text-xs font-semibold uppercase tracking-[0.14em] text-slate-500',
              },
              label,
            ),
        createElement(
          'td',
          { className: 'break-all px-3 py-2 text-slate-800', colSpan: hideLabel ? 2 : undefined },
          actions
            ? createElement(
                'div',
                { className: 'flex min-w-0 items-start justify-between gap-2' },
                createElement('div', { className: 'min-w-0' }, value),
                actions,
              )
            : value,
        ),
      ),
    RecordDetails: ({ children, className, viewId }) =>
      createElement(
        'div',
        {
          className: cn(
            'overflow-hidden rounded-lg border border-slate-950/8 bg-white/72',
            className,
          ),
          ...createPresentationTestAttributes({ viewId }),
        },
        createElement(
          'table',
          { className: 'w-full text-sm' },
          createElement('tbody', null, children),
        ),
      ),
  }
}

/** Creates tabs components from shadcn-compatible tabs primitives. */
export function createShadcnTabsComponents({
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
}: CreateShadcnTabsComponentsOptions): PresentationTabsComponentSystemComponents {
  return {
    TabsLayout: ({ children, className, onValueChange, value, viewId }) =>
      createElement(
        Tabs,
        {
          className: cn('flex min-h-0 w-full min-w-0 flex-1 flex-col gap-4', className),
          onValueChange,
          value,
          ...createPresentationTestAttributes({ viewId }),
        },
        children,
      ),
    TabsList: ({ children, className, viewId }) =>
      createElement(
        TabsList,
        {
          className: cn(
            'w-fit rounded-xl border border-slate-950/8 bg-white/75',
            className,
          ),
          ...createPresentationTestAttributes({ viewId }),
        },
        children,
      ),
    TabsPanel: ({ children, className, region, value, viewId }) =>
      createElement(
        TabsContent,
        {
          className: cn(
            'mt-0 flex min-h-0 w-full min-w-0 flex-1 flex-col overflow-hidden data-[state=inactive]:hidden',
            className,
          ),
          'data-presentation-region-id': region.Id,
          forceMount: true,
          value,
          ...createPresentationTestAttributes({ viewId }),
        },
        children,
      ),
    TabsTrigger: ({ children, className, region, value, viewId }) =>
      createElement(
        TabsTrigger,
        {
          className,
          'data-presentation-region-id': region.Id,
          value,
          ...createPresentationTestAttributes({ viewId }),
        },
        children,
      ),
  }
}

/** Creates process components for projected workflow/task notices. */
export function createShadcnProcessComponents():
  PresentationProcessComponentSystemComponents {
  return {
    ProcessTaskNotice: ({ actions, className, description, icon, title }) =>
      createElement(
        'div',
        { className },
        createElement(
          'div',
          { className: 'flex flex-wrap items-center justify-between gap-3' },
          createElement(
            'div',
            { className: 'flex min-w-0 items-start gap-2' },
            icon,
            createElement(
              'div',
              { className: 'min-w-0' },
              createElement('p', { className: 'font-medium' }, title),
              description
                ? createElement(
                    'p',
                    { className: 'break-all text-xs opacity-75' },
                    description,
                  )
                : null,
            ),
          ),
          actions,
        ),
      ),
  }
}

/** Creates prompt components for modal presentation flows. */
export function createShadcnPromptComponents():
  PresentationPromptComponentSystemComponents {
  return {
    PromptContent: ({ children, className, viewId }) =>
      createElement(
        'div',
        {
          className: cn('grid gap-4', className),
          ...createPresentationTestAttributes({ viewId }),
        },
        children,
      ),
    PromptFooter: ({ children, className, viewId }) =>
      isRenderableComponentNode(children)
        ? createElement(
            'footer',
            {
              className: cn('mt-4 flex justify-end border-t border-slate-900/8 pt-4', className),
              ...createPresentationTestAttributes({ viewId }),
            },
            children,
          )
        : null,
    PromptHeaderActions: ({
      actions,
      className,
      closeButton,
      viewId,
    }) =>
      actions || closeButton
        ? createElement(
            'div',
            {
              className: cn('flex flex-wrap items-center justify-end gap-2', className),
              ...createPresentationTestAttributes({ viewId }),
            },
            actions,
            closeButton,
          )
        : null,
    PromptModal: ({
      ariaLabelledBy,
      children,
      className,
      description,
      footer,
      headerActions,
      onBackdropMouseDown,
      role,
      title,
      titleId,
    }) =>
      createElement(
        'div',
        {
          'aria-labelledby': ariaLabelledBy,
          'aria-modal': 'true',
          className:
            'fixed inset-0 z-50 flex items-center justify-center bg-slate-950/72 p-4 text-slate-950 backdrop-blur-sm',
          onMouseDown: onBackdropMouseDown,
          role: role ?? 'dialog',
        },
        createElement(
          'section',
          {
            className: cn(
              'flex max-h-full min-h-0 w-full max-w-3xl flex-col overflow-auto rounded-lg border border-white/20 bg-white p-4 shadow-2xl',
              className,
            ),
          },
          createElement(
            'header',
            {
              className:
                'mb-3 flex flex-wrap items-start justify-between gap-3 border-b border-slate-900/8 pb-3',
            },
            createElement(
              'div',
              { className: 'min-w-0' },
              createElement(
                'h2',
                {
                  className: 'text-sm font-semibold text-slate-950',
                  id: titleId,
                },
                title,
              ),
              description
                ? createElement('div', { className: 'mt-1 text-xs text-slate-500' }, description)
                : null,
            ),
            headerActions
              ? headerActions
              : null,
          ),
          children,
          footer,
        ),
      ),
    PromptRegion: ({
      children,
      className,
      region,
      viewId,
    }) =>
      isRenderableComponentNode(children)
        ? createElement(
            'div',
            {
              className: cn('h-full min-h-0', className),
              'data-region-id': region.Id,
              'data-presentation-region-id': region.Id,
              ...createPresentationTestAttributes({ viewId }),
            },
            children,
          )
        : null,
  }
}

/** Creates view chrome components for projected actions, badges, metrics, and switches. */
export function createShadcnViewChromeComponents({
  Button,
}: CreateShadcnViewChromeComponentsOptions):
  PresentationViewChromeComponentSystemComponents {
  return {
    ActionSlot: ({ children, className, slotId, viewId }) =>
      createElement(
        'div',
        {
          className,
          'data-presentation-view-chrome-slot-id': slotId,
          ...createPresentationTestAttributes({ viewId }),
        },
        children,
      ),
    BadgeStrip: ({ badges, className, slotId, viewId }) =>
      badges.length === 0
        ? null
        : createElement(
            'div',
            {
              className: cn('flex flex-wrap items-center gap-2', className),
              'data-presentation-view-chrome-slot-id': slotId,
              ...createPresentationTestAttributes({ viewId }),
            },
            badges,
          ),
    LayoutSwitch: (props) => renderShadcnViewChromeSwitch(props, Button),
    MetricStripSlot: ({
      children,
      className,
      message,
      slotId: _slotId,
      viewId,
    }) =>
      createElement(
        'div',
        {
          className: cn('grid gap-2', className),
          'data-presentation-view-chrome-slot-id': _slotId,
          ...createPresentationTestAttributes({ viewId }),
        },
        children,
        message,
      ),
    ViewChromeSlot: ({ children, className, slot, viewId }) =>
      createElement(
        'span',
        {
          className: cn('contents', className),
          'data-presentation-view-chrome-slot-id': slot.Id,
          ...createPresentationTestAttributes({ viewId }),
        },
        children,
      ),
    ViewSwitch: (props) => renderShadcnViewChromeSwitch(props, Button),
  }
}

function renderShadcnViewChromeSwitch(
  {
    ariaLabel,
    className,
    items,
    slotId,
    viewId,
  }: PresentationViewChromeSwitchProps,
  Button: ComponentType<PresentationViewChromeSwitchButtonProps>,
) {
  if (items.length === 0) {
    return null
  }

  return createElement(
    'div',
    {
      'aria-label': ariaLabel,
      className: cn(
        'flex items-center gap-1 rounded-lg border border-slate-950/8 bg-white/75 p-0.5',
        className,
      ),
      'data-presentation-view-chrome-slot-id': slotId,
      role: 'group',
      ...createPresentationTestAttributes({ viewId }),
    },
    items.map((item) =>
      createElement(
        Button,
        {
          'aria-pressed': item.isActive,
          key: item.id,
          onClick: item.onSelect,
          size: 'sm',
          type: 'button',
          variant: item.isActive ? 'secondary' : 'ghost',
        },
        item.icon,
        item.label,
      ),
    ),
  )
}

function isRenderableComponentNode(
  node: ReactNode,
): node is Exclude<ReactNode, null | undefined | false> {
  return node !== null && node !== undefined && node !== false
}

function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}
