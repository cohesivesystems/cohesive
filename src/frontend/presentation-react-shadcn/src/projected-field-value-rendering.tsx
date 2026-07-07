import type { ReactNode } from 'react'

import type {
  FieldPresentationDefinition,
  NavigationRouteParameters,
  PresentationModuleDefinition,
} from '@cohesivesystems/presentation-core'
import {
  ResourceLinkBadge,
  TextBadge,
} from './projected-document-badges'
import type {
  PresentationTextBadgeTone,
} from '@cohesivesystems/presentation-tailwind'
import {
  resolvePresentationFieldValueLabel,
  resolvePresentationFieldValueIcon,
  resolvePresentationFieldValueTone,
} from '@cohesivesystems/presentation-core'
import {
  renderPresentationIcon,
} from './presentation-icon-registry'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import { formatBadgeValue } from '@cohesivesystems/presentation-core'
import {
  projectDocumentFieldNavigationBinding,
} from '@cohesivesystems/presentation-core'
import {
  readObjectPath,
} from '@cohesivesystems/presentation-core'
import {
  isProjectedDocumentBadgeField,
  isProjectedDocumentEntityReferenceField,
  matchesFieldDisplayKind,
} from '@cohesivesystems/presentation-core'
import {
  fieldEntityReferenceFallbackKinds,
  fieldDisplayKinds,
  fieldJsonDisplayModes,
  formatKinds,
} from '@cohesivesystems/presentation-contracts'

/**
 * Runtime inputs required to render a projected field value through the
 * shadcn-oriented presentation component system.
 */
export interface ProjectedFieldValueRenderContext {
  /** Component-system implementation used for scalar, code, JSON, and composite field value chrome. */
  readonly componentSystem: PresentationComponentSystem
  /** Optional route-to-href adapter used when entity-reference fields declare navigation bindings. */
  readonly createHref?:
    | ((routeId: string, parameters?: NavigationRouteParameters) => string | null)
    | undefined
  /** Fallback node used when the value is absent and the field has no modeled empty-value label. */
  readonly emptyValueFallback: ReactNode
  /** Presentation field definition that declares display kind, formatting, fallbacks, badges, and navigation. */
  readonly field: FieldPresentationDefinition
  /** Optional caller formatter for values that need host-specific text or node rendering. */
  readonly formatValue?: (context: ProjectedFieldValueFormatContext) => ReactNode
  /** Presentation module fragment used to resolve target bindings and icon definitions. */
  readonly module: Pick<PresentationModuleDefinition, 'Targets'> | null
  /** Optional navigation callback invoked for resolved entity-reference links. */
  readonly navigateHref?: (href: string) => void
  /** Current resource object that field paths, supporting values, and navigation bindings read from. */
  readonly resource: unknown
  /** Default rendering style for entity references that do not resolve to a navigation binding. */
  readonly unboundEntityReferenceStyle?: 'badge' | 'code'
  /** Raw field value supplied by the caller before display fallbacks are applied. */
  readonly value: unknown
}

/**
 * Value formatting context passed to host-provided field value formatters.
 */
export interface ProjectedFieldValueFormatContext {
  /** Presentation field definition associated with the value being formatted. */
  readonly field: FieldPresentationDefinition
  /** Resource object that owns the rendered field. */
  readonly resource: unknown
  /** Field value after display fallback resolution. */
  readonly value: unknown
}

/**
 * Renders a projected presentation field value as React content.
 *
 * The renderer applies modeled display fallbacks, supporting values, inline
 * badges, entity-reference navigation bindings, value labels, tones, icons,
 * and scalar formatting before delegating chrome to the component system.
 */
export function renderProjectedFieldValue({
  componentSystem,
  createHref,
  emptyValueFallback,
  field,
  formatValue,
  module,
  navigateHref,
  resource,
  unboundEntityReferenceStyle = 'code',
  value,
}: ProjectedFieldValueRenderContext) {
  const displayValue = resolveProjectedDisplayValue(field, resource, value)
  const supportingValues = readProjectedSupportingFieldValues(field, resource)
  const inlineBadges = renderProjectedInlineBadges(componentSystem, field, resource)

  if (isAbsentValue(displayValue)) {
    if (supportingValues.length > 0 || inlineBadges.length > 0) {
      return renderProjectedCompositeValue({
        componentSystem,
        inlineBadges,
        primaryValue: null,
        supportingValues,
      })
    }

    return renderEmptyValueFallback(componentSystem, field, emptyValueFallback)
  }

  const primaryValue = renderProjectedFieldPrimaryValue({
    componentSystem,
    createHref,
    field,
    formatValue,
    module,
    navigateHref,
    resource,
    unboundEntityReferenceStyle,
    value: displayValue,
  })

  return supportingValues.length > 0 || inlineBadges.length > 0
    ? renderProjectedCompositeValue({
      componentSystem,
      inlineBadges,
      primaryValue,
      supportingValues,
    })
    : primaryValue
}

