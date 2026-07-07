import { describe, expect, it } from 'vitest'

import {
  formatPresentationDateTime,
  formatPresentationOptionalValue,
  formatPresentationVersion,
  resolvePresentationBadges,
  resolvePresentationContent,
  resolvePresentationEnumLabel,
  resolvePresentationFieldValueIcon,
  resolvePresentationFieldValueLabel,
  resolvePresentationFieldValueTone,
  type FieldPresentationDefinition,
  type PresentationBadgeDefinition,
  type PresentationContentDefinition,
} from './index'

describe('presentation display resolution', () => {
  const statusField = {
    Display: {
      Tone: 'neutral',
      ToneFieldPaths: ['State.Tone'],
      ValueIcons: [{ Icon: 'check', Value: 'ready' }],
      ValueLabels: [{ Label: 'Ready', Value: 'ready' }],
      ValueTones: [{ Tone: 'danger', Value: 'failed' }],
    },
    Field: 'Status',
    Id: 'status',
    Label: 'Status',
  } as unknown as FieldPresentationDefinition

  it('resolves field display labels, tones, and icons', () => {
    expect(resolvePresentationFieldValueLabel(statusField, 'READY')).toBe('Ready')
    expect(resolvePresentationFieldValueIcon(statusField, 'READY')).toBe('check')
    expect(resolvePresentationFieldValueTone(statusField, 'failed')).toBe('danger')
    expect(resolvePresentationFieldValueTone(statusField, 'ready', {
      State: { Tone: 'success' },
    })).toBe('success')
    expect(resolvePresentationFieldValueTone(statusField, 'unknown')).toBe('neutral')
  })

  it('resolves semantic content from literal, field, and template values', () => {
    const content = {
      DescriptionTemplate: 'Current status: {Status}',
      Subtitle: { Field: 'Owner', Kind: 'Field' },
      Title: { Kind: 'Literal', Literal: 'Run summary' },
    } as unknown as PresentationContentDefinition

    expect(resolvePresentationContent(content, {
      Owner: 'Sample',
      Status: 'Ready',
    })).toEqual({
      description: 'Current status: Ready',
      subtitle: 'Sample',
      title: 'Run summary',
    })
  })

  it('resolves badges from field-backed presentation semantics', () => {
    const badges = [{
      FieldId: 'status',
      Id: 'status-badge',
      Name: 'Status',
      OmitWhenEmpty: true,
      Tone: 'success',
    }] as unknown as readonly PresentationBadgeDefinition[]

    expect(resolvePresentationBadges(
      badges,
      { Status: 'ready' },
      { Fields: [statusField] },
    )).toEqual([{
      id: 'status-badge',
      label: 'Status: Ready',
      tone: 'success',
    }])
  })

  it('formats common presentation values without UI dependencies', () => {
    expect(resolvePresentationEnumLabel(1, { 1: 'One' })).toBe('One')
    expect(resolvePresentationEnumLabel('', { 1: 'One' })).toBe('Unknown')
    expect(formatPresentationVersion(7)).toBe('v7')
    expect(formatPresentationOptionalValue(null)).toBe('n/a')
    expect(formatPresentationDateTime(7)).toBe('n/a')
    expect(formatPresentationDateTime('not-a-date')).toBe('not-a-date')
  })
})
