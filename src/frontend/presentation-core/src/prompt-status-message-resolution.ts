import {
  getErrorMessage,
} from './projected-activity-state-model'
import type { ProjectedDocumentActionStatusMap } from './projected-action-status-projection'
import {
  resolvePresentationContent,
} from './presentation-content-resolution'
import {
  formatPresentationValue,
  resolvePresentationTemplate,
} from './presentation-value-resolution'
import type { PresentationDataSourceResolver } from './presentation-data-source-runtime'
import type { ViewDefinition } from './module'
import {
  promptStatusMessageKinds,
  type PromptStatusMessageDefinition,
} from '@cohesivesystems/presentation-contracts'

export interface ProjectedPromptStatusMessage {
  readonly definition: PromptStatusMessageDefinition
  readonly label: string
  readonly tone: 'default' | 'error'
}

export interface ResolvePromptStatusMessagesOptions {
  readonly actionStatuses: ProjectedDocumentActionStatusMap
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly region?: string
  readonly view: Pick<ViewDefinition, 'PromptStatusMessages'>
}

export function resolvePromptStatusMessages({
  actionStatuses,
  dataSourceResolver,
  region,
  view,
}: ResolvePromptStatusMessagesOptions): readonly ProjectedPromptStatusMessage[] {
  return (view.PromptStatusMessages ?? []).flatMap((definition) => {
    if (region && definition.Region !== region) {
      return []
    }

    const label = resolvePromptStatusMessageLabel({
      actionStatuses,
      dataSourceResolver,
      definition,
    })
    if (!label) {
      return []
    }

    return [{
      definition,
      label,
      tone: isErrorTone(definition) ? 'error' : 'default',
    }]
  })
}

export function resolvePromptStatusMessageText(
  options: ResolvePromptStatusMessagesOptions,
) {
  return resolvePromptStatusMessages(options)[0]?.label ?? null
}

function resolvePromptStatusMessageLabel({
  actionStatuses,
  dataSourceResolver,
  definition,
}: {
  readonly actionStatuses: ProjectedDocumentActionStatusMap
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly definition: PromptStatusMessageDefinition
}) {
  if (definition.Kind === promptStatusMessageKinds.actionPending) {
    return definition.ActionId && actionStatuses[definition.ActionId]?.isPending
      ? resolvePromptStatusMessageContent(
          definition,
          actionStatuses[definition.ActionId],
          definition.Message,
        )
      : null
  }

  if (definition.Kind === promptStatusMessageKinds.actionError) {
    const error = definition.ActionId
      ? actionStatuses[definition.ActionId]?.error
      : null
    const errorMessage = error ? getErrorMessage(error) : null
    return errorMessage
      ? resolvePromptStatusMessageContent(definition, { error, errorMessage }, errorMessage)
      : null
  }

  if (
    definition.Kind === promptStatusMessageKinds.dataFieldEquals ||
    definition.Kind === promptStatusMessageKinds.dataFieldTruthy
  ) {
    const dataSourceId = definition.DataSourceId
    if (!dataSourceId) {
      return null
    }

    const value = dataSourceResolver.readPath(dataSourceId, definition.FieldPath)
    if (
      definition.Kind === promptStatusMessageKinds.dataFieldEquals &&
      !doesPromptStatusValueEqual(value, definition.ExpectedValue)
    ) {
      return null
    }

    if (
      definition.Kind === promptStatusMessageKinds.dataFieldTruthy &&
      !isPromptStatusTruthy(value)
    ) {
      return null
    }

    const data = dataSourceResolver.read(dataSourceId)
    const legacyLabel =
      resolvePresentationTemplate(definition.MessageTemplate, data) ??
      definition.Message
    return (
      resolvePromptStatusMessageContent(definition, data, legacyLabel)
    )
  }

  return null
}

function resolvePromptStatusMessageContent(
  definition: PromptStatusMessageDefinition,
  data: unknown,
  fallbackLabel: string,
) {
  const content = resolvePresentationContent(definition.Content, data)
  return content.description ?? content.title ?? content.subtitle ?? fallbackLabel
}

function doesPromptStatusValueEqual(value: unknown, expected: string | null | undefined) {
  if (expected === null || expected === undefined) {
    return isPromptStatusTruthy(value)
  }

  return formatPresentationValue(value) === expected
}

function isPromptStatusTruthy(value: unknown) {
  return Boolean(value) && value !== 'false' && value !== '0'
}

function isErrorTone(definition: PromptStatusMessageDefinition) {
  return (
    definition.Kind === promptStatusMessageKinds.actionError ||
    definition.Tone === 'danger' ||
    definition.Tone === 'error'
  )
}
