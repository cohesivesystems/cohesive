import {
  findPresentationView,
  type FlowDefinition,
  type PresentationModuleDefinition,
  type PromptDocumentPreviewDefinition,
  type ViewDefinition,
} from './module'
import { conventionalPreviewResourceFields } from '@cohesive/presentation-contracts'
import {
  presentationDataSourceBindings,
  type PresentationDataSourceAuthorizationRequirement,
  type PresentationDataSourceBinding,
} from './presentation-data-source-binding-model'
import { readObjectProperty } from './object-path'
import {
  resolvePresentationContent,
} from './presentation-content-resolution'
import {
  formatPresentationValue,
  readPresentationFieldValue,
  resolvePresentationTemplate,
  resolvePresentationValue,
} from './presentation-value-resolution'

export type ProjectedPromptDocumentPreviewResource = Readonly<Record<string, unknown>> & {
  readonly Document?: unknown
  readonly Id?: string | null
  readonly Name?: string | null
}

export type ProjectedPromptDocumentPreviewData<
  TResponse = unknown,
  TRequest = unknown,
  TResource extends ProjectedPromptDocumentPreviewResource =
    ProjectedPromptDocumentPreviewResource,
  TValues extends Readonly<Record<string, unknown>> = Readonly<Record<string, never>>,
> = TValues & {
  readonly request: TRequest
  readonly response: TResponse
  readonly resource: TResource
  readonly text: string
}

export interface ProjectPromptDocumentPreviewDataOptions<
  TResponse,
  TRequest,
  TResource extends ProjectedPromptDocumentPreviewResource,
  TValues extends Readonly<Record<string, unknown>>,
> {
  readonly definition?: PromptDocumentPreviewDefinition | null
  readonly request?: TRequest
  readonly resource?: TResource | null
  readonly response: TResponse
  readonly values?: TValues
}

export interface PromptDocumentPreviewDataSourceState {
  readonly data?: unknown
  readonly error?: unknown
  readonly isFetching?: boolean
  readonly isPending?: boolean
}

export interface PromptDocumentPreviewFlowEntryProjection {
  readonly flow: Pick<FlowDefinition, 'Id'>
  readonly view: ViewDefinition | null
}

export function findPromptDocumentPreviewView(
  module: PresentationModuleDefinition | null,
  promptView: ViewDefinition | null | undefined,
): ViewDefinition | null {
  if (!module || !promptView) {
    return null
  }

  if (promptView.PromptDocumentPreview) {
    return promptView
  }

  for (const region of promptView.Regions) {
    for (const viewId of region.ViewIds) {
      const view = findPresentationView<ViewDefinition>(module, viewId)
      if (view?.PromptDocumentPreview) {
        return view
      }
    }
  }

  return null
}

export function createPromptDocumentPreviewDataSourceBindings({
  activeEntries,
  authorization,
  module,
  state,
  statesByFlowId,
}: {
  readonly activeEntries: readonly PromptDocumentPreviewFlowEntryProjection[]
  readonly authorization: PresentationDataSourceAuthorizationRequirement
  readonly module: PresentationModuleDefinition | null
  readonly state?: PromptDocumentPreviewDataSourceState | null
  readonly statesByFlowId?: Readonly<Record<string, PromptDocumentPreviewDataSourceState | null | undefined>>
}): readonly PresentationDataSourceBinding[] {
  return activeEntries.flatMap((entry) => {
    const previewState = statesByFlowId?.[entry.flow.Id] ?? state
    const dataSourceIds = resolvePromptDocumentPreviewDataSourceIds(module, entry.view)
    return dataSourceIds.map((dataSourceId) =>
      presentationDataSourceBindings.localValue({
        authorization,
        data: previewState?.data ?? null,
        dataSourceId,
        error: previewState?.error,
        isFetching: previewState?.isFetching,
        isPending: previewState?.isPending,
      }),
    )
  })
}

export function resolvePromptDocumentPreviewDataSourceIds(
  module: PresentationModuleDefinition | null,
  promptView: ViewDefinition | null | undefined,
) {
  const previewView = findPromptDocumentPreviewView(module, promptView)
  return previewView?.PromptDocumentPreview?.DataSourceId
    ? [previewView.PromptDocumentPreview.DataSourceId]
    : []
}

export function findPromptDocumentPreviewRegionId(
  promptView: ViewDefinition,
  previewViewId: string,
) {
  return promptView.Regions.find((region) => region.ViewIds.includes(previewViewId))?.Id ?? null
}

export function findPromptDocumentPreviewStatusRegionId(
  promptView: ViewDefinition,
  previewViewId: string,
) {
  const previewRegionId = findPromptDocumentPreviewRegionId(promptView, previewViewId)
  const messages = promptView.PromptStatusMessages ?? []
  const statusMessages = messages.filter(
    (message) =>
      message.Region !== previewRegionId &&
      message.Region.toLocaleLowerCase().includes('status'),
  )
  if (previewRegionId) {
    const previewRegionStatusMessage = statusMessages.find((message) =>
      message.Region.toLocaleLowerCase().includes(previewRegionId.toLocaleLowerCase()),
    )
    if (previewRegionStatusMessage) {
      return previewRegionStatusMessage.Region
    }
  }

  return statusMessages[0]?.Region ?? null
}

