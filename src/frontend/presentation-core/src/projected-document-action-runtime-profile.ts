import type {
  DocumentProfileProjection,
} from './document-module'
import type {
  ActionPlacementDefinition,
} from './module'

export type ProjectedDocumentActionRuntimeProfile =
  NonNullable<DocumentProfileProjection['ActionRuntimeProfiles']>[number]

/**
 * Frontend runtime selection contract derived from a document action runtime
 * profile declared by the presentation module.
 *
 * The spec keeps semantic identifiers and state conventions in core while
 * allowing application adapters to supply concrete preview/start request and
 * response types.
 *
 * @typeParam TPreviewRequest - Request shape sent to the action preview endpoint.
 * @typeParam TPreviewResponse - Response shape returned from the action preview endpoint.
 * @typeParam TStartInput - Input shape used to start the action after preview.
 * @typeParam TStartResult - Result shape returned when the action is started.
 * @typeParam TMetadata - Metadata shape associated with the action runtime.
 */
export interface ProjectedDocumentActionRuntimeProfileSpec<
  TPreviewRequest extends object = Record<string, unknown>,
  TPreviewResponse = unknown,
  TStartInput = TPreviewRequest,
  TStartResult = unknown,
  TMetadata = unknown,
> {
  /** Action id that this runtime profile handles. */
  readonly actionId?: string

  /** Prefix used when registering diagnostics for this runtime profile. */
  readonly diagnosticsKeyPrefix?: string

  /** Presentation flow id used for preview/review UI, when one is modeled. */
  readonly flowId?: string | null

  /** Runtime profile id selected from the document profile projection. */
  readonly profileId?: string

  /**
   * Optional suffix used to partition process input state for this action.
   */
  readonly processInputStateKeySuffix?: string | null

  /** Source identifier for tracing where this runtime profile was projected. */
  readonly source?: string

  /**
   * Compile-time-only type anchors for application adapters. These values are
   * not populated by projection; they preserve strongly typed request,
   * response, result, and metadata shapes through generic inference.
   */
  readonly typeHints?: {
    /** Metadata shape associated with the runtime profile. */
    readonly metadata?: TMetadata

    /** Preview request shape sent before starting the action. */
    readonly previewRequest?: TPreviewRequest

    /** Preview response shape rendered by the review flow. */
    readonly previewResponse?: TPreviewResponse

    /** Start input shape submitted when the action is accepted. */
    readonly startInput?: TStartInput

    /** Start result shape returned after the action is accepted. */
    readonly startResult?: TStartResult
  }
}

export interface SelectDocumentActionRuntimeProfileOptions {
  readonly actionPlacements?: readonly ActionPlacementDefinition[]
}

/**
 * Selects the document action runtime profile that best matches placed actions
 * for a document workspace profile.
 */
export function selectDocumentActionRuntimeProfile(
  documentProfile: Pick<DocumentProfileProjection, 'ActionRuntimeProfiles' | 'Actions'> | null,
  options: SelectDocumentActionRuntimeProfileOptions = {},
): ProjectedDocumentActionRuntimeProfile | null {
  const profiles = documentProfile?.ActionRuntimeProfiles ?? []
  if (profiles.length <= 1) {
    return profiles[0] ?? null
  }

  const actionPlacements = options.actionPlacements ?? documentProfile?.Actions ?? []
  const placedActionIds = new Set(actionPlacements.map((placement) => placement.ActionId))

  return (
    profiles.find((profile) => placedActionIds.has(profile.ActionId)) ??
    profiles[0] ??
    null
  )
}

/**
 * Projects a backend-declared document action runtime profile into the frontend
 * action-runtime spec consumed by document workspace adapters.
 */
export function projectDocumentActionRuntimeProfileSpec<
  TPreviewRequest extends object = Record<string, unknown>,
  TPreviewResponse = unknown,
  TStartInput = TPreviewRequest,
  TStartResult = unknown,
  TMetadata = unknown,
>(
  documentProfile: Pick<DocumentProfileProjection, 'ActionRuntimeProfiles' | 'Actions'> | null,
  options: SelectDocumentActionRuntimeProfileOptions = {},
): ProjectedDocumentActionRuntimeProfileSpec<
  TPreviewRequest,
  TPreviewResponse,
  TStartInput,
  TStartResult,
  TMetadata
> | null {
  const profile = selectDocumentActionRuntimeProfile(documentProfile, options)
  return profile
    ? {
        actionId: profile.ActionId,
        diagnosticsKeyPrefix: profile.DiagnosticsKeyPrefix,
        flowId: profile.FlowId,
        profileId: profile.Id,
        processInputStateKeySuffix: profile.ProcessInputStateKeySuffix,
        source: profile.Source,
      }
    : null
}
