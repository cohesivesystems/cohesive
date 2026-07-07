import type { ReactNode } from 'react'

import type {
  PresentationActionComponentSystemComponents,
  PresentationBadgeComponentSystemComponents,
  PresentationCollectionChromeComponentSystemComponents,
  PresentationCollectionComponentSystemComponents,
  PresentationDataTableRenderer,
  PresentationDocumentWorkspaceComponentSystemComponents,
  PresentationFeedbackComponentSystemComponents,
  PresentationFieldValueComponentSystemComponents,
  PresentationFormComponentSystemComponents,
  PresentationMetricComponentSystemComponents,
  PresentationNavigationComponentSystemComponents,
  PresentationProcessComponentSystemComponents,
  PresentationPromptComponentSystemComponents,
  PresentationRecordComponentSystemComponents,
  PresentationSurfaceComponentSystemComponents,
  PresentationTabsComponentSystemComponents,
  PresentationViewChromeComponentSystemComponents,
} from './presentation-component-groups'
import type {
  PresentationComponentSystem,
  PresentationComponentSystemRoleGroups,
} from './presentation-component-system'

/**
 * Loosest app-extension renderer contract for projected collection tables.
 *
 * The shared presentation runtime only treats the table as a component-system
 * role. Concrete apps can narrow the props to their local table implementation
 * while reusable renderers consume this package-level contract.
 */
export type PresentationShadcnDataTableRenderer = <TData extends object>(
  props: any & { readonly __data?: TData },
) => ReactNode

/**
 * Common shadcn-target component-system role set used by reusable presentation
 * renderers. App-specific extension points remain generic so products can bind
 * local table, tree-control, JSON-diff, and JSON-editor implementations without
 * leaking those app types into the framework runtime.
 */
export type PresentationShadcnComponentSystemRoleGroups<
  TDateTimeFilterValue = any,
  TDataTableRenderer extends PresentationDataTableRenderer =
    PresentationShadcnDataTableRenderer,
  TDocumentWorkspaceTreeControlsProps extends object = any,
  TJsonDocumentDiffProps extends object = any,
  TJsonDocumentEditorProps extends object = any,
> = PresentationComponentSystemRoleGroups<
  PresentationActionComponentSystemComponents,
  PresentationBadgeComponentSystemComponents,
  PresentationCollectionChromeComponentSystemComponents,
  PresentationCollectionComponentSystemComponents<TDataTableRenderer>,
  PresentationDocumentWorkspaceComponentSystemComponents<
    TDocumentWorkspaceTreeControlsProps,
    TJsonDocumentDiffProps,
    TJsonDocumentEditorProps
  >,
  PresentationFieldValueComponentSystemComponents,
  PresentationFeedbackComponentSystemComponents,
  PresentationFormComponentSystemComponents<TDateTimeFilterValue>,
  PresentationMetricComponentSystemComponents,
  PresentationNavigationComponentSystemComponents,
  PresentationProcessComponentSystemComponents,
  PresentationPromptComponentSystemComponents,
  PresentationRecordComponentSystemComponents,
  PresentationSurfaceComponentSystemComponents,
  PresentationTabsComponentSystemComponents,
  PresentationViewChromeComponentSystemComponents
>

export type PresentationShadcnComponentSystem<
  TDateTimeFilterValue = any,
  TDataTableRenderer extends PresentationDataTableRenderer =
    PresentationShadcnDataTableRenderer,
  TDocumentWorkspaceTreeControlsProps extends object = any,
  TJsonDocumentDiffProps extends object = any,
  TJsonDocumentEditorProps extends object = any,
> = PresentationComponentSystem<
  PresentationActionComponentSystemComponents,
  PresentationBadgeComponentSystemComponents,
  PresentationCollectionChromeComponentSystemComponents,
  PresentationCollectionComponentSystemComponents<TDataTableRenderer>,
  PresentationDocumentWorkspaceComponentSystemComponents<
    TDocumentWorkspaceTreeControlsProps,
    TJsonDocumentDiffProps,
    TJsonDocumentEditorProps
  >,
  PresentationFieldValueComponentSystemComponents,
  PresentationFeedbackComponentSystemComponents,
  PresentationFormComponentSystemComponents<TDateTimeFilterValue>,
  PresentationMetricComponentSystemComponents,
  PresentationNavigationComponentSystemComponents,
  PresentationProcessComponentSystemComponents,
  PresentationPromptComponentSystemComponents,
  PresentationRecordComponentSystemComponents,
  PresentationSurfaceComponentSystemComponents,
  PresentationTabsComponentSystemComponents,
  PresentationViewChromeComponentSystemComponents
>
