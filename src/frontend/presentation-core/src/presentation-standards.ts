export type PresentationStandardsComposer<TStandards, TContribution> = (
  ...contributions: readonly TContribution[]
) => TStandards

/**
 * Creates a typed standards contribution without coupling the contribution to a
 * particular app shell. Folder-local feature modules can export these and the
 * app composition layer decides how to merge them.
 */
export function definePresentationStandardsContribution<TContribution>(
  contribution: TContribution,
) {
  return contribution
}
