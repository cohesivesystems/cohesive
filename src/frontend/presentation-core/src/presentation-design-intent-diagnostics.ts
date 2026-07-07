import {
  createPresentationProjectionDiagnostic,
  type PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'
import type {
  DesignIntent,
} from '@cohesive/presentation-contracts'

export const presentationDesignIntentFieldNames = [
  'Role',
  'Variant',
  'Tone',
  'Density',
  'Size',
  'Layout',
] as const

export type PresentationDesignIntentFieldName =
  (typeof presentationDesignIntentFieldNames)[number]

export interface ProjectPresentationDesignIntentDiagnosticsOptions {
  readonly design: DesignIntent | null | undefined
  readonly ignoredFields: readonly PresentationDesignIntentFieldName[]
  readonly interpretedFields: readonly PresentationDesignIntentFieldName[]
  readonly message: string
  readonly semanticInputs?: readonly string[]
  readonly source: string
  readonly subject: {
    readonly id: string
    readonly kind: string
    readonly name?: string | null
  }
  readonly suggestedNextStep?: string
  readonly target: string
}

/**
 * Reports which design-intent fields a frontend interpreter currently consumes
 * and which declared fields remain TODOs for the active target.
 */
export function projectPresentationDesignIntentDiagnostics({
  design,
  ignoredFields,
  interpretedFields,
  message,
  semanticInputs = [],
  source,
  subject,
  suggestedNextStep =
    'Extend the design interpreter for these fields or remove the unused design intent.',
  target,
}: ProjectPresentationDesignIntentDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  if (!design) {
    return []
  }

  const declaredFields = presentationDesignIntentFieldNames.filter((field) =>
    isPresentationDesignIntentFieldDeclared(design, field))
  const declaredIgnoredFields = ignoredFields.filter((field) =>
    declaredFields.includes(field))
  if (declaredIgnoredFields.length === 0) {
    return []
  }

  const declaredInterpretedFields = interpretedFields.filter((field) =>
    declaredFields.includes(field))

  return [
    createPresentationProjectionDiagnostic({
      category: 'incomplete-projection',
      details: {
        ignoredFields: declaredIgnoredFields.map((field) => `Design.${field}`),
        ignoredValues: Object.fromEntries(
          declaredIgnoredFields.map((field) => [
            field,
            design[field],
          ]),
        ),
        interpretedFields: [
          ...semanticInputs,
          ...declaredInterpretedFields.map((field) => `Design.${field}`),
        ],
      },
      id: `presentation-design.${subject.id}.design-intent.partial`,
      interpretation: {
        status: 'projected',
        target,
      },
      message,
      severity: 'info',
      source,
      subject,
      suggestedNextStep,
    }),
  ]
}

function isPresentationDesignIntentFieldDeclared(
  design: DesignIntent,
  field: PresentationDesignIntentFieldName,
) {
  const value = design[field]
  return value !== null && value !== undefined && value !== ''
}
