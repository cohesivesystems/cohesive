export type DeepReadonly<T> =
  T extends (...args: never[]) => unknown
    ? T
    : T extends readonly (infer TItem)[]
      ? readonly DeepReadonly<TItem>[]
      : T extends object
        ? { readonly [TKey in keyof T]: DeepReadonly<T[TKey]> }
        : T

export function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export function compareOrdinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0
}

export function cloneWireValue<T>(value: T): DeepReadonly<T> {
  return cloneWireValueCore(value, new WeakMap()) as DeepReadonly<T>
}

function cloneWireValueCore(value: unknown, seen: WeakMap<object, unknown>): unknown {
  if (typeof value !== 'object' || value === null) {
    return value
  }
  const existing = seen.get(value)
  if (existing !== undefined) {
    return existing
  }
  if (Array.isArray(value)) {
    const clone: unknown[] = []
    seen.set(value, clone)
    for (const item of value) {
      clone.push(cloneWireValueCore(item, seen))
    }
    return Object.freeze(clone)
  }

  const clone: Record<string, unknown> = {}
  seen.set(value, clone)
  for (const [key, item] of Object.entries(value)) {
    clone[key] = cloneWireValueCore(item, seen)
  }
  return Object.freeze(clone)
}

export function deepFreeze<T>(value: T): T {
  if (typeof value !== 'object' || value === null || Object.isFrozen(value)) {
    return value
  }
  for (const nested of Object.values(value)) {
    deepFreeze(nested)
  }
  return Object.freeze(value)
}
