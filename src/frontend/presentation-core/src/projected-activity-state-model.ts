export type ProjectedActivityState =
  | { readonly kind: 'blocked'; readonly label: string }
  | { readonly kind: 'pending'; readonly label: string }
  | { readonly kind: 'error'; readonly label: string }
  | { readonly kind: 'ready' }

export function createQueryActivityState({
  blockedLabel,
  error,
  isBlocked,
  isPending,
  pendingLabel,
}: {
  readonly blockedLabel: string
  readonly error: unknown
  readonly isBlocked: boolean
  readonly isPending: boolean
  readonly pendingLabel: string
}): ProjectedActivityState {
  if (isBlocked) {
    return { kind: 'blocked', label: blockedLabel }
  }

  if (isPending) {
    return { kind: 'pending', label: pendingLabel }
  }

  if (error) {
    return { kind: 'error', label: getErrorMessage(error) }
  }

  return { kind: 'ready' }
}

export function getErrorMessage(error: unknown) {
  if (error instanceof Error) {
    const displayMessage = readErrorDisplayMessage(error)
    if (displayMessage) {
      return displayMessage
    }

    const apiMessage = getApiErrorMessage(error)
    if (apiMessage) {
      return `${error.message}: ${apiMessage}`
    }

    return error.message
  }

  return String(error)
}

export function getApiErrorMessage(error: Error) {
  const responseBody = readErrorResponseBody(error)
  if (!responseBody) {
    return null
  }

  try {
    const body = JSON.parse(responseBody) as unknown
    if (body && typeof body === 'object' && 'Diagnostics' in body) {
      const diagnostics = (body as { readonly Diagnostics?: unknown }).Diagnostics
      if (Array.isArray(diagnostics)) {
        const firstMessage = diagnostics
          .map((diagnostic) =>
            diagnostic && typeof diagnostic === 'object' && 'Message' in diagnostic
              ? (diagnostic as { readonly Message?: unknown }).Message
              : null,
          )
          .find((message): message is string => typeof message === 'string' && message.length > 0)
        if (firstMessage) {
          return firstMessage
        }
      }
    }

    if (body && typeof body === 'object' && 'message' in body) {
      const message = (body as { readonly message?: unknown }).message
      return typeof message === 'string' ? message : null
    }

    if (body && typeof body === 'object' && 'Message' in body) {
      const message = (body as { readonly Message?: unknown }).Message
      return typeof message === 'string' ? message : null
    }

    if (body && typeof body === 'object' && 'detail' in body) {
      const detail = (body as { readonly detail?: unknown }).detail
      if (typeof detail === 'string' && detail.length > 0) {
        return detail
      }
    }

    if (body && typeof body === 'object' && 'title' in body) {
      const title = (body as { readonly title?: unknown }).title
      return typeof title === 'string' ? title : null
    }
  } catch {
    return responseBody.length > 0 ? responseBody : null
  }

  return null
}

function readErrorDisplayMessage(error: Error) {
  if (!('displayMessage' in error)) {
    return null
  }

  const displayMessage = (error as Error & { readonly displayMessage?: unknown }).displayMessage
  return typeof displayMessage === 'string' && displayMessage.length > 0
    ? displayMessage
    : null
}

function readErrorResponseBody(error: Error) {
  if (!('responseBody' in error)) {
    return null
  }

  const responseBody = (error as Error & { readonly responseBody?: unknown }).responseBody
  return typeof responseBody === 'string' ? responseBody : null
}
