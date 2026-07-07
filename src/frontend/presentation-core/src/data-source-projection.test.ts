import { describe, expect, it } from 'vitest'

import {
  dataSourceKinds,
  presentationBindingKinds,
  type DataSourceDefinition,
  type PresentationModuleDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  createLocalStateDataSourceBindingProjectionRegistry,
  createPresentationDataSourceTargetInterpretation,
  presentationDataSourceBindingKinds,
  projectPresentationDataSourceBindings,
  readDataSourceBindingRuntimeOptions,
} from './index'

describe('data source binding projection', () => {
  it('projects local-state data sources through a target interpretation', () => {
    const dataSource = {
      Binding: {
        Kind: presentationBindingKinds.localState,
        Options: null,
      },
      Id: 'selected-run',
      Kind: dataSourceKinds.localState,
      Name: 'Selected run',
    } as unknown as DataSourceDefinition
    const module = {
      DataSources: [dataSource],
    } as unknown as PresentationModuleDefinition
    const targetInterpretation = createPresentationDataSourceTargetInterpretation({})
    const bindings = projectPresentationDataSourceBindings({
      dataSourceIds: ['selected-run'],
      module,
      registry: createLocalStateDataSourceBindingProjectionRegistry({
        resolveLocalValue: (context) => ({
          data: { id: context.dataSource.Id },
          dataSourceId: context.dataSource.Id,
        }),
        targetInterpretation,
      }),
    })

    expect(bindings).toEqual([
      {
        authorization: { kind: 'none' },
        data: { id: 'selected-run' },
        dataSourceId: 'selected-run',
        kind: presentationDataSourceBindingKinds.localValue,
      },
    ])
  })

  it('creates blocked local bindings for unbound data sources', () => {
    const dataSource = {
      Binding: null,
      Id: 'runs',
      Kind: dataSourceKinds.collectionQuery,
      Name: 'Runs',
    } as unknown as DataSourceDefinition
    const module = {
      DataSources: [dataSource],
    } as unknown as PresentationModuleDefinition

    expect(projectPresentationDataSourceBindings({
      dataSourceIds: ['runs'],
      module,
      registry: {},
    })).toEqual([
      {
        authorization: {
          blockedLabel: "No frontend binding is registered for 'Runs'.",
          isAuthorized: false,
          kind: 'required',
        },
        data: undefined,
        dataSourceId: 'runs',
        kind: presentationDataSourceBindingKinds.localValue,
      },
    ])
  })

  it('reads runtime options from data source annotations', () => {
    const dataSource = {
      Annotations: [
        {
          Name: 'cohesive.presentation.data-source.runtime',
          Value: {
            emptyMessage: 'No runs',
            fallbackData: [],
            pendingLabel: 'Loading runs',
            refetchIntervalSeconds: 30,
            retry: 2,
            staleTimeMs: 5000,
          },
        },
      ],
    } as unknown as DataSourceDefinition

    expect(readDataSourceBindingRuntimeOptions(dataSource)).toEqual({
      emptyMessage: 'No runs',
      fallbackData: [],
      pendingLabel: 'Loading runs',
      refetchInterval: 30000,
      retry: 2,
      staleTime: 5000,
    })
  })
})