export function projectPromptDocumentPreviewData<
  TResponse,
  TRequest = unknown,
  TResource extends ProjectedPromptDocumentPreviewResource =
    ProjectedPromptDocumentPreviewResource,
  TValues extends Readonly<Record<string, unknown>> = Readonly<Record<string, never>>,
>({
  definition,
  request,
  resource,
  response,
  values,
}: ProjectPromptDocumentPreviewDataOptions<
  TResponse,
  TRequest,
  TResource,
  TValues
>): ProjectedPromptDocumentPreviewData<TResponse, TRequest, TResource, TValues> {
  const responseFields = isRecord(response) ? response : {}
  const responseResource = (
    readRecord(responseFields, 'resource') ??
    readRecord(responseFields, 'Resource')
  ) as TResource | null
  const projectedResource =
    resource ??
    responseResource ??
    createConventionalPreviewResource(responseFields, response) as TResource
  const data = {
    ...responseFields,
    ...values,
    request,
    response,
    resource: projectedResource,
  }
  const text = resolvePromptDocumentPreviewText(definition, data)

  return {
    ...data,
    text,
  } as ProjectedPromptDocumentPreviewData<TResponse, TRequest, TResource, TValues>
}

export function resolvePromptDocumentPreviewText(
  definition: PromptDocumentPreviewDefinition | null | undefined,
  data: unknown,
) {
  const contentText = resolvePromptDocumentPreviewContentText(definition, data)
  if (contentText !== null) {
    return contentText
  }

  const definedValue = resolvePresentationValue(definition?.DocumentText, data)
  if (definedValue !== undefined && definedValue !== null) {
    return formatPromptDocumentText(definedValue)
  }

  const resourceDocument = readPresentationFieldValue(data, 'resource.Document')
  if (resourceDocument !== undefined && resourceDocument !== null) {
    return formatPromptDocumentText(resourceDocument)
  }

  const responseDocument = readPresentationFieldValue(data, 'response.Document')
  if (responseDocument !== undefined && responseDocument !== null) {
    return formatPromptDocumentText(responseDocument)
  }

  return formatPromptDocumentText(data)
}

export function resolvePromptDocumentPreviewTitle(
  definition: PromptDocumentPreviewDefinition | null | undefined,
  data: unknown,
  fallback = 'Document preview',
) {
  const content = resolvePresentationContent(definition?.Content, data)
  return (
    content.title ??
    formatPresentationValue(resolvePresentationValue(definition?.Title, data)) ??
    fallback
  )
}

export function resolvePromptDocumentPreviewPath(
  definition: PromptDocumentPreviewDefinition | null | undefined,
  data: unknown,
  fallback = 'document-preview.json',
) {
  return resolvePresentationTemplate(definition?.DocumentPathTemplate, data) ?? fallback
}

function resolvePromptDocumentPreviewContentText(
  definition: PromptDocumentPreviewDefinition | null | undefined,
  data: unknown,
) {
  const content = definition?.Content
  if (!content) {
    return null
  }

  const description = resolvePresentationValue(content.Description, data)
  if (description !== undefined && description !== null) {
    return formatPromptDocumentText(description)
  }

  const descriptionTemplate = resolvePresentationTemplate(content.DescriptionTemplate, data)
  if (descriptionTemplate !== null) {
    return descriptionTemplate
  }

  const subtitle = resolvePresentationValue(content.Subtitle, data)
  return subtitle === undefined || subtitle === null ? null : formatPromptDocumentText(subtitle)
}

function createConventionalPreviewResource(
  responseFields: Readonly<Record<string, unknown>>,
  response: unknown,
): ProjectedPromptDocumentPreviewResource {
  const document = readPreviewField(responseFields, 'Document') ?? response
  const id =
    readPreviewStringField(responseFields, 'Id') ??
    readPreviewStringField(responseFields, 'ShapeGraphId') ??
    readPreviewStringField(responseFields, 'DocumentId')
  const name =
    readPreviewStringField(responseFields, 'Name') ??
    readPreviewStringField(responseFields, 'Title') ??
    id
  const resource: Record<string, unknown> = {
    Document: document,
    Id: id,
    Name: name,
  }

  for (const field of Object.values(conventionalPreviewResourceFields)) {
    const value = readPreviewField(responseFields, field)
    if (value !== undefined) {
      resource[field] = value
    }
  }

  return resource
}

function formatPromptDocumentText(value: unknown) {
  if (typeof value === 'string') {
    return value
  }

  if (value === null || value === undefined) {
    return ''
  }

  try {
    return JSON.stringify(value, null, 2)
  } catch {
    return String(value)
  }
}

function readRecord(
  source: Readonly<Record<string, unknown>>,
  key: string,
) {
  const value = readObjectProperty(source, key)
  return isRecord(value) ? value : null
}

function readPreviewStringField(
  source: Readonly<Record<string, unknown>>,
  key: string,
) {
  const value = readPreviewField(source, key)
  return typeof value === 'string' ? value : null
}

function readPreviewField(
  source: Readonly<Record<string, unknown>>,
  key: string,
) {
  return readObjectProperty(source, key)
}

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return Boolean(value && typeof value === 'object' && !Array.isArray(value))
}
