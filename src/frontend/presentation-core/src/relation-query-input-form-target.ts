import type {
  DataSourceQueryDefinition,
  InputFormDefinition,
  QueryFormDefinition,
  ViewDefinition,
} from './module'
import type { PresentationDataSourceResolver } from './presentation-data-source-runtime'
import { resolvePresentationViewDataSourceIds } from './presentation-data-source-runtime'
import type { ProjectedInputFormTargetContext } from './projected-input-form-runtime'

export function createRelationQueryInputFormTarget({
  dataSourceResolver,
  inputForm,
  queryForm,
  view,
}: {
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly inputForm: InputFormDefinition
  readonly queryForm: QueryFormDefinition
  readonly view: ViewDefinition
}): ProjectedInputFormTargetContext {
  return {
    queryDefinition: resolveProjectedQueryDefinition({
      dataSourceResolver,
      inputForm,
      queryForm,
      view,
    }),
    queryForm,
    stateId: inputForm.SharedStateId ?? queryForm.Target.State.StateId,
  }
}

function resolveProjectedQueryDefinition({
  dataSourceResolver,
  inputForm,
  queryForm,
  view,
}: {
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly inputForm: InputFormDefinition
  readonly queryForm: QueryFormDefinition
  readonly view: ViewDefinition
}): DataSourceQueryDefinition | null {
  const candidateDataSourceIds = [
    inputForm.StateDataSourceId,
    queryForm.Target.State.DraftDataSourceId,
    queryForm.Target.Result.DataSourceId,
    queryForm.Target.State.ResultDataSourceId,
    ...resolvePresentationViewDataSourceIds(view),
  ].filter((dataSourceId): dataSourceId is string => Boolean(dataSourceId))

  for (const dataSourceId of Array.from(new Set(candidateDataSourceIds))) {
    const query = dataSourceResolver.resolve(dataSourceId)?.definition?.Query
    if (query) {
      return query
    }
  }

  return null
}
