import {
  findPresentationView,
} from './module'
import type {
  PresentationModuleDefinition,
  ViewDefinition,
} from './module'
import type {
  PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'

export interface ProjectViewChromeContentDiagnosticsOptions {
  readonly module: Pick<PresentationModuleDefinition, 'Views'> | null
  readonly view: ViewDefinition | null
}

export function projectViewChromeContentDiagnostics({
  module,
  view,
}: ProjectViewChromeContentDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  return collectViewTree(module, view).flatMap(projectSingleViewChromeContentDiagnostics)
}

function projectSingleViewChromeContentDiagnostics(
  view: ViewDefinition,
): readonly PresentationProjectionDiagnostic[] {
  const chrome = view.Chrome
  if (!chrome) {
    return []
  }

  const hasLegacyTitle = Boolean(chrome.Title)
  const hasLegacySubtitle = Boolean(chrome.Subtitle)
  const content = chrome.Content
  if (!content && (hasLegacyTitle || hasLegacySubtitle)) {
    return [
      createViewChromeContentDiagnostic({
        fields: [
          hasLegacyTitle ? 'Title' : null,
          hasLegacySubtitle ? 'Subtitle' : null,
        ].filter((field): field is string => Boolean(field)),
        message:
          `View '${view.Name}' chrome still declares legacy title/subtitle fields ` +
          'without Content; the frontend will use legacy chrome content fallback semantics.',
        reason: 'legacy-content-fallback',
        view,
      }),
    ]
  }

  const diagnostics: PresentationProjectionDiagnostic[] = []
  if (content && hasLegacyTitle && !content.Title) {
    diagnostics.push(
      createViewChromeContentDiagnostic({
        fields: ['Title'],
        message:
          `View '${view.Name}' chrome Content does not declare a title, but legacy Title is present; ` +
          'the frontend will use legacy title fallback semantics.',
        reason: 'legacy-title-fallback',
        view,
      }),
    )
  }

  const hasContentDescription = Boolean(
    content?.Description ??
    content?.DescriptionTemplate ??
    content?.Subtitle,
  )
  if (content && hasLegacySubtitle && !hasContentDescription) {
    diagnostics.push(
      createViewChromeContentDiagnostic({
        fields: ['Subtitle'],
        message:
          `View '${view.Name}' chrome Content does not declare description or subtitle content, ` +
          'but legacy Subtitle is present; the frontend will use legacy subtitle fallback semantics.',
        reason: 'legacy-subtitle-fallback',
        view,
      }),
    )
  }

  return diagnostics
}

function collectViewTree(
  module: Pick<PresentationModuleDefinition, 'Views'> | null,
  view: ViewDefinition | null,
  seen = new Set<string>(),
): readonly ViewDefinition[] {
  if (!view || seen.has(view.Id)) {
    return []
  }

  seen.add(view.Id)
  return [
    view,
    ...view.Regions.flatMap((region) =>
      region.ViewIds.flatMap((viewId) =>
        collectViewTree(module, findPresentationView(module, viewId), seen),
      ),
    ),
  ]
}

function createViewChromeContentDiagnostic({
  fields,
  message,
  reason,
  view,
}: {
  readonly fields: readonly string[]
  readonly message: string
  readonly reason: string
  readonly view: ViewDefinition
}): PresentationProjectionDiagnostic {
  return {
    category: 'incomplete-projection',
    details: {
      fields,
      viewId: view.Id,
    },
    id: `view-chrome.${view.Id}.content.${reason}`,
    interpretation: {
      status: 'locally-interpreted',
      target: 'react',
    },
    message,
    severity: 'warning',
    source: 'view-chrome-content-projection',
    subject: {
      id: view.Id,
      kind: 'view-chrome',
      name: view.Name,
    },
    suggestedNextStep:
      'Declare View.Chrome.Content with PresentationContentDefinition and remove legacy title/subtitle fallback fields.',
  }
}
