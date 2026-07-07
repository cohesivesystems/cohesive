import { Fragment, useMemo, type ReactNode } from 'react'

import {
  createDefaultDateTimeFilter,
  createPresentationEnumDiscriminator,
  createPresentationProjectionDiagnostic,
  defaultPresentationComponentSet,
  findPresentationAction,
  findPresentationField,
  isViewChromeSlotKind,
  matchesPresentationEnum,
  normalizeDateTimeFilter,
  readObjectPath,
  resolvePresentationFieldValueLabel,
  resolvePresentationFieldValueTone,
  type ActionPlacementDefinition,
  type DataSourceQueryDefinition,
  type DataSourceQueryFieldDefinition,
  type DateTimeFilterValue,
  type FieldPresentationDefinition,
  type InputFormDefinition,
  type InputFormFieldDefinition,
  type PresentationModuleDefinition,
  type PresentationDataSourceResolver,
  type PresentationProjectionDiagnostic,
  type ProjectedInputFormRuntime,
  type ProjectedInputFormTargetContext,
  type QueryFormDefinition,
  type ViewChromeSlotDefinition,
  type ViewDefinition,
} from '@cohesive/presentation-core'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type { PresentationDesignSystem } from '@cohesive/presentation-tailwind'
import {
  projectPresentationActionIconDiagnostics,
} from './presentation-icon-diagnostics'
import {
  renderPresentationIcon,
} from './presentation-icon-registry'
import { ProjectedStatusBlock } from './projected-activity-state'
import {
  ProjectedViewSurface,
  type ProjectedViewSurfaceChromeSlotRenderer,
} from './projected-view-surface'
import {
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesive/presentation-react'
import {
  inputFormChoiceDefaultSelections,
  inputFormFieldControlKinds,
  inputFormFieldKinds,
  viewChromeSlotKinds,
} from '@cohesive/presentation-contracts'

export type {
  ProjectedInputFormActionContext,
  ProjectedInputFormRuntime,
  ProjectedInputFormTargetContext,
  ProjectedInputFormValueChangeContext,
} from '@cohesive/presentation-core'

/**
 * Context supplied to a domain-specific input-form field renderer.
 */
export interface ProjectedInputFormFieldRenderContext {
  /** Optional resolver that maps a choice and tone to toggle styling. */
  readonly choiceToggleClassName?: ProjectedInputFormChoiceClassNameResolver

  /** Optional resolver that normalizes domain choice metadata into a presentation tone. */
  readonly choiceTone?: ProjectedInputFormChoiceToneResolver

  /** Choices resolved from the field's semantic choice source. */
  readonly choices: readonly ProjectedInputFormChoice[]

  /** Component implementation set used for projected form controls. */
  readonly componentSystem: PresentationComponentSystem

  /** Design-system class and semantic resolvers used for projected form layout. */
  readonly designSystem: PresentationDesignSystem

  /** Input-form field being rendered. */
  readonly field: InputFormFieldDefinition

  /** Input form that owns the field. */
  readonly inputForm: InputFormDefinition

  /** Presentation field metadata referenced by the input-form field, when available. */
  readonly presentationField: FieldPresentationDefinition | null

  /** Data-source query field supplied when a relation-query target specializes the input form. */
  readonly queryField: DataSourceQueryFieldDefinition | null

  /** Query-form binding supplied when a relation-query target specializes the input form. */
  readonly queryForm: QueryFormDefinition | null

  /** Shared or target state id affected by the input form. */
  readonly stateId: string

  /** Target interpretation attached to this form instance. */
  readonly target: ProjectedInputFormTargetContext

  /** Updates the field value at its declared value path. */
  readonly setFieldValue: (value: unknown) => void

  /** Current value read from the caller-owned form value at the field value path. */
  readonly value: unknown
}

/**
 * Override renderer for an input-form field.
 */
export type ProjectedInputFormFieldRenderer = (
  context: ProjectedInputFormFieldRenderContext,
) => ReactNode

/**
 * Normalized choice projected from an input-form choice source.
 */
export interface ProjectedInputFormChoice {
  /** User-facing choice label. */
  readonly label: string

  /** Optional semantic tone supplied by the choice source or caller resolver. */
  readonly tone?: string | null

  /** Stable choice value written into input-form state. */
  readonly value: string
}

/**
 * Context supplied when resolving choice tone or styling.
 */
export interface ProjectedInputFormChoiceRenderContext {
  /** Choice being rendered. */
  readonly choice: ProjectedInputFormChoice

  /** Input-form field that owns the choice. */
  readonly field: InputFormFieldDefinition

  /** Input form that owns the field. */
  readonly inputForm: InputFormDefinition

  /** Presentation field metadata referenced by the input-form field, when available. */
  readonly presentationField: FieldPresentationDefinition | null

  /** Data-source query field supplied when a relation-query target specializes the input form. */
  readonly queryField: DataSourceQueryFieldDefinition | null

  /** Query-form binding supplied when a relation-query target specializes the input form. */
  readonly queryForm: QueryFormDefinition | null

  /** Shared or target state id affected by the input form. */
  readonly stateId: string

  /** Target interpretation attached to this form instance. */
  readonly target: ProjectedInputFormTargetContext
}

/**
 * Choice styling context after tone resolution has completed.
 */
export interface ProjectedInputFormChoiceClassNameContext
  extends ProjectedInputFormChoiceRenderContext {
  /** Resolved presentation tone for the choice, or null when no tone applies. */
  readonly tone: string | null
}

/**
 * Resolves a semantic choice into a presentation tone understood by the local design system.
 */
export type ProjectedInputFormChoiceToneResolver = (
  context: ProjectedInputFormChoiceRenderContext,
) => string | null | undefined

/**
 * Resolves final toggle classes for a choice after tone resolution.
 */
export type ProjectedInputFormChoiceClassNameResolver = (
  context: ProjectedInputFormChoiceClassNameContext,
) => string | undefined

/**
 * Props required to project a backend-declared input form into React controls.
 *
 * @typeParam TValue - Shape of the caller-owned form state object.
 */
export interface ProjectedInputFormProps<TValue extends object = object> {
  /** Optional resolver that maps a choice and tone to toggle styling. */
  readonly choiceToggleClassName?: ProjectedInputFormChoiceClassNameResolver

  /** Optional resolver that normalizes domain choice metadata into a presentation tone. */
  readonly choiceTone?: ProjectedInputFormChoiceToneResolver

  /** Class name applied to the projected view surface. */
  readonly className?: string

  /** Class name applied to projected view chrome after the form body. */
  readonly chromeAfterContentClassName?: string

  /** Class name applied to projected view chrome before the form body. */
  readonly chromeBeforeContentClassName?: string

  /** Class name applied to projected view chrome in the surface footer. */
  readonly chromeFooterClassName?: string

  /** Class name applied to projected view chrome in the surface header. */
  readonly chromeHeaderClassName?: string

  /** Class name applied to the projected view surface content area. */
  readonly contentClassName?: string

  /** Component implementation set used for projected form controls. */
  readonly componentSystem: PresentationComponentSystem

  /** Presentation target component set used to resolve declared icons. */
  readonly componentSet?: string

  /** Design-system class and semantic resolvers used for projected form layout. */
  readonly designSystem: PresentationDesignSystem

  /** Resolver for data sources used by field choice sources. */
  readonly dataSourceResolver: PresentationDataSourceResolver

  /** Field renderer overrides keyed by input-form field id or presentation field id. */
  readonly fieldRenderers?: Readonly<Record<string, ProjectedInputFormFieldRenderer>>

  /** Input form to project; null renders a diagnostic status block. */
  readonly inputForm: InputFormDefinition | null

  /** Presentation module that owns the input form, fields, and actions. */
  readonly module: PresentationModuleDefinition

  /** Runtime state and action handlers; null renders a diagnostic status block. */
  readonly runtime: ProjectedInputFormRuntime<TValue> | null

  /** Interprets non-form view chrome slots declared by the hosting view. */
  readonly renderChromeSlot?: ProjectedViewSurfaceChromeSlotRenderer

  /** Optional target specialization attached to this input form instance. */
  readonly target?: ProjectedInputFormTargetContext | null

  /** View that hosts the input form and supplies surface identity. */
  readonly view: ViewDefinition
}

interface ProjectedInputFormFieldProjection {
  readonly inputField: InputFormFieldDefinition
  readonly queryField: DataSourceQueryFieldDefinition | null
}

/**
 * Projects a semantic input-form definition into concrete React form controls.
 *
 * Generic input kinds are handled locally; domain-specific fields, choice tones,
 * styling, and action behavior stay injectable through renderer props and runtime.
 */
export function ProjectedInputForm<TValue extends object = object>({
  choiceToggleClassName,
  choiceTone,
  className,
  chromeAfterContentClassName,
  chromeBeforeContentClassName,
  chromeFooterClassName,
  chromeHeaderClassName,
  componentSet = defaultPresentationComponentSet,
  componentSystem,
  contentClassName,
  dataSourceResolver,
  designSystem,
  fieldRenderers,
  inputForm,
  module,
  renderChromeSlot,
  runtime,
  target,
  view,
}: ProjectedInputFormProps<TValue>) {
  const actionIconPlacements = useMemo(
    () => resolveInputFormActionIconPlacements(inputForm, view),
    [inputForm, view],
  )
  const actionIconDiagnostics = useMemo(
    () => projectPresentationActionIconDiagnostics({
      actionPlacements: actionIconPlacements,
      componentSet,
      module,
      source: `projected-input-form-icons:${view.Id}:${inputForm?.Id ?? 'missing'}`,
      surfaceId: view.Id,
      surfaceName: inputForm?.Name ?? view.Name,
    }),
    [
      actionIconPlacements,
      componentSet,
      inputForm?.Id,
      inputForm?.Name,
      module,
      view.Id,
      view.Name,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    `projected-input-form-icons:${view.Id}:${inputForm?.Id ?? 'missing'}`,
    actionIconDiagnostics,
  )
  const runtimeDiagnosticsSource =
    `projected-input-form-runtime:${view.Id}:${inputForm?.Id ?? 'missing'}`
  const runtimeDiagnostics = useMemo(
    () => createInputFormRuntimeDiagnostics({
      hasRuntime: runtime !== null,
      inputForm,
      source: runtimeDiagnosticsSource,
      view,
    }),
    [inputForm, runtime, runtimeDiagnosticsSource, view],
  )
  useRegisterPresentationProjectionDiagnostics(
    runtimeDiagnosticsSource,
    runtimeDiagnostics,
  )

  if (!inputForm) {
    return <ProjectedStatusBlock label={`Presentation view '${view.Name}' has no input form binding.`} />
  }

  if (!runtime) {
    return null
  }

  const formTarget = target ?? createDefaultInputFormTarget(inputForm)
  const projectedFields = projectInputFormFields(inputForm, formTarget.queryDefinition ?? null)
  const projectedFieldsById = new Map(
    projectedFields.map((field) => [field.inputField.Id, field] as const),
  )
  const choiceValuesByFieldId = Object.fromEntries(
    projectedFields.map(({ inputField, queryField }) => [
      inputField.Id,
      readInputFormChoices(
        dataSourceResolver,
        inputField,
        queryField,
        findPresentationField<FieldPresentationDefinition>(
          module,
          queryField?.FieldId ?? inputField.FieldId,
        ) ?? null,
      ).map((choice) => choice.value),
    ]),
  )
  const renderFormChromeSlot = (slot: ViewChromeSlotDefinition, chromeView: ViewDefinition | null) => {
    if (isViewChromeSlotKind(slot, viewChromeSlotKinds.actions)) {
      return renderInputFormActionRow({
        choiceValuesByFieldId,
        componentSet,
        componentSystem,
        designSystem,
        inputForm,
        module,
        placements: slot.Actions.length > 0 ? slot.Actions : inputForm.Actions,
        runtime,
        target: formTarget,
      })
    }

    return renderChromeSlot?.(slot, chromeView) ?? null
  }

  return (
    <ProjectedViewSurface
      className={className}
      chromeAfterContentClassName={chromeAfterContentClassName}
      chromeBeforeContentClassName={chromeBeforeContentClassName}
      chromeFooterClassName={chromeFooterClassName}
      chromeHeaderClassName={chromeHeaderClassName}
      componentSystem={componentSystem}
      contentClassName={cn(designSystem.classNames.formSurface.content({}), contentClassName)}
      collapsible
      renderChromeSlot={renderFormChromeSlot}
      title={view.Name}
      view={view}
    >
      {componentSystem.forms.InputForm({
        form: inputForm,
        viewId: view.Id,
        children: (
          <>
            {componentSystem.forms.InputFormGroups({
              className: designSystem.classNames.formSurface.groups({}),
              form: inputForm,
              children: inputForm.Groups.map((group) => {
                const groupFields = group.FieldIds.flatMap((fieldId) => {
                  const field = projectedFieldsById.get(fieldId)
                  return field ? [field] : []
                })
                if (groupFields.length === 0) {
                  return null
                }

                return (
                  <Fragment key={group.Id}>
                    {componentSystem.forms.InputFormGroup({
                      className: designSystem.classNames.formSurface.group({ group }),
                      group,
                      children: groupFields.map((field) => (
                        <Fragment key={field.inputField.Id}>
                          {renderInputFormField({
                            choiceToggleClassName,
                            choiceTone,
                            componentSystem,
                            dataSourceResolver,
                            designSystem,
                            fieldRenderers,
                            choiceValuesByFieldId,
                            inputField: field.inputField,
                            inputForm,
                            module,
                            queryField: field.queryField,
                            runtime,
                            target: formTarget,
                          })}
                        </Fragment>
                      )),
                    })}
                  </Fragment>
                )
              }),
            })}

            {hasActionsChromeSlot(view) ? null : renderInputFormActionRow({
              choiceValuesByFieldId,
              componentSet,
              componentSystem,
              designSystem,
              inputForm,
              module,
              placements: inputForm.Actions,
              runtime,
              target: formTarget,
            })}
          </>
        ),
      })}
    </ProjectedViewSurface>
  )
}

function createInputFormRuntimeDiagnostics({
  hasRuntime,
  inputForm,
  source,
  view,
}: {
  readonly hasRuntime: boolean
  readonly inputForm: InputFormDefinition | null
  readonly source: string
  readonly view: ViewDefinition
}): readonly PresentationProjectionDiagnostic[] {
  if (!inputForm || hasRuntime) {
    return []
  }

  return [
    createPresentationProjectionDiagnostic({
      category: 'missing-binding',
      id: `input-form.${inputForm.Id}.runtime.missing-binding`,
      interpretation: {
        status: 'unbound',
        target: 'input-form-runtime-binding',
      },
      message: `Input form '${inputForm.Name}' has no runtime binding.`,
      severity: 'warning',
      source,
      subject: {
        id: inputForm.Id,
        kind: 'input-form',
        name: inputForm.Name,
      },
      suggestedNextStep:
        `Bind input form '${inputForm.Id}' to a frontend runtime adapter or remove it from view '${view.Id}'.`,
    }),
  ]
}

function renderInputFormField<TValue extends object>({
  choiceToggleClassName,
  choiceTone,
  componentSystem,
  dataSourceResolver,
  designSystem,
  fieldRenderers,
  choiceValuesByFieldId,
  inputField,
  inputForm,
  module,
  queryField,
  runtime,
  target,
}: {
  readonly choiceToggleClassName?: ProjectedInputFormChoiceClassNameResolver
  readonly choiceTone?: ProjectedInputFormChoiceToneResolver
  readonly choiceValuesByFieldId: Readonly<Record<string, readonly string[]>>
  readonly componentSystem: PresentationComponentSystem
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly designSystem: PresentationDesignSystem
  readonly fieldRenderers?: Readonly<Record<string, ProjectedInputFormFieldRenderer>>
  readonly inputField: InputFormFieldDefinition
  readonly inputForm: InputFormDefinition
  readonly module: PresentationModuleDefinition
  readonly queryField: DataSourceQueryFieldDefinition | null
  readonly runtime: ProjectedInputFormRuntime<TValue>
  readonly target: ProjectedInputFormTargetContext
}) {
  const fieldId = queryField?.FieldId ?? inputField.FieldId
  const valuePath = queryField?.ValuePath ?? inputField.ValuePath
  const presentationField = findPresentationField<FieldPresentationDefinition>(module, fieldId) ?? null
  const value = readFormValue(runtime.value, valuePath)
  const choices = readInputFormChoices(dataSourceResolver, inputField, queryField, presentationField)
  const setFieldValue = (nextValue: unknown) => {
    runtime.setValue(
      (current) => setFormValue(current, valuePath, nextValue),
      {
        choiceValuesByFieldId,
        inputForm,
        target,
        value: runtime.value,
      },
    )
  }
  const renderOverride = findInputFieldRenderer(fieldRenderers, inputField, queryField)
  const fieldContext = {
    choiceToggleClassName,
    choiceTone,
    choices,
    componentSystem,
    designSystem,
    field: inputField,
    inputForm,
    presentationField,
    queryField,
    queryForm: target.queryForm ?? null,
    setFieldValue,
    stateId: target.stateId,
    target,
    value,
  } satisfies ProjectedInputFormFieldRenderContext
  const control = renderOverride
    ? renderOverride(fieldContext)
    : renderDefaultFieldControl({
        ...fieldContext,
        label: presentationField?.Label ?? queryField?.Name ?? inputField.Name,
      })
  const label = presentationField?.Label ?? queryField?.Name ?? inputField.Name

  return componentSystem.forms.InputFormField({
    className: designSystem.classNames.formSurface.field({}),
    control: componentSystem.forms.InputFormControlSlot({
      field: inputField,
      children: control,
    }),
    field: inputField,
    label: componentSystem.forms.FormFieldLabel({
      children: label,
    }),
  })
}

function renderDefaultFieldControl({
  choiceToggleClassName,
  choiceTone,
  choices,
  componentSystem,
  designSystem,
  field,
  inputForm,
  label,
  presentationField,
  queryField,
  queryForm,
  setFieldValue,
  stateId,
  target,
  value,
}: ProjectedInputFormFieldRenderContext & { readonly label: string }) {
  if (isMultiSelectInputField(field, queryField)) {
    const selected = readSelectedChoiceValues(field, queryField, choices, value)
    return choices.length > 0 ? (
      componentSystem.forms.ChoiceToggleGroup({
        'aria-label': label,
        onValueChange: setFieldValue,
        value: selected,
        children: choices.map((choice) => (
          <Fragment key={choice.value}>
            {componentSystem.forms.ChoiceToggleItem({
              'aria-label': choice.label,
              className: resolveChoiceToggleClassName({
                choice,
                choiceToggleClassName,
                choiceTone,
                field,
                inputForm,
                presentationField,
                queryField,
                queryForm,
                stateId,
                target,
              }),
              value: choice.value,
              children: choice.label,
            })}
          </Fragment>
        )),
      })
    ) : (
      componentSystem.forms.InputFormFieldMessage({
        field,
        children: `No ${label.toLocaleLowerCase()} choices are available.`,
      })
    )
  }

  if (isInputFormFieldKind(field, 'select', 'Select')) {
    return componentSystem.forms.SelectControl({
      'aria-label': label,
      onValueChange: setFieldValue,
      options: [
        { label: 'Any', value: '' },
        ...choices.map((choice) => ({
          label: choice.label,
          value: choice.value,
        })),
      ],
      value: typeof value === 'string' ? value : '',
    })
  }

  if (isInputFormFieldKind(field, 'dateTimeRange', 'DateTimeRange')) {
    if (isDateTimeFilterControl(field)) {
      return componentSystem.forms.DateTimeFilterControl({
        emptyLabel: field.Display?.EmptyValueLabel ?? undefined,
        incrementMinutes: field.Display?.IncrementMinutes ?? undefined,
        label,
        onValueChange: setFieldValue,
        showTimezone: field.Display?.ShowTimezone ?? undefined,
        value: coerceDateTimeFilterValue(value),
      })
    }

    const range = value && typeof value === 'object'
      ? value as Record<string, unknown>
      : {}
    return componentSystem.forms.InputFormControlGroup({
      className: designSystem.classNames.formSurface.rangeGroup({}),
      field,
      children: (
        <>
        {componentSystem.forms.TextInputControl({
          'aria-label': `${label} after`,
          onValueChange: (nextValue) =>
            setFieldValue({ ...range, after: localDateTimeToIso(nextValue) }),
          type: 'datetime-local',
          value: isoToLocalDateTime(readOptionalString(range.after)),
        })}
        {componentSystem.forms.TextInputControl({
          'aria-label': `${label} before`,
          onValueChange: (nextValue) =>
            setFieldValue({ ...range, before: localDateTimeToIso(nextValue) }),
          type: 'datetime-local',
          value: isoToLocalDateTime(readOptionalString(range.before)),
        })}
        </>
      ),
    })
  }

  if (isInputFormFieldKind(field, 'dateTime', 'DateTime')) {
    return componentSystem.forms.TextInputControl({
      'aria-label': label,
      onValueChange: (nextValue) => setFieldValue(localDateTimeToIso(nextValue)),
      type: 'datetime-local',
      value: isoToLocalDateTime(readOptionalString(value)),
    })
  }

  if (isInputFormFieldKind(field, 'date', 'Date')) {
    return componentSystem.forms.TextInputControl({
      'aria-label': label,
      onValueChange: (nextValue) => setFieldValue(nextValue || null),
      type: 'date',
      value: typeof value === 'string' ? value.slice(0, 10) : '',
    })
  }

  if (isInputFormFieldKind(field, 'number', 'Number')) {
    return componentSystem.forms.TextInputControl({
      'aria-label': label,
      onValueChange: (nextValue) =>
        setFieldValue(nextValue === '' ? null : Number.parseFloat(nextValue)),
      type: 'number',
      value: typeof value === 'number' && Number.isFinite(value) ? String(value) : '',
    })
  }

  if (isInputFormFieldKind(field, 'numberRange', 'NumberRange')) {
    const range = value && typeof value === 'object'
      ? value as Record<string, unknown>
      : {}
    return componentSystem.forms.InputFormControlGroup({
      className: designSystem.classNames.formSurface.rangeGroup({}),
      field,
      children: (
        <>
        {componentSystem.forms.TextInputControl({
          'aria-label': `${label} minimum`,
          onValueChange: (nextValue) =>
            setFieldValue({ ...range, minimum: readOptionalNumber(nextValue) }),
          type: 'number',
          value: typeof range.minimum === 'number' ? String(range.minimum) : '',
        })}
        {componentSystem.forms.TextInputControl({
          'aria-label': `${label} maximum`,
          onValueChange: (nextValue) =>
            setFieldValue({ ...range, maximum: readOptionalNumber(nextValue) }),
          type: 'number',
          value: typeof range.maximum === 'number' ? String(range.maximum) : '',
        })}
        </>
      ),
    })
  }

  if (isInputFormFieldKind(field, 'boolean', 'Boolean')) {
    return componentSystem.forms.CheckboxControl({
      'aria-label': label,
      checked: value === true,
      onCheckedChange: setFieldValue,
    })
  }

  return componentSystem.forms.TextInputControl({
    'aria-label': label,
    onValueChange: setFieldValue,
    placeholder: field.Placeholder ?? undefined,
    value: typeof value === 'string' ? value : '',
  })
}

function renderInputFormActionRow<TValue extends object>({
  choiceValuesByFieldId,
  componentSet,
  componentSystem,
  designSystem,
  inputForm,
  module,
  placements,
  runtime,
  target,
}: {
  readonly choiceValuesByFieldId: Readonly<Record<string, readonly string[]>>
  readonly componentSet: string
  readonly componentSystem: PresentationComponentSystem
  readonly designSystem: PresentationDesignSystem
  readonly inputForm: InputFormDefinition
  readonly module: PresentationModuleDefinition
  readonly placements: readonly ActionPlacementDefinition[]
  readonly runtime: ProjectedInputFormRuntime<TValue>
  readonly target: ProjectedInputFormTargetContext
}) {
  if (placements.length === 0) {
    return null
  }

  return componentSystem.forms.InputFormActionRow({
    className: designSystem.classNames.formSurface.actionRow({}),
    form: inputForm,
    children: placements.map((placement) => (
      <Fragment key={`${placement.Region}:${placement.ActionId}`}>
        {renderInputFormAction({
          choiceValuesByFieldId,
          componentSet,
          componentSystem,
          inputForm,
          module,
          placement,
          runtime,
          target,
        })}
      </Fragment>
    )),
  })
}

function renderInputFormAction<TValue extends object>({
  choiceValuesByFieldId,
  componentSystem,
  componentSet,
  inputForm,
  module,
  placement,
  runtime,
  target,
}: {
  readonly choiceValuesByFieldId: Readonly<Record<string, readonly string[]>>
  readonly componentSet: string
  readonly componentSystem: PresentationComponentSystem
  readonly inputForm: InputFormDefinition
  readonly module: PresentationModuleDefinition
  readonly placement: ActionPlacementDefinition
  readonly runtime: ProjectedInputFormRuntime<TValue>
  readonly target: ProjectedInputFormTargetContext
}) {
  const action = findPresentationAction(module, placement.ActionId)
  const label = placement.Label ?? action?.Name ?? placement.ActionId
  return componentSystem.forms.FormActionButton({
    'data-presentation-action-id': placement.ActionId,
    'data-presentation-view-id': target.queryForm?.Id ?? inputForm.Id,
    onClick: () =>
      runtime.invokeAction({
        action,
        choiceValuesByFieldId,
        inputForm,
        placement,
        target,
        value: runtime.value,
      }),
    size: 'sm',
    type: 'button',
    variant: placement.Intent === 'primary' ? 'default' : 'outline',
    children: (
      <>
        {renderPresentationIcon({
          componentSet,
          icon: placement.Icon,
          module,
          subject: placement,
        })}
        {label}
      </>
    ),
  })
}

function hasActionsChromeSlot(view: ViewDefinition) {
  return view.Chrome?.Slots.some((slot) =>
    isViewChromeSlotKind(slot, viewChromeSlotKinds.actions)) ?? false
}

function resolveInputFormActionIconPlacements(
  inputForm: InputFormDefinition | null,
  view: ViewDefinition,
) {
  if (!inputForm) {
    return []
  }

  const actionSlots = view.Chrome?.Slots.filter((slot) =>
    isViewChromeSlotKind(slot, viewChromeSlotKinds.actions)) ?? []
  if (actionSlots.length === 0) {
    return inputForm.Actions
  }

  return actionSlots.flatMap((slot) =>
    slot.Actions.length > 0 ? slot.Actions : inputForm.Actions)
}

function createDefaultInputFormTarget(inputForm: InputFormDefinition): ProjectedInputFormTargetContext {
  return {
    queryDefinition: null,
    queryForm: null,
    stateId: inputForm.SharedStateId ?? inputForm.Target.Id ?? inputForm.StateDataSourceId,
  }
}

function projectInputFormFields(
  inputForm: InputFormDefinition,
  query: DataSourceQueryDefinition | null,
): ProjectedInputFormFieldProjection[] {
  if (!query) {
    return inputForm.Fields.map((field): ProjectedInputFormFieldProjection => ({
      inputField: field,
      queryField: null,
    }))
  }

  const queryFieldsById = new Map(query.Fields.map((field) => [field.Id, field]))
  const queryFieldsByFieldId = new Map(
    query.Fields.flatMap((field) => field.FieldId ? [[field.FieldId, field]] : []),
  )
  const queryFieldsByValuePath = new Map(
    query.Fields.flatMap((field) => field.ValuePath ? [[field.ValuePath, field]] : []),
  )

  return inputForm.Fields.map((field): ProjectedInputFormFieldProjection => {
    const queryField =
      queryFieldsById.get(field.Id) ??
      queryFieldsByFieldId.get(field.FieldId) ??
      queryFieldsByValuePath.get(field.ValuePath)
    return {
      inputField: field,
      queryField: queryField ?? null,
    }
  })
}

function findInputFieldRenderer(
  renderers: Readonly<Record<string, ProjectedInputFormFieldRenderer>> | undefined,
  field: InputFormFieldDefinition,
  queryField: DataSourceQueryFieldDefinition | null,
) {
  if (!renderers) {
    return undefined
  }

  return (
    (queryField?.Id ? renderers[queryField.Id] : undefined) ??
    renderers[field.Id] ??
    (queryField?.FieldId ? renderers[queryField.FieldId] : undefined) ??
    renderers[field.FieldId] ??
    (queryField?.ValuePath ? renderers[queryField.ValuePath] : undefined) ??
    renderers[field.ValuePath]
  )
}

function readInputFormChoices(
  dataSourceResolver: PresentationDataSourceResolver,
  field: InputFormFieldDefinition,
  queryField: DataSourceQueryFieldDefinition | null,
  presentationField: FieldPresentationDefinition | null,
): readonly ProjectedInputFormChoice[] {
  if (queryField?.ChoiceDataSourceId) {
    const value = dataSourceResolver.readPath(
      queryField.ChoiceDataSourceId,
      queryField.ChoiceItemsPath,
    )
    return readChoiceItems(value, {
      labelPath: 'Label',
      presentationField,
      tonePath: 'Tone',
      valuePath: 'Id',
    })
  }

  const source = field.ChoiceSource
  if (!source) {
    return []
  }

  const sourcePath = source.CollectionPath ?? source.FactsPath
  const value = source.DataSourceId
    ? dataSourceResolver.readPath(source.DataSourceId, sourcePath)
    : []
  if (!Array.isArray(value)) {
    return []
  }

  return readChoiceItems(value, {
    labelPath: source.LabelPath,
    presentationField,
    tonePath: source.TonePath,
    valuePath: source.ValuePath,
  })
}

function readChoiceItems(
  value: unknown,
  paths: {
    readonly labelPath: string
    readonly presentationField: FieldPresentationDefinition | null
    readonly tonePath?: string | null
    readonly valuePath: string
  },
): readonly ProjectedInputFormChoice[] {
  if (!Array.isArray(value)) {
    return []
  }

  return value.flatMap((item): ProjectedInputFormChoice[] => {
    const choiceValue = readObjectPath(item, paths.valuePath)
    if (choiceValue === null || choiceValue === undefined || choiceValue === '') {
      return []
    }

    const label = readObjectPath(item, paths.labelPath)
    const tone = paths.tonePath
      ? readObjectPath(item, paths.tonePath)
      : null
    const resolvedLabel = label === null || label === undefined || label === ''
      ? resolvePresentationFieldValueLabel(paths.presentationField, choiceValue)
      : String(label)
    return [{
      label: resolvedLabel ?? String(choiceValue),
      tone: tone === null || tone === undefined
        ? resolvePresentationFieldValueTone(paths.presentationField, choiceValue)
        : String(tone),
      value: String(choiceValue),
    }]
  })
}

function readSelectedChoiceValues(
  field: InputFormFieldDefinition,
  queryField: DataSourceQueryFieldDefinition | null,
  choices: readonly ProjectedInputFormChoice[],
  value: unknown,
) {
  if (Array.isArray(value) && value.length > 0) {
    return value.map(String)
  }

  if (isInputFormChoiceDefaultSelection(field, 'all', 'All')) {
    return choices.map((choice) => choice.value)
  }

  if (isInputFormChoiceDefaultSelection(field, 'first', 'First')) {
    return choices[0] ? [choices[0].value] : []
  }

  if (queryField?.ChoiceDataSourceId && isMultiSelectInputField(field, queryField)) {
    return choices.map((choice) => choice.value)
  }

  return []
}

function resolveChoiceToggleClassName({
  choice,
  choiceToggleClassName,
  choiceTone,
  field,
  inputForm,
  presentationField,
  queryField,
  queryForm,
  stateId,
  target,
}: {
  readonly choice: ProjectedInputFormChoice
  readonly choiceToggleClassName?: ProjectedInputFormChoiceClassNameResolver
  readonly choiceTone?: ProjectedInputFormChoiceToneResolver
  readonly field: InputFormFieldDefinition
  readonly inputForm: InputFormDefinition
  readonly presentationField: FieldPresentationDefinition | null
  readonly queryField: DataSourceQueryFieldDefinition | null
  readonly queryForm: QueryFormDefinition | null
  readonly stateId: string
  readonly target: ProjectedInputFormTargetContext
}) {
  const choiceContext = {
    choice,
    field,
    inputForm,
    presentationField,
    queryField,
    queryForm,
    stateId,
    target,
  } satisfies ProjectedInputFormChoiceRenderContext
  const tone =
    choiceTone?.(choiceContext) ??
    choice.tone ??
    resolvePresentationFieldValueTone(presentationField, choice.value) ??
    null
  return choiceToggleClassName?.({ ...choiceContext, tone }) ??
    getDefaultChoiceClassName(tone)
}

function readFormValue(value: object, path: string) {
  return readObjectPath(value, path)
}

function setFormValue<TValue extends object>(
  value: TValue,
  path: string,
  nextValue: unknown,
): TValue {
  const segments = path.split('.').filter(Boolean)
  if (segments.length === 0) {
    return value
  }

  return setObjectPath(value, segments, nextValue) as TValue
}

function setObjectPath(value: unknown, segments: readonly string[], nextValue: unknown): unknown {
  const [head, ...tail] = segments
  if (!head) {
    return nextValue
  }

  const record = value && typeof value === 'object'
    ? value as Record<string, unknown>
    : {}

  return {
    ...record,
    [head]: tail.length === 0
      ? nextValue
      : setObjectPath(record[head], tail, nextValue),
  }
}

function isMultiSelectInputField(
  field: InputFormFieldDefinition,
  queryField: DataSourceQueryFieldDefinition | null,
) {
  return isInputFormFieldKind(field, 'multiSelect', 'MultiSelect') ||
    queryField?.Operators.some((operator) => operator.toLocaleLowerCase() === 'in') === true
}

function isInputFormFieldKind(
  field: InputFormFieldDefinition,
  kind: keyof typeof inputFormFieldKinds,
  label: string,
) {
  return matchesPresentationEnum(
    field.Kind,
    createPresentationEnumDiscriminator(inputFormFieldKinds, kind, label),
  )
}

function isInputFormChoiceDefaultSelection(
  field: InputFormFieldDefinition,
  selection: keyof typeof inputFormChoiceDefaultSelections,
  label: string,
) {
  const defaultSelection = field.ChoiceSource?.DefaultSelection
  return defaultSelection !== null &&
    defaultSelection !== undefined &&
    matchesPresentationEnum(
      defaultSelection,
      createPresentationEnumDiscriminator(inputFormChoiceDefaultSelections, selection, label),
    )
}

function getDefaultChoiceClassName(tone: string | null) {
  if (tone === 'success') {
    return 'data-[state=off]:border-teal-700/15 data-[state=off]:bg-white data-[state=off]:text-slate-500 data-[state=on]:border-teal-700/15 data-[state=on]:bg-teal-50 data-[state=on]:text-teal-700'
  }

  if (tone === 'danger') {
    return 'data-[state=off]:border-red-700/15 data-[state=off]:bg-white data-[state=off]:text-slate-500 data-[state=on]:border-red-700/15 data-[state=on]:bg-red-50 data-[state=on]:text-red-700'
  }

  if (tone === 'warning') {
    return 'data-[state=off]:border-amber-700/15 data-[state=off]:bg-white data-[state=off]:text-slate-500 data-[state=on]:border-amber-700/15 data-[state=on]:bg-amber-50 data-[state=on]:text-amber-700'
  }

  if (tone === 'accent') {
    return 'data-[state=off]:border-fuchsia-700/15 data-[state=off]:bg-white data-[state=off]:text-slate-500 data-[state=on]:border-fuchsia-700/15 data-[state=on]:bg-fuchsia-50 data-[state=on]:text-fuchsia-700'
  }

  return 'data-[state=off]:border-sky-700/15 data-[state=off]:bg-white data-[state=off]:text-slate-500 data-[state=on]:border-sky-700/15 data-[state=on]:bg-sky-50 data-[state=on]:text-sky-700'
}

function isDateTimeFilterControl(field: InputFormFieldDefinition) {
  const control = field.Display?.Control
  return control === inputFormFieldControlKinds.dateTimeFilter ||
    control?.toString().toLocaleLowerCase() === 'datetimefilter'
}

function coerceDateTimeFilterValue(value: unknown): DateTimeFilterValue {
  const fallback = createDefaultDateTimeFilter()
  if (!value || typeof value !== 'object') {
    return fallback
  }

  const record = value as Record<string, unknown>
  return normalizeDateTimeFilter({
    afterLocal: readOptionalString(record.afterLocal) || fallback.afterLocal,
    beforeLocal: readOptionalString(record.beforeLocal) || fallback.beforeLocal,
    mode: record.mode === 'range' ? 'range' : 'preset',
    preset: readOptionalString(record.preset) as DateTimeFilterValue['preset'],
    timezone: readOptionalString(record.timezone) || fallback.timezone,
  })
}

function readOptionalString(value: unknown) {
  return typeof value === 'string' ? value : ''
}

function readOptionalNumber(value: string) {
  if (!value) {
    return null
  }

  const numberValue = Number(value)
  return Number.isFinite(numberValue) ? numberValue : null
}

function isoToLocalDateTime(value: string) {
  if (!value) {
    return ''
  }

  const parsed = new Date(value)
  if (Number.isNaN(parsed.getTime())) {
    return ''
  }

  const year = parsed.getFullYear()
  const month = `${parsed.getMonth() + 1}`.padStart(2, '0')
  const day = `${parsed.getDate()}`.padStart(2, '0')
  const hours = `${parsed.getHours()}`.padStart(2, '0')
  const minutes = `${parsed.getMinutes()}`.padStart(2, '0')
  return `${year}-${month}-${day}T${hours}:${minutes}`
}

function localDateTimeToIso(value: string) {
  if (!value) {
    return null
  }

  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString()
}

function cn(...values: readonly (false | null | string | undefined)[]) {
  return values.filter(Boolean).join(' ')
}
