/**
 * Frontend realization strategies for semantic presentation data sources.
 * These are projection/runtime binding kinds, not backend IR data-source kinds.
 */
export const presentationDataSourceBindingKinds = {
  /**
   * Realizes a data source through TanStack Query so React owns fetch lifecycle,
   * caching, pending/error state, and refetch behavior.
   */
  tanstackQuery: 'tanstack-query',
  /**
   * Realizes a data source from already-available local/runtime data without
   * invoking a query adapter.
   */
  localValue: 'local-value',
} as const

/**
 * Declares whether a presentation data source can resolve in the current
 * authorization context.
 */
export type PresentationDataSourceAuthorizationRequirement =
  | { readonly kind: 'none' }
  | {
      readonly blockedLabel?: string
      readonly isAuthorized: boolean
      readonly kind: 'required'
    }

/**
 * Small constructors for keeping data source authorization declarations explicit
 * at the binding site.
 */
export const presentationDataSourceAuthorization = {
  none: (): PresentationDataSourceAuthorizationRequirement => ({ kind: 'none' }),
  required: ({
    blockedLabel,
    isAuthorized,
  }: {
    readonly blockedLabel?: string
    readonly isAuthorized: boolean
  }): PresentationDataSourceAuthorizationRequirement => ({
    blockedLabel,
    isAuthorized,
    kind: 'required',
  }),
}

/**
 * Declarative binding from a semantic presentation data source id to a concrete
 * frontend source. Query bindings currently use TanStack Query.
 */
export type PresentationDataSourceBinding =
  | PresentationLocalValueDataSourceBinding
  | PresentationTanStackQueryDataSourceBinding

export interface PresentationDataSourceBindingBase {
  readonly authorization: PresentationDataSourceAuthorizationRequirement
  readonly blockedLabel?: string
  readonly dataSourceId: string
  readonly emptyMessage?: string
  readonly pendingLabel?: string
}

export interface PresentationLocalValueDataSourceBinding
  extends PresentationDataSourceBindingBase {
  readonly data: unknown
  readonly error?: unknown
  readonly isFetching?: boolean
  readonly isPending?: boolean
  readonly kind: typeof presentationDataSourceBindingKinds.localValue
  readonly refetch?: () => Promise<unknown> | unknown
}

export interface PresentationTanStackQueryDataSourceBinding
  extends PresentationDataSourceBindingBase {
  readonly enabled?: boolean
  readonly fallbackData?: unknown
  readonly kind: typeof presentationDataSourceBindingKinds.tanstackQuery
  readonly queryFn: () => Promise<unknown>
  readonly queryKey: readonly unknown[]
  readonly refetchInterval?: number | false
  readonly retry?: boolean | number
  readonly staleTime?: number
}

export type PresentationLocalValueDataSourceBindingInput = Omit<
  PresentationLocalValueDataSourceBinding,
  'kind'
>

export type PresentationTanStackQueryDataSourceBindingInput = Omit<
  PresentationTanStackQueryDataSourceBinding,
  'kind'
>

export const presentationDataSourceBindings = {
  localValue: (
    binding: PresentationLocalValueDataSourceBindingInput,
  ): PresentationLocalValueDataSourceBinding => ({
    ...binding,
    kind: presentationDataSourceBindingKinds.localValue,
  }),
  tanstackQuery: (
    binding: PresentationTanStackQueryDataSourceBindingInput,
  ): PresentationTanStackQueryDataSourceBinding => ({
    ...binding,
    kind: presentationDataSourceBindingKinds.tanstackQuery,
  }),
}

export interface DataSourceAuthorizationState {
  readonly blockedLabel?: string
  readonly isBlocked: boolean
}

export function resolveDataSourceAuthorization(
  binding: PresentationDataSourceBindingBase,
): DataSourceAuthorizationState {
  if (binding.authorization.kind === 'required' && !binding.authorization.isAuthorized) {
    return {
      blockedLabel: binding.authorization.blockedLabel ?? binding.blockedLabel,
      isBlocked: true,
    }
  }

  return {
    blockedLabel:
      binding.authorization.kind === 'required'
        ? binding.authorization.blockedLabel ?? binding.blockedLabel
        : binding.blockedLabel,
    isBlocked: false,
  }
}

export function isTanStackQueryBinding(
  binding: PresentationDataSourceBinding,
): binding is PresentationTanStackQueryDataSourceBinding {
  return binding.kind === presentationDataSourceBindingKinds.tanstackQuery
}