function renderProjectedFieldPrimaryValue({
  componentSystem,
  createHref,
  field,
  formatValue,
  module,
  navigateHref,
  resource,
  unboundEntityReferenceStyle = 'code',
  value,
}: Omit<ProjectedFieldValueRenderContext, 'emptyValueFallback'>) {
  if (isProjectedDocumentEntityReferenceField(field)) {
    return renderProjectedEntityReferenceValue({
      componentSystem,
      createHref,
      field,
      module,
      navigateHref,
      resource,
      unboundEntityReferenceStyle,
      value,
    })
  }

  if (matchesFieldDisplayKind(field, fieldDisplayKinds.code, 'Code')) {
    return renderCodeValue(componentSystem, formatProjectedScalarValue(value, field, resource))
  }

  if (isProjectedDocumentBadgeField(field)) {
    const text =
      formatValue?.({ field, resource, value }) ??
      formatProjectedScalarValue(value, field, resource)
    const icon = renderProjectedFieldValueIcon({ field, module, value })

    return (
      <TextBadge
        componentSystem={componentSystem}
        tone={resolveProjectedFieldBadgeTone(field, resource, value)}
      >
        {icon ? (
          <span className="inline-flex min-w-0 items-center gap-1">
            {icon}
            <span className="truncate">{text}</span>
          </span>
        ) : text}
      </TextBadge>
    )
  }

  if (matchesFieldDisplayKind(field, fieldDisplayKinds.date, 'Date') ||
    matchesFieldDisplayKind(field, fieldDisplayKinds.dateTime, 'DateTime')) {
    return formatProjectedDateTime(componentSystem, String(value), field)
  }

  if (matchesFieldDisplayKind(field, fieldDisplayKinds.boolean, 'Boolean')) {
    return renderScalarValue(componentSystem, value ? 'Yes' : 'No', {
      tone: resolveProjectedFieldBadgeTone(field, resource),
    })
  }

  if (matchesFieldDisplayKind(field, fieldDisplayKinds.json, 'Json')) {
    return renderJsonValue(componentSystem, value, field, resource)
  }

  return renderScalarValue(componentSystem, formatProjectedScalarValue(value, field, resource), {
    tone: resolveProjectedFieldBadgeTone(field, resource),
  })
}

/**
 * Returns whether the standard shadcn field-value renderer understands the
 * supplied field's modeled display semantics.
 */
export function canRenderProjectedFieldValue(field: FieldPresentationDefinition) {
  return isProjectedDocumentEntityReferenceField(field) ||
    isProjectedDocumentBadgeField(field) ||
    matchesFieldDisplayKind(field, fieldDisplayKinds.code, 'Code') ||
    matchesFieldDisplayKind(field, fieldDisplayKinds.date, 'Date') ||
    matchesFieldDisplayKind(field, fieldDisplayKinds.dateTime, 'DateTime') ||
    matchesFieldDisplayKind(field, fieldDisplayKinds.boolean, 'Boolean') ||
    matchesFieldDisplayKind(field, fieldDisplayKinds.json, 'Json') ||
    matchesFieldDisplayKind(field, fieldDisplayKinds.text, 'Text') ||
    matchesFieldDisplayKind(field, fieldDisplayKinds.number, 'Number')
}

/**
 * Runtime inputs for rendering a projected entity-reference field value.
 */
