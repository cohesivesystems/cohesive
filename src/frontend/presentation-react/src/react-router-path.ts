import type { RouteParameterValues } from './page-host-projection'

export function readRequiredRouteParameter(
  parameters: RouteParameterValues,
  name: string,
) {
  return parameters[name] ?? ''
}

export function toReactRouterPath(pathTemplate: string) {
  const pathname = pathTemplate.split(/[?#]/)[0] ?? '/'
  const routePath = pathname.length === 0 ? '/' : pathname
  return routePath.replace(/\{([^}]+)\}/g, (_token, parameterName: string) => {
    return `:${parameterName}`
  })
}
