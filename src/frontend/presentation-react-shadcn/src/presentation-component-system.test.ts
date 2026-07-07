import { describe, expect, it } from 'vitest'

import {
  createPresentationComponentSystem,
  createPresentationComponentSystemComponents,
} from './presentation-component-system'

describe('presentation component system', () => {
  it('keeps role groups addressable and flattens component bindings', () => {
    const actions = { ActionButton: () => 'action' }
    const badges = { Badge: () => 'badge' }
    const collections = { DataTable: () => 'table' }
    const forms = { TextInputControl: () => 'input' }
    const navigation = { NavigationLink: () => 'link' }

    const system = createPresentationComponentSystem({
      actions,
      badges,
      collectionChrome: {},
      collections,
      documentWorkspaces: {},
      fieldValues: {},
      feedback: {},
      forms,
      id: 'test-system',
      metrics: {},
      navigation,
      processes: {},
      prompts: {},
      records: {},
      surfaces: {},
      tabs: {},
      target: 'test-target',
      viewChrome: {},
    })

    expect(system.id).toBe('test-system')
    expect(system.target).toBe('test-target')
    expect(system.actions).toBe(actions)
    expect(system.badges).toBe(badges)
    expect(system.components.ActionButton()).toBe('action')
    expect(system.components.Badge()).toBe('badge')
    expect(system.components.DataTable()).toBe('table')
    expect(system.components.TextInputControl()).toBe('input')
    expect(system.components.NavigationLink()).toBe('link')
  })

  it('can flatten role groups without creating a full system', () => {
    const components = createPresentationComponentSystemComponents({
      actions: { ActionButton: 'action' },
      badges: { Badge: 'badge' },
      collectionChrome: { CollectionBodySlot: 'collection-body' },
      collections: { DataTable: 'table' },
      documentWorkspaces: { DocumentWorkspaceShell: 'workspace' },
      fieldValues: { FieldValueScalar: 'field' },
      feedback: { StatusBlock: 'status' },
      forms: { TextInputControl: 'input' },
      metrics: { MetricStrip: 'metrics' },
      navigation: { NavigationLink: 'link' },
      processes: { ProcessTaskNotice: 'process' },
      prompts: { PromptModal: 'prompt' },
      records: { RecordDetails: 'record' },
      surfaces: { ViewSurface: 'surface' },
      tabs: { TabsLayout: 'tabs' },
      viewChrome: { ViewChromeSlot: 'chrome' },
    })

    expect(components).toMatchObject({
      ActionButton: 'action',
      Badge: 'badge',
      CollectionBodySlot: 'collection-body',
      DataTable: 'table',
      DocumentWorkspaceShell: 'workspace',
      FieldValueScalar: 'field',
      MetricStrip: 'metrics',
      NavigationLink: 'link',
      PromptModal: 'prompt',
      StatusBlock: 'status',
      TabsLayout: 'tabs',
      TextInputControl: 'input',
      ViewChromeSlot: 'chrome',
      ViewSurface: 'surface',
    })
  })
})
