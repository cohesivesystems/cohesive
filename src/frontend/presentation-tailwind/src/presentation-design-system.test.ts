import { describe, expect, it } from 'vitest'

import {
  actionKinds,
  actionScopeKinds,
  inputFormGroupKinds,
  inputFormGroupContainerIntents,
  navigationShellSlotKinds,
  presentationBindingKinds,
  type ActionDefinition,
  type ActionPlacementDefinition,
  type DesignIntent,
  type InputFormGroupDefinition,
  type NavigationShellSlotDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  tailwindPresentationDesignSystem,
} from './presentation-design-system'

describe('tailwind presentation design system', () => {
  it('projects routed surface width from semantic design intent', () => {
    expect(tailwindPresentationDesignSystem.classNames.routedSurface.content({
      design: createDesign({ role: 'editor' }),
    })).toContain('w-full max-w-none')

    expect(tailwindPresentationDesignSystem.classNames.routedSurface.content({
      design: createDesign({ role: 'relation-definition-editor' }),
    })).toContain('w-full max-w-none')
    expect(tailwindPresentationDesignSystem.classNames.routedSurface.content({
      design: createDesign({ role: 'relation-definition-editor' }),
    })).toContain('flex-1')

    expect(tailwindPresentationDesignSystem.classNames.routedSurface.content({
      design: createDesign({ role: 'relations-workspace' }),
    })).toContain('w-full max-w-none')

    expect(tailwindPresentationDesignSystem.classNames.routedSurface.content({
      design: createDesign({ role: 'spec-graph-catalog' }),
    })).toContain('w-full max-w-none')

    expect(tailwindPresentationDesignSystem.classNames.routedSurface.content({
      design: createDesign({ role: 'execution-details' }),
    })).toContain('max-w-7xl')

    expect(tailwindPresentationDesignSystem.classNames.routedSurface.content({
      design: createDesign({ size: 'wide' }),
    })).toContain('max-w-420')

    expect(tailwindPresentationDesignSystem.classNames.routedSurface.content({
      design: null,
    })).toContain('max-w-360')
  })

  it('projects navigation shell slot classes from slot kind and density', () => {
    expect(tailwindPresentationDesignSystem.classNames.navigationShell.slotRoot({
      slot: createSlot({
        density: 'comfortable',
        kind: navigationShellSlotKinds.primaryNavigation,
        placement: 'top-center',
      }),
    })).toBe('flex flex-wrap items-center gap-2')

    expect(tailwindPresentationDesignSystem.classNames.navigationShell.slotRoot({
      slot: createSlot({
        kind: navigationShellSlotKinds.routedContent,
        placement: 'main',
      }),
    })).toBe('contents')
  })

  it('projects component-level button sizing and variants from action semantics', () => {
    expect(tailwindPresentationDesignSystem.components.actionButton.variant({
      action: createAction({ tone: 'danger' }),
      placement: createPlacement({ intent: 'primary', region: 'toolbar' }),
    })).toBe('destructive')

    expect(tailwindPresentationDesignSystem.components.actionButton.variant({
      action: createAction({ variant: 'ghost' }),
      placement: createPlacement({ region: 'row-actions' }),
    })).toBe('ghost')

    expect(tailwindPresentationDesignSystem.components.actionButton.size({
      action: createAction({ size: 'lg' }),
      placement: createPlacement({}),
    })).toBe('lg')
  })

  it('projects known input form group layouts', () => {
    expect(tailwindPresentationDesignSystem.classNames.formSurface.group({
      group: createInputFormGroup({
        id: 'identity',
        orientation: 'vertical',
      }),
    })).toBe('grid gap-3 lg:grid-cols-[1fr_1fr_minmax(18rem,1.25fr)]')

    expect(tailwindPresentationDesignSystem.classNames.formSurface.group({
      group: createInputFormGroup({
        id: 'custom',
        orientation: 'horizontal',
      }),
    })).toBe('flex flex-wrap items-end gap-3')
  })
})

function createDesign({
  density = '',
  layout = null,
  role = '',
  size = '',
  tone = '',
  variant = '',
}: {
  readonly density?: string
  readonly layout?: string | null
  readonly role?: string
  readonly size?: string
  readonly tone?: string
  readonly variant?: string
} = {}): DesignIntent {
  return {
    Density: density,
    Layout: layout,
    Role: role,
    Size: size,
    Tone: tone,
    Variant: variant,
  }
}

function createSlot({
  density,
  kind,
  placement,
}: {
  readonly density?: string
  readonly kind: NavigationShellSlotDefinition['Kind']
  readonly placement: string
}): NavigationShellSlotDefinition {
  return {
    Annotations: [],
    Design: createDesign({ density }),
    Id: 'slot',
    Kind: kind,
    NodeIds: [],
    Placement: placement,
    RegionIds: [],
  }
}

function createAction({
  size,
  tone,
  variant,
}: {
  readonly size?: string
  readonly tone?: string
  readonly variant?: string
} = {}): ActionDefinition {
  return {
    Accessibility: null,
    Annotations: [],
    Binding: {
      Annotations: [],
      Kind: presentationBindingKinds.none,
      Target: null,
    },
    Design: createDesign({
      size,
      tone,
      variant,
    }),
    Enablement: [],
    EndpointRequests: [],
    Execution: null,
    Id: 'action',
    Kind: actionKinds.command,
    Name: 'Action',
    Parameters: [],
    Preparation: null,
    Result: null,
    RuntimePresentation: null,
    Scope: actionScopeKinds.collection,
    Semantics: null,
  }
}

function createPlacement({
  intent = null,
  region = 'toolbar',
}: {
  readonly intent?: string | null
  readonly region?: string
}): ActionPlacementDefinition {
  return {
    ActionId: 'action',
    Icon: null,
    Intent: intent,
    Label: null,
    Region: region,
  }
}

function createInputFormGroup({
  id,
  orientation,
}: {
  readonly id: string
  readonly orientation: string
}): InputFormGroupDefinition {
  return {
    Annotations: [],
    Description: null,
    Design: null,
    Display: {
      Container: inputFormGroupContainerIntents.section,
      IsCollapsible: false,
      IsDefaultCollapsed: false,
      Orientation: orientation,
      Priority: 0,
      SemanticDensity: 'default',
    },
    FieldIds: [],
    Id: id,
    Kind: inputFormGroupKinds.custom,
    Name: id,
  }
}