export interface ProjectedEntityReferenceValueRenderContext {
  /** Component-system implementation used for fallback code, text, and badge rendering. */
  readonly componentSystem: PresentationComponentSystem
  /** Optional route-to-href adapter used to resolve modeled navigation bindings. */
  readonly createHref?:
    | ((routeId: string, parameters?: NavigationRouteParameters) => string | null)
    | undefined
  /** Entity-reference field definition. */
  readonly field: FieldPresentationDefinition
  /** Presentation module fragment containing target bindings used by navigation projection. */
  readonly module: Pick<PresentationModuleDefinition, 'Targets'> | null
  /** Optional callback invoked when a resolved entity-reference link is activated. */
  readonly navigateHref?: (href: string) => void
  /** Resource object that owns the entity-reference value. */
  readonly resource: unknown
  /** Caller fallback style used when the field does not declare its own fallback style. */
  readonly unboundEntityReferenceStyle: 'badge' | 'code'
  /** Entity-reference scalar value to render. */
  readonly value: unknown
}

/**
 * Renders an entity-reference field as a navigation badge when the modeled
 * binding resolves, or as the field's configured fallback style otherwise.
 */
export function renderProjectedEntityReferenceValue({
  componentSystem,
  createHref,
  field,
  module,
  navigateHref,
  resource,
  unboundEntityReferenceStyle: fallbackStyle,
  value,
}: ProjectedEntityReferenceValueRenderContext) {
  const unboundEntityReferenceStyle =
    resolveProjectedEntityReferenceFallbackStyle(field) ?? fallbackStyle
  const binding = projectDocumentFieldNavigationBinding({
    createHref,
    field,
    module,
    resource,
    value,
  })
  const label = binding?.label ?? formatProjectedScalarValue(value, field, resource)
  if (!label) {
    return null
  }

  if (binding && navigateHref) {
    return (
      <ResourceLinkBadge
        componentSystem={componentSystem}
        disabled={!binding.href}
        label={binding.label}
        onClick={() => {
          if (binding.href) {
            navigateHref(binding.href)
          }
        }}
        prefix={binding.prefix}
      />
    )
  }

  if (unboundEntityReferenceStyle === 'badge') {
    return <TextBadge componentSystem={componentSystem} tone="sky">{label}</TextBadge>
  }

  if (unboundEntityReferenceStyle === 'text') {
    return <span className="wrap-break-word">{label}</span>
  }

  return renderCodeValue(componentSystem, label)
}

/**
 * Resolves the visual tone for a projected field value badge or scalar chip.
 *
 * Value-specific tone rules take precedence over the field's general design
 * tone, and the result is coerced into the shadcn badge tone set.
 */
export function resolveProjectedFieldBadgeTone(
  field: FieldPresentationDefinition,
  resource?: unknown,
  value?: unknown,
): PresentationTextBadgeTone {
  const fieldValue = value ?? resolveFieldValue(field, resource)

  return coerceProjectedTextBadgeTone(
    resolvePresentationFieldValueTone(field, fieldValue, resource) ??
    field.Design?.Tone,
  )
}

function renderProjectedFieldValueIcon({
  field,
  module,
  value,
}: {
  readonly field: FieldPresentationDefinition
  readonly module: Pick<PresentationModuleDefinition, 'Targets'> | null
  readonly value: unknown
}) {
  const icon = resolvePresentationFieldValueIcon(field, value)
  if (!icon) {
    return null
  }

  return renderPresentationIcon({
    className: 'size-3 shrink-0',
    icon,
    module,
  })
}

function coerceProjectedTextBadgeTone(tone: string | null | undefined): PresentationTextBadgeTone {
  switch (tone?.toLocaleLowerCase()) {
    case 'accent':
    case 'primary':
    case 'violet':
      return 'violet'
    case 'amber':
    case 'warning':
      return 'amber'
    case 'danger':
    case 'error':
    case 'red':
      return 'red'
    case 'info':
    case 'sky':
      return 'sky'
    case 'success':
    case 'teal':
      return 'teal'
    default:
      return 'slate'
  }
}

function renderEmptyValueFallback(
  componentSystem: PresentationComponentSystem,
  field: FieldPresentationDefinition,
  fallback: ReactNode,
) {
  if (!field.Display?.EmptyValueLabel) {
    return fallback
  }

  const FieldValueEmpty = componentSystem.fieldValues.FieldValueEmpty
  return <FieldValueEmpty label={field.Display.EmptyValueLabel} />
}

/**
 * Resolves the fallback rendering style declared for an entity-reference field.
 *
 * Returns `null` when the field leaves fallback style selection to the caller.
 */
