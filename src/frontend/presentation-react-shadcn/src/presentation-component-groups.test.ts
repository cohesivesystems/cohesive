import { createElement, isValidElement } from 'react'
import { describe, expect, it, vi } from 'vitest'

import {
  createShadcnActionComponents,
  createShadcnBadgeComponents,
  createShadcnCollectionChromeComponents,
  createShadcnCollectionComponents,
  createShadcnDocumentWorkspaceComponents,
  createShadcnDocumentWorkspaceTreeItem,
  createShadcnDocumentWorkspaceTreeLayout,
  createShadcnDocumentWorkspaceTreeView,
  createShadcnFeedbackComponents,
  createShadcnFieldValueComponents,
  createShadcnFormComponents,
  createShadcnJsonDocumentDiff,
  createShadcnJsonDocumentEditor,
  createShadcnNavigationComponents,
  createShadcnPromptComponents,
  createShadcnSurfaceComponents,
  createShadcnTabsComponents,
  createShadcnViewChromeComponents,
} from './presentation-component-groups'

describe('shadcn component groups', () => {
  it('creates action and badge wrappers from injected primitives', () => {
    const action = createShadcnActionComponents({
      Button: (props) => createElement('button', props),
    }).ActionButton({
      children: 'Run',
      variant: 'outline',
    })
    const badge = createShadcnBadgeComponents({
      Badge: (props) => createElement('span', props),
    }).Badge({
      children: 'Draft',
      variant: 'secondary',
    })

    expect(isValidElement(action)).toBe(true)
    expect(isValidElement(action) ? action.props.variant : null).toBe('outline')
    expect(isValidElement(badge)).toBe(true)
    expect(isValidElement(badge) ? badge.props.variant : null).toBe('secondary')
  })

  it('creates navigation links from injected routing and button class bindings', () => {
    const navigation = createShadcnNavigationComponents({
      buttonClassName: ({ size, variant }) => `button:${size}:${variant}`,
      Link: (props) => createElement('a', props),
    })
    const rendered = navigation.NavigationLink({
      children: 'Runs',
      className: 'extra',
      isActive: true,
      to: '/runs',
    })

    expect(isValidElement(rendered)).toBe(true)
    expect(isValidElement(rendered) ? rendered.props.className : null)
      .toBe('button:sm:secondary extra')
    expect(isValidElement(rendered) ? rendered.props.to : null).toBe('/runs')
  })

  it('creates portable feedback components', () => {
    const status = createShadcnFeedbackComponents().StatusBlock({
      label: 'Failed',
      tone: 'error',
    })

    expect(isValidElement(status)).toBe(true)
    expect(isValidElement(status) ? status.props.className : '').toContain('text-red-800')
  })

  it('creates portable field value components', () => {
    const field = createShadcnFieldValueComponents().FieldValueJson({
      formattedValue: '{ "ok": true }',
      tone: 'red',
    })

    expect(isValidElement(field)).toBe(true)
    expect(isValidElement(field) ? field.props.className : '').toContain('border-red-300/60')
  })

  it('creates tabs from injected primitives', () => {
    const tabs = createShadcnTabsComponents({
      Tabs: (props) => createElement('section', props),
      TabsContent: (props) => createElement('article', props),
      TabsList: (props) => createElement('div', props),
      TabsTrigger: (props) => createElement('button', props),
    })
    const layout = tabs.TabsLayout({
      children: 'Body',
      onValueChange: vi.fn(),
      value: 'overview',
    })
    const panel = tabs.TabsPanel({
      children: 'Panel',
      region: { Id: 'overview' } as never,
      value: 'overview',
    })

    expect(isValidElement(layout)).toBe(true)
    const layoutClassName = isValidElement(layout) ? layout.props.className : ''
    expect(layoutClassName).toContain('flex')
    expect(layoutClassName).toContain('min-h-0')
    expect(layoutClassName).toContain('w-full')
    expect(layoutClassName).toContain('min-w-0')
    expect(layoutClassName).toContain('flex-1')
    expect(isValidElement(panel)).toBe(true)
    const panelClassName = isValidElement(panel) ? panel.props.className : ''
    expect(panelClassName).toContain('w-full')
    expect(panelClassName).toContain('min-w-0')
    expect(isValidElement(panel) ? panel.props.value : null).toBe('overview')
  })

  it('creates form controls from injected primitives', () => {
    const onDateChange = vi.fn()
    const forms = createShadcnFormComponents<{ mode: string }>({
      Button: (props) => createElement('button', props),
      DateTimeFilter: (props) => createElement('div', props),
      Input: (props) => createElement('input', props),
      Label: (props) => createElement('label', props),
      ToggleGroup: (props) => createElement('div', props),
      ToggleGroupItem: (props) => createElement('button', props),
    })
    const choiceGroup = forms.ChoiceToggleGroup({
      children: 'Choices',
      onValueChange: vi.fn(),
      value: ['ready'],
    })
    const field = forms.InputFormField({
      control: 'Control',
      field: { Id: 'status' } as never,
      label: 'Status',
    })
    const dateTime = forms.DateTimeFilterControl({
      onValueChange: onDateChange,
      value: { mode: 'preset' },
    })

    expect(isValidElement(choiceGroup)).toBe(true)
    expect(isValidElement(choiceGroup) ? choiceGroup.props.type : null).toBe('multiple')
    expect(isValidElement(choiceGroup) ? choiceGroup.props.value : null).toEqual(['ready'])
    expect(isValidElement(field) ? field.props['data-field-id'] : null).toBe('status')
    expect(isValidElement(dateTime) ? dateTime.props.onChange : null).toBe(onDateChange)
  })

  it('creates collection chrome around injected button primitives', () => {
    const collectionChrome = createShadcnCollectionChromeComponents({
      Button: (props) => createElement('button', props),
    })
    const pagination = collectionChrome.CollectionPaginationBar({
      canGoNextPage: true,
      canGoPreviousPage: false,
      isFetching: false,
      onFirstPage: vi.fn(),
      onNextPage: vi.fn(),
      onPreviousPage: vi.fn(),
      pageLabel: 'Page 1',
      pageSizeLabel: '25/page',
      shownLabel: '1-25',
      totalLabel: '100 total',
    })
    const rowActions = collectionChrome.CollectionRowActions({
      children: 'Actions',
      className: 'extra',
    })

    expect(isValidElement(pagination)).toBe(true)
    expect(isValidElement(pagination) ? pagination.props.className : '')
      .toContain('justify-between')
    expect(isValidElement(rowActions)).toBe(true)
    expect(isValidElement(rowActions) ? rowActions.props.className : '')
      .toBe('flex justify-end gap-1 extra')
  })

  it('creates collection row action menus with injected table bindings', () => {
    const detailLayout = vi.fn(({ detail }) => createElement('section', null, detail))
    const collections = createShadcnCollectionComponents({
      CollectionDetailLayout: detailLayout,
      DataTable: <TData extends object>(_props: never) => null,
      rowActionMenuTriggerClassName: ({ size, variant }) => `button:${size}:${variant}`,
    })
    const trigger = collections.RowActionMenuTrigger({
      'aria-label': 'Open row actions',
      children: '...',
    })
    const menu = collections.RowActionMenu({
      children: collections.RowActionMenuItem({
        children: 'Run',
        onClick: vi.fn(),
      }),
      trigger,
    })
    const detail = collections.CollectionDetailLayout({
      detail: 'Detail',
      mode: 'side-panel',
      table: 'Table',
    })

    expect(isValidElement(trigger)).toBe(true)
    expect(isValidElement(trigger) ? trigger.props.className : '')
      .toContain('button:icon-sm:ghost')
    expect(isValidElement(menu)).toBe(true)
    expect(isValidElement(menu) ? menu.props.className : '').toBe('flex justify-end')
    expect(detailLayout).toHaveBeenCalledWith({
      detail: 'Detail',
      mode: 'side-panel',
      table: 'Table',
    })
    expect(detail).toEqual(createElement('section', null, 'Detail'))
  })

  it('creates surface wrappers from injected primitives', () => {
    const surfaces = createShadcnSurfaceComponents({
      Surface: (props) => createElement('section', props),
    })
    const surface = surfaces.ViewSurface({
      children: 'Body',
      contentTopInset: 'none',
      title: 'Overview',
    })
    const plainContent = surfaces.ViewSurfaceContent({
      children: 'Only child',
    })
    const chromedContent = surfaces.ViewSurfaceContent({
      afterContentChrome: 'After',
      children: 'Body',
    })
    const mergedActions = surfaces.ViewSurfaceHeaderActions({
      action: 'Primary',
      chrome: 'Secondary',
      className: 'extra',
    })

    expect(isValidElement(surface)).toBe(true)
    expect(isValidElement(surface) ? surface.props.title : null).toBe('Overview')
    expect(isValidElement(surface) ? surface.props.contentTopInset : null).toBe('none')
    expect(plainContent).toBe('Only child')
    expect(isValidElement(chromedContent)).toBe(true)
    expect(isValidElement(mergedActions)).toBe(true)
    expect(isValidElement(mergedActions) ? mergedActions.props.className : '')
      .toBe('flex flex-wrap items-center justify-end gap-2 extra')
  })

  it('creates document workspace wrappers with injected infrastructure', () => {
    const resolveSurfaceSlotOptions = vi.fn(() => ({
      className: 'surface-slot',
    }))
    const documentWorkspaces = createShadcnDocumentWorkspaceComponents({
      Badge: (props) => createElement('span', props),
      DocumentWorkspaceTreeControls: (props) => createElement('nav', props),
      DocumentWorkspaceTreeItem: (props) => createElement('li', props),
      DocumentWorkspaceTreeLayout: (props) => createElement('section', props),
      DocumentWorkspaceTreeView: (props) => createElement('ul', props),
      JsonDocumentDiff: (props) => createElement('div', props),
      JsonDocumentEditor: (props) => createElement('div', props),
      resolveSurfaceSlotOptions,
    })
    const detailPanel = documentWorkspaces.DocumentWorkspaceDetailPanel({
      children: 'Body',
      title: 'Document',
    })
    const label = documentWorkspaces.DocumentWorkspaceNodeLabel({
      badges: [{ label: 'edited' }],
      label: 'Root',
    })
    const slot = documentWorkspaces.DocumentWorkspaceSurfaceSlot({
      renderSurface: (options) => createElement('aside', options),
      role: 'header',
      slot: 'summary',
      viewId: 'document-view',
    })
    const slotSurface = isValidElement(slot) ? slot.props.children : null
    const table = documentWorkspaces.DocumentWorkspaceTable({
      details: [{ label: 'Status', value: 'Ready' }],
    })

    expect(isValidElement(detailPanel)).toBe(true)
    expect(isValidElement(detailPanel) ? detailPanel.type : null).toBe('aside')
    expect(isValidElement(label)).toBe(true)
    expect(isValidElement(label) ? label.props.children[2][0].props.variant : null)
      .toBe('outline')
    expect(resolveSurfaceSlotOptions).toHaveBeenCalledWith({
      role: 'header',
      slot: 'summary',
    })
    expect(
      isValidElement(slot)
        ? slot.props['data-presentation-document-workspace-slot-id']
        : null,
    ).toBe('summary')
    expect(
      isValidElement(slot) ? slot.props['data-presentation-view-id'] : null,
    ).toBe('document-view')
    expect(isValidElement(slotSurface) ? slotSurface.props.className : null)
      .toBe('surface-slot')
    expect(isValidElement(table)).toBe(true)
  })

  it('creates document workspace tree adapters from injected primitives', () => {
    const onExpandedItemIdsChange = vi.fn()
    const onSelectedItemIdChange = vi.fn()
    const treeLayout = createShadcnDocumentWorkspaceTreeLayout({
      Group: (props) => createElement('section', props),
      Panel: (props) => createElement('article', props),
      Separator: (props) => createElement('div', props),
    })
    const treeItem = createShadcnDocumentWorkspaceTreeItem({
      TreeItem: (props) => createElement('li', props),
    })
    const treeView = createShadcnDocumentWorkspaceTreeView({
      sx: { color: 'slate' },
      TreeView: (props) => createElement('ul', props),
    })
    const layout = treeLayout({
      detail: 'Detail',
      detailId: 'detail',
      tree: 'Tree',
      treeId: 'tree',
    })
    const item = treeItem({
      itemId: 'root',
      label: 'Root',
    })
    const view = treeView({
      ariaLabel: 'Documents',
      expandedItemIds: ['root'],
      onExpandedItemIdsChange,
      onSelectedItemIdChange,
      selectedItemId: 'root',
    })

    expect(isValidElement(layout)).toBe(true)
    expect(isValidElement(layout) ? layout.props.orientation : null).toBe('horizontal')
    expect(isValidElement(item) ? item.props.itemId : null).toBe('root')
    expect(isValidElement(view) ? view.props.expandedItems : null).toEqual(['root'])
    expect(isValidElement(view) ? view.props.sx : null).toEqual({ color: 'slate' })

    if (isValidElement(view)) {
      view.props.onExpandedItemsChange(null, ['root', 'child'])
      view.props.onSelectedItemsChange(null, ['child'])
    }

    expect(onExpandedItemIdsChange).toHaveBeenCalledWith(['root', 'child'])
    expect(onSelectedItemIdChange).toHaveBeenCalledWith('child')
  })

  it('creates JSON document diff and editor wrappers from injected components', () => {
    const diff = createShadcnJsonDocumentDiff<{ id: string }>({
      JsonDocumentDiff: (props) => createElement('div', props),
    })
    const editor = createShadcnJsonDocumentEditor<{ layout: 'fill' | 'inline' }>({
      fallbackClassName: (props) => props.layout === 'fill' ? 'fill-fallback' : 'inline-fallback',
      JsonDocumentEditor: (props) => createElement('section', props),
    })
    const renderedDiff = diff({ id: 'review' })
    const renderedEditor = editor({ layout: 'fill' })

    expect(isValidElement(renderedDiff)).toBe(true)
    expect(isValidElement(renderedDiff) ? renderedDiff.props.id : null).toBe('review')
    expect(isValidElement(renderedEditor)).toBe(true)
    expect(isValidElement(renderedEditor) ? renderedEditor.props.fallback.props.className : null)
      .toBe('fill-fallback')
  })

  it('creates prompt components that suppress empty optional regions', () => {
    const prompts = createShadcnPromptComponents()

    expect(prompts.PromptFooter({ children: null })).toBeNull()
    expect(prompts.PromptRegion({
      children: false,
      region: { Id: 'filters' } as never,
    })).toBeNull()
  })

  it('creates view chrome switches with injected button primitives', () => {
    const onSelect = vi.fn()
    const viewChrome = createShadcnViewChromeComponents({
      Button: (props) => createElement('button', props),
    })
    const rendered = viewChrome.ViewSwitch({
      ariaLabel: 'View',
      items: [
        {
          id: 'table',
          isActive: true,
          label: 'Table',
          onSelect,
        },
      ],
    })

    expect(isValidElement(rendered)).toBe(true)
    expect(isValidElement(rendered) ? rendered.props.role : null).toBe('group')
    expect(isValidElement(rendered) ? rendered.props.children[0].props.variant : null)
      .toBe('secondary')
  })
})
