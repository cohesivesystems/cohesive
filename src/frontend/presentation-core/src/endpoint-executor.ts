/**
 * Transport-neutral request envelope passed to an endpoint executor after a
 * presentation runtime has projected semantic inputs into concrete API inputs.
 *
 * @typeParam TBody - Request body payload shape.
 * @typeParam TQuery - Query payload shape.
 */
export interface EndpointExecutionRequest<TBody = unknown, TQuery = unknown> {
  /** Request body payload for mutation/action endpoints. */
  readonly body?: TBody

  /** Query payload for query/read endpoints. */
  readonly query?: TQuery | null

  /** Route parameters substituted into endpoint paths by the host adapter. */
  readonly routeParameters?: Readonly<Record<string, string | null | undefined>>
}

/**
 * Executes a concrete endpoint request.
 *
 * Endpoint executors are supplied by applications or adapters because only the
 * host knows how endpoint ids map to generated clients, fetch calls, auth, and
 * transport concerns.
 *
 * @typeParam TResult - Result shape returned by the endpoint executor.
 * @typeParam TBody - Request body payload shape.
 * @typeParam TQuery - Query payload shape.
 */
export type EndpointExecutor<
  TResult = unknown,
  TBody = unknown,
  TQuery = unknown,
> = (request: EndpointExecutionRequest<TBody, TQuery>) => Promise<TResult>

/**
 * Registry keyed by semantic endpoint id.
 */
export type EndpointExecutorRegistry = Readonly<Record<string, EndpointExecutor>>

/**
 * Dispatches an endpoint id through a host-provided endpoint executor registry.
 *
 * @typeParam TResult - Result shape returned by the selected executor.
 * @typeParam TBody - Request body payload shape.
 * @typeParam TQuery - Query payload shape.
 */
export type EndpointRegistryExecutor = <
  TResult = unknown,
  TBody = unknown,
  TQuery = unknown,
>(
  endpointId: string,
  request?: EndpointExecutionRequest<TBody, TQuery>,
) => Promise<TResult>

/**
 * Options for creating an endpoint registry dispatcher.
 */
export interface CreateEndpointExecutorOptions {
  /** Human-readable endpoint family label used in diagnostic errors. */
  readonly label?: string
}

/**
 * Creates a generic dispatcher from semantic endpoint ids to concrete endpoint
 * executors.
 */
export function createEndpointExecutor(
  registry: EndpointExecutorRegistry,
  { label = 'API' }: CreateEndpointExecutorOptions = {},
): EndpointRegistryExecutor {
  return async function executeEndpoint<
    TResult = unknown,
    TBody = unknown,
    TQuery = unknown,
  >(
    endpointId: string,
    request: EndpointExecutionRequest<TBody, TQuery> = {},
  ): Promise<TResult> {
    const executor = registry[endpointId]
    if (!executor) {
      throw new Error(`No ${label} endpoint executor is registered for endpoint '${endpointId}'.`)
    }

    return await executor(request) as TResult
  }
}

/**
 * Reads a required route parameter from a projected endpoint request.
 */
export function readRequiredEndpointRouteParameter(
  endpointId: string,
  request: EndpointExecutionRequest,
  parameterName: string,
): string {
  const value = request.routeParameters?.[parameterName]
  if (value === null || value === undefined || value === '') {
    throw new Error(
      `Endpoint '${endpointId}' requires route parameter '${parameterName}'.`,
    )
  }

  return value
}

/**
 * Reads a required request body from a projected endpoint request.
 *
 * @typeParam TBody - Expected request body payload shape.
 */
export function readRequiredEndpointBody<TBody>(
  endpointId: string,
  request: EndpointExecutionRequest,
): TBody {
  if (request.body === undefined) {
    throw new Error(`Endpoint '${endpointId}' requires a request body.`)
  }

  return request.body as TBody
}
