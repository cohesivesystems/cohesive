import { describe, expect, it } from 'vitest'

import {
  presentationBindingKinds,
  presentationTargetKinds,
  type PresentationBindingDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  createPresentationIconRegistry,
  projectPresentationActionIconDiagnostics,
  projectPresentationIconDiagnostics,
  resolvePresentationIcon,
  type PresentationIconModuleProjection,
} from './index'

describe('presentation icon registry and diagnostics', () => {
  it('resolves target icon bindings through component keys before raw icon keys', () => {
    const registry = createPresentationIconRegistry({
      byComponentKey: {
        'lucide.save': () => 'component-renderer',
      },
      byIconKey: {
        save: () => 'raw-renderer',
      },
    })

    expect(resolvePresentationIcon({
      componentSet: 'react-shadcn',
      icon: 'save',
      module: createIconModule({
        ComponentKey: 'lucide.save',
        Id: 'save',
      }),
      registry,
    })).toMatchObject({
      componentKey: 'lucide.save',
      icon: 'save',
      resolutionSource: 'component-key',
      targetBindingSource: 'target-icon-binding',
    })
  })

  it('reports raw icon fallback when target binding exists but registry resolves by icon key', () => {
    const diagnostics = projectPresentationActionIconDiagnostics({
      actionPlacements: [
        {
          ActionId: 'save',
          Icon: 'save',
          Label: 'Save',
          Region: 'toolbar',
        },
      ],
      componentSet: 'react-shadcn',
      module: createIconModule({
        ComponentKey: 'lucide.save',
        Id: 'save',
      }),
      registry: createPresentationIconRegistry({
        byIconKey: {
          save: () => 'raw-renderer',
        },
      }),
      source: 'test-icons',
    })

    expect(diagnostics).toHaveLength(1)
    expect(diagnostics[0]).toMatchObject({
      category: 'local-interpretation',
      id: 'action-icon.save.toolbar.save.icon-target-binding-fallback',
      interpretation: {
        status: 'locally-interpreted',
        target: 'presentation-icon-target-binding',
      },
      severity: 'warning',
    })
  })

  it('reports missing generic icon renderer when no registry binding exists', () => {
    const diagnostics = projectPresentationIconDiagnostics({
      icons: [
        {
          icon: 'unknown',
          id: 'metric.total',
          kind: 'metric',
          label: 'Total',
        },
      ],
      registry: createPresentationIconRegistry({}),
      source: 'test-icons',
    })

    expect(diagnostics).toHaveLength(1)
    expect(diagnostics[0]).toMatchObject({
      category: 'missing-binding',
      id: 'icon.metric.metric.total.unknown.missing-icon-renderer',
      interpretation: {
        status: 'unbound',
        target: 'presentation-icon-registry',
      },
    })
  })
})

function createIconModule(
  binding: Pick<PresentationBindingDefinition, 'ComponentKey' | 'ComponentRole' | 'Id'>,
): PresentationIconModuleProjection {
  return {
    Targets: [
      {
        Bindings: [
          {
            ComponentKey: binding.ComponentKey,
            ComponentRole: binding.ComponentRole,
            Id: binding.Id,
            Kind: presentationBindingKinds.icon,
          },
        ],
        ComponentSet: 'react-shadcn',
        Target: presentationTargetKinds.react,
      },
    ],
  }
}
