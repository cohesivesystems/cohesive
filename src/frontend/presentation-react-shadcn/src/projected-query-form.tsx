import {
  createPresentationProjectionDiagnostic,
  createRelationQueryInputFormTarget,
  findPresentationInputForm,
  type PresentationProjectionDiagnostic,
  type PresentationModuleDefinition,
  type PresentationDataSourceResolver,
  type ProjectedInputFormRuntime,
  type ProjectedInputFormValueChangeContext,
  type ProjectedQueryFormActionContext,
  type ProjectedQueryFormRuntime,
  type ProjectedQueryFormValueChangeContext,
  type QueryFormDefinition,
  type ViewDefinition,
} from '@cohesivesystems/presentation-core'
import { useMemo } from 'react'
import { useRegisterPresentationProjectionDiagnostics } from '@cohesivesystems/presentation-react'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type { PresentationDesignSystem } from '@cohesivesystems/presentation-tailwind'
import {
  ProjectedInputForm,
  type ProjectedInputFormChoice,
  type ProjectedInputFormChoiceClassNameContext,
  type ProjectedInputFormChoiceClassNameResolver,
  type ProjectedInputFormChoiceRenderContext,
  type ProjectedInputFormChoiceToneResolver,
  type ProjectedInputFormFieldRenderer,
  type ProjectedInputFormFieldRenderContext,
} from './projected-input-form'
import { ProjectedStatusBlock } from './projected-activity-state'
import type {
  ProjectedViewSurfaceChromeSlotRenderer,
} from './projected-view-surface'

export type {
  ProjectedInputFormChoice as ProjectedQueryFormChoice,
  ProjectedInputFormChoiceClassNameContext as ProjectedQueryFormChoiceClassNameContext,
  ProjectedInputFormChoiceClassNameResolver as ProjectedQueryFormChoiceClassNameResolver,
  ProjectedInputFormChoiceRenderContext as ProjectedQueryFormChoiceRenderContext,
  ProjectedInputFormChoiceToneResolver as ProjectedQueryFormChoiceToneResolver,
  ProjectedInputFormFieldRenderer as ProjectedQueryFormFieldRenderer,
  ProjectedInputFormFieldRenderContext as ProjectedQueryFormFieldRenderContext,
}

export type {
  ProjectedQueryFormActionContext,
  ProjectedQueryFormRuntime,
  ProjectedQueryFormValueChangeContext,
} from '@cohesivesystems/presentation-core'

/**
 * Props required to project a backend-declared query form into React controls.
 *
 * @typeParam TValue - Shape of the caller-owned query state object.
 */
export interface ProjectedQueryFormProps<TValue extends object = object> {
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

  /** Presentation module that owns the query form, input form, fields, and actions. */
  readonly module: PresentationModuleDefinition

  /** Query form to project; null renders a diagnostic status block. */
  readonly queryForm: QueryFormDefinition | null

  /** Runtime state and action handlers; null renders a diagnostic status block. */
  readonly runtime: ProjectedQueryFormRuntime<TValue> | null

  /** Interprets non-form view chrome slots declared by the hosting view. */
  readonly renderChromeSlot?: ProjectedViewSurfaceChromeSlotRenderer

  /** View that hosts the query form and supplies surface identity. */
  readonly view: ViewDefinition
}

/**
 * Projects a relation-query specialization by delegating the interaction surface
 * to the generic input-form renderer.
 */