export function resolveProjectedEntityReferenceFallbackStyle(
  field: FieldPresentationDefinition,
): 'badge' | 'code' | 'text' | null {
  const fallback = field.Display?.EntityReferenceFallback
  if (fallback === null || fallback === undefined) {
    return null
  }

  if (matchesGeneratedEnum(fallback, fieldEntityReferenceFallbackKinds.badge, 'Badge')) {
    return 'badge'
  }

  if (matchesGeneratedEnum(fallback, fieldEntityReferenceFallbackKinds.text, 'Text')) {
    return 'text'
  }

  return 'code'
}

function renderCodeValue(
  componentSystem: PresentationComponentSystem,
  value: string,
) {
  const FieldValueCode = componentSystem.fieldValues.FieldValueCode
  return <FieldValueCode value={value} />
}

function resolveProjectedDisplayValue(
  field: FieldPresentationDefinition,
  resource: unknown,
  value: unknown,
) {
  if (!isAbsentValue(value)) {
    return value
  }

  for (const fallbackFieldPath of field.Display?.FallbackFieldPaths ?? []) {
    const fallbackValue = readObjectPath(resource, fallbackFieldPath)
    if (!isAbsentValue(fallbackValue)) {
      return fallbackValue
    }
  }

  return value
}

function readProjectedSupportingFieldValues(
  field: FieldPresentationDefinition,
  resource: unknown,
) {
  const pathValues = (field.Display?.SupportingFieldPaths ?? []).flatMap((fieldPath) => {
    const value = readObjectPath(resource, fieldPath)
    if (isAbsentValue(value)) {
      return []
    }

    const label = formatProjectedSupportingValue(value)
    return label ? [label] : []
  })

  const definedValues = (field.Display?.SupportingValues ?? []).flatMap((definition) => {
    const value = readObjectPath(resource, definition.FieldPath)
    if (isAbsentValue(value)) {
      return []
    }

    const label = formatProjectedSupportingValue(value, definition.Separator ?? undefined)
    return label
      ? [`${definition.Prefix ?? ''}${label}${definition.Suffix ?? ''}`]
      : []
  })

  return [...pathValues, ...definedValues]
}

function renderProjectedCompositeValue({
  componentSystem,
  inlineBadges,
  primaryValue,
  supportingValues,
}: {
  readonly componentSystem: PresentationComponentSystem
  readonly inlineBadges: readonly ReactNode[]
  readonly primaryValue: ReactNode
  readonly supportingValues: readonly string[]
}) {
  const FieldValueComposite = componentSystem.fieldValues.FieldValueComposite
  const FieldValueSupportingValue = componentSystem.fieldValues.FieldValueSupportingValue
  return (
    <FieldValueComposite
      inlineBadges={inlineBadges}
      primaryValue={primaryValue}
      supportingValues={supportingValues.map((value, index) => (
        <FieldValueSupportingValue key={`${value}:${index}`}>
          {value}
        </FieldValueSupportingValue>
      ))}
    />
  )
}

function renderProjectedInlineBadges(
  componentSystem: PresentationComponentSystem,
  field: FieldPresentationDefinition,
  resource: unknown,
) {
  return (field.Display?.InlineBadges ?? []).flatMap((badge) => {
    const value = readObjectPath(resource, badge.FieldPath)
    if (isAbsentValue(value)) {
      return []
    }

    const label = formatProjectedSupportingValue(value)
    if (!label) {
      return []
    }

    const tone =
      badge.ToneFieldPath
        ? readOptionalString(readObjectPath(resource, badge.ToneFieldPath))
        : null

    return [
      <TextBadge
        componentSystem={componentSystem}
        key={badge.FieldPath}
        tone={coerceProjectedTextBadgeTone(tone ?? badge.Tone)}
      >
        {label}
      </TextBadge>,
    ]
  })
}

function formatProjectedSupportingValue(
  value: unknown,
  separator = ', ',
): string | null {
  if (Array.isArray(value)) {
    const items: string[] = value
      .map((item) => formatProjectedSupportingValue(item, separator))
      .filter((item): item is string => Boolean(item))

    return items.length > 0 ? items.join(separator) : null
  }

  const scalarValue = formatBadgeValue(value)
  if (scalarValue !== null) {
    return scalarValue
  }

  try {
    return JSON.stringify(value)
  } catch {
    return String(value)
  }
}

