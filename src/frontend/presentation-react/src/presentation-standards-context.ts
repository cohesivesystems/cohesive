import {
  createContext,
  useContext,
  type Context,
} from 'react'

export function createPresentationStandardsContext<TStandards>() {
  return createContext<TStandards | null>(null)
}

export function usePresentationStandards<TStandards>(
  context: Context<TStandards | null>,
  label = 'Presentation standards',
) {
  const standards = useContext(context)
  if (!standards) {
    throw new Error(`${label} are not available.`)
  }

  return standards
}