export function ProjectedQueryForm<TValue extends object = object>({
  choiceToggleClassName,
  choiceTone,
  className,
  chromeAfterContentClassName,
  chromeBeforeContentClassName,
  chromeFooterClassName,
  chromeHeaderClassName,
  componentSet,
  componentSystem,
  contentClassName,
  dataSourceResolver,
  designSystem,
  fieldRenderers,
  module,
  queryForm,
  renderChromeSlot,
  runtime,
  view,
}: ProjectedQueryFormProps<TValue>) {
  const inputForm = queryForm
    ? findPresentationInputForm(module, queryForm.FormId)
    : null
  const bindingDiagnosticsSource =
    `projected-query-form-binding:${view.Id}:${queryForm?.Id ?? 'missing'}`
  const bindingDiagnostics = useMemo(
    () => createQueryFormBindingDiagnostics({
      inputForm,
      queryForm,
      source: bindingDiagnosticsSource,
      view,
    }),
    [bindingDiagnosticsSource, inputForm, queryForm, view],
  )
  useRegisterPresentationProjectionDiagnostics(
    bindingDiagnosticsSource,
    bindingDiagnostics,
  )

  if (!queryForm) {
    return <ProjectedStatusBlock label={`Presentation view '${view.Name}' has no query form binding.`} />
  }

  if (!inputForm) {
    return null
  }

  if (!runtime) {
    return <ProjectedStatusBlock label={`Query form '${inputForm.Name}' has no runtime binding.`} />
  }

  return (
    <ProjectedInputForm
      choiceToggleClassName={choiceToggleClassName}
      choiceTone={choiceTone}
      className={className}
      chromeAfterContentClassName={chromeAfterContentClassName}
      chromeBeforeContentClassName={chromeBeforeContentClassName}
      chromeFooterClassName={chromeFooterClassName}
      chromeHeaderClassName={chromeHeaderClassName}
      componentSystem={componentSystem}
      componentSet={componentSet}
      contentClassName={contentClassName}
      dataSourceResolver={dataSourceResolver}
      designSystem={designSystem}
      fieldRenderers={fieldRenderers}
      inputForm={inputForm}
      module={module}
      renderChromeSlot={renderChromeSlot}
      runtime={createQueryInputFormRuntime(runtime, queryForm)}
      target={createRelationQueryInputFormTarget({
        dataSourceResolver,
        inputForm,
        queryForm,
        view,
      })}
      view={view}
    />
  )
}

function createQueryFormBindingDiagnostics({
  inputForm,
  queryForm,
  source,
  view,
}: {
  readonly inputForm: ReturnType<typeof findPresentationInputForm>
  readonly queryForm: QueryFormDefinition | null
  readonly source: string
  readonly view: ViewDefinition
}): readonly PresentationProjectionDiagnostic[] {
  if (!queryForm || inputForm) {
    return []
  }

  return [
    createPresentationProjectionDiagnostic({
      category: 'missing-definition',
      details: {
        formId: queryForm.FormId,
        queryFormId: queryForm.Id,
        viewId: view.Id,
      },
      id: `query-form.${queryForm.Id}.input-form.${queryForm.FormId}.missing-definition`,
      interpretation: {
        status: 'unbound',
        target: 'query-form-input-form-definition',
      },
      message: `Query form '${queryForm.Id}' has no input form '${queryForm.FormId}'.`,
      severity: 'error',
      source,
      subject: {
        id: queryForm.Id,
        kind: 'query-form',
        name: queryForm.Id,
      },
      suggestedNextStep:
        `Define input form '${queryForm.FormId}' or update query form '${queryForm.Id}' to reference an existing input form.`,
    }),
  ]
}

function createQueryInputFormRuntime<TValue extends object>(
  runtime: ProjectedQueryFormRuntime<TValue>,
  queryForm: QueryFormDefinition,
): ProjectedInputFormRuntime<TValue> {
  return {
    invokeAction: ({ choiceValuesByFieldId, inputForm, placement, target, value }) => {
      const queryContext = {
        choiceValuesByFieldId,
        inputForm,
        queryForm,
        stateId: target.stateId,
        value,
      } satisfies ProjectedQueryFormActionContext<TValue>

      if (isResetAction(placement.ActionId)) {
        runtime.reset(queryContext)
        return
      }

      runtime.apply(queryContext)
    },
    setValue: (update, context) =>
      runtime.setValue(
        update,
        context ? createQueryFormValueChangeContext(queryForm, context) : undefined,
      ),
    value: runtime.value,
  }
}

function createQueryFormValueChangeContext<TValue extends object>(
  queryForm: QueryFormDefinition,
  context: ProjectedInputFormValueChangeContext<TValue>,
): ProjectedQueryFormValueChangeContext<TValue> {
  return {
    choiceValuesByFieldId: context.choiceValuesByFieldId,
    inputForm: context.inputForm,
    queryForm,
    stateId: context.target.stateId,
    value: context.value,
  }
}

function isResetAction(actionId: string) {
  return actionId.toLocaleLowerCase().includes('reset')
}