function formatProjectedScalarValue(
  value: unknown,
  field: FieldPresentationDefinition,
  resource?: unknown,
) {
  const label =
    readFirstDisplayFieldPathValue(field.Display?.LabelFieldPaths, resource) ??
    resolvePresentationFieldValueLabel(field, value) ??
    formatBadgeValue(value)
  if (label === null || label === undefined) {
    return String(value)
  }

  return field.Display?.ValuePrefix
    ? `${field.Display.ValuePrefix}${label}`
    : label
}

function readFirstDisplayFieldPathValue(
  fieldPaths: readonly string[] | null | undefined,
  resource: unknown,
) {
  if (!resource) {
    return null
  }

  for (const fieldPath of fieldPaths ?? []) {
    const value = readObjectPath(resource, fieldPath)
    if (!isAbsentValue(value)) {
      return formatProjectedSupportingValue(value)
    }
  }

  return null
}

function isAbsentValue(value: unknown) {
  return value === null || value === undefined || value === ''
}

function resolveFieldValue(
  field: FieldPresentationDefinition,
  resource: unknown,
) {
  return readObjectPath(resource, field.Field)
}

function renderJsonValue(
  componentSystem: PresentationComponentSystem,
  value: unknown,
  field: FieldPresentationDefinition,
  resource: unknown,
) {
  const formattedValue = JSON.stringify(value) ?? String(value)
  if (matchesGeneratedEnum(field.Display?.JsonMode, fieldJsonDisplayModes.inline, 'Inline')) {
    return renderScalarValue(componentSystem, formattedValue, {
      tone: resolveProjectedFieldBadgeTone(field, resource),
    })
  }

  const tone = resolveProjectedFieldBadgeTone(field, resource)
  const FieldValueJson = componentSystem.fieldValues.FieldValueJson

  return (
    <FieldValueJson
      formattedValue={JSON.stringify(value, null, 2) ?? String(value)}
      tone={tone}
    />
  )
}

function renderScalarValue(
  componentSystem: PresentationComponentSystem,
  value: ReactNode,
  options: {
    readonly title?: string
    readonly tone?: ReturnType<typeof resolveProjectedFieldBadgeTone>
  } = {},
) {
  const FieldValueScalar = componentSystem.fieldValues.FieldValueScalar
  return (
    <FieldValueScalar title={options.title} tone={options.tone}>
      {value}
    </FieldValueScalar>
  )
}

function formatProjectedDateTime(
  componentSystem: PresentationComponentSystem,
  value: string,
  field: FieldPresentationDefinition,
) {
  const parsed = new Date(value)
  if (Number.isNaN(parsed.getTime())) {
    return renderScalarValue(componentSystem, value)
  }

  const text = formatDateTimeValue(parsed, field)

  return renderScalarValue(componentSystem, text, { title: value })
}

function formatDateTimeValue(
  value: Date,
  field: FieldPresentationDefinition,
) {
  if (field.Format?.Pattern === 'iso') {
    return value.toISOString()
  }

  const options: Intl.DateTimeFormatOptions = {
    dateStyle: 'medium',
  }
  const formatKind = field.Format?.Kind
  const dateOnly =
    matchesGeneratedEnum(formatKind, formatKinds.date, 'Date') ||
    matchesFieldDisplayKind(field, fieldDisplayKinds.date, 'Date')
  const timeZone = normalizeTimeZone(field.Format?.TimeZone)

  if (!dateOnly) {
    options.timeStyle = 'short'
  }

  if (timeZone) {
    options.timeZone = timeZone
  }

  try {
    return new Intl.DateTimeFormat(undefined, options).format(value)
  } catch {
    return dateOnly
      ? value.toLocaleDateString()
      : value.toLocaleString()
  }
}

function normalizeTimeZone(value: string | null | undefined) {
  if (!value) {
    return undefined
  }

  return value.toLocaleLowerCase() === 'utc' ? 'UTC' : value
}

function matchesGeneratedEnum(
  value: number | string | null | undefined,
  numericValue: number | string,
  label: string,
) {
  return value === numericValue ||
    String(value) === String(numericValue) ||
    value?.toString().toLocaleLowerCase() === label.toLocaleLowerCase()
}

function readOptionalString(value: unknown) {
  return typeof value === 'string' && value.length > 0 ? value : null
}
