export const dateTimePresets = ['today', 'this-week', 'this-month', '30m', '1h', '2h', '6h'] as const

export type DateTimePreset = (typeof dateTimePresets)[number]
export type DateTimeFilterMode = 'preset' | 'range'

export interface DateTimeFilterValue {
  readonly mode: DateTimeFilterMode
  readonly preset: DateTimePreset
  readonly afterLocal: string
  readonly beforeLocal: string
  readonly timezone: string
}

export interface DateTimeRange {
  readonly afterUtc?: string | null
  readonly beforeUtc?: string | null
  readonly timezone: string
}

export function createDefaultDateTimeFilter(
  timezone = resolveBrowserTimeZone(),
): DateTimeFilterValue {
  return {
    afterLocal: toDateTimeLocal(startOfToday()),
    beforeLocal: '',
    mode: 'preset',
    preset: 'today',
    timezone,
  }
}

export function normalizeDateTimeFilter(
  value: DateTimeFilterValue,
): DateTimeFilterValue {
  return {
    afterLocal: value.afterLocal,
    beforeLocal: value.beforeLocal,
    mode: value.mode,
    preset: isDateTimePreset(value.preset) ? value.preset : 'today',
    timezone: value.timezone || resolveBrowserTimeZone(),
  }
}

export function resolveDateTimeFilterRange(
  value: DateTimeFilterValue,
  now = new Date(),
): DateTimeRange {
  const timezone = value.timezone || resolveBrowserTimeZone()

  if (value.mode === 'range') {
    return {
      afterUtc: localDateTimeToIso(value.afterLocal),
      beforeUtc: localDateTimeToIso(value.beforeLocal),
      timezone,
    }
  }

  if (value.preset === 'today') {
    return {
      afterUtc: startOfToday(now).toISOString(),
      beforeUtc: endOfToday(now).toISOString(),
      timezone,
    }
  }

  if (value.preset === 'this-week') {
    return {
      afterUtc: startOfThisWeek(now).toISOString(),
      beforeUtc: null,
      timezone,
    }
  }

  if (value.preset === 'this-month') {
    return {
      afterUtc: startOfThisMonth(now).toISOString(),
      beforeUtc: null,
      timezone,
    }
  }

  return {
    afterUtc: new Date(now.getTime() - getPresetDurationMs(value.preset)).toISOString(),
    beforeUtc: now.toISOString(),
    timezone,
  }
}

export function isDateTimePreset(value: string): value is DateTimePreset {
  return dateTimePresets.includes(value as DateTimePreset)
}

function getPresetDurationMs(preset: DateTimePreset) {
  switch (preset) {
    case '30m':
      return 30 * 60 * 1000
    case '1h':
      return 60 * 60 * 1000
    case '2h':
      return 2 * 60 * 60 * 1000
    case '6h':
      return 6 * 60 * 1000
    case 'today':
    case 'this-week':
    case 'this-month':
      return 0
  }
}

function resolveBrowserTimeZone() {
  return Intl.DateTimeFormat().resolvedOptions().timeZone || 'local'
}

function startOfToday(reference = new Date()) {
  const date = new Date(reference)
  date.setHours(0, 0, 0, 0)
  return date
}

function endOfToday(reference = new Date()) {
  const date = new Date(reference)
  date.setHours(23, 59, 59, 999)
  return date
}

function startOfThisWeek(reference = new Date()) {
  const date = startOfToday(reference)
  const day = date.getDay()
  const daysSinceMonday = (day + 6) % 7
  date.setDate(date.getDate() - daysSinceMonday)
  return date
}

function startOfThisMonth(reference = new Date()) {
  const date = startOfToday(reference)
  date.setDate(1)
  return date
}

function toDateTimeLocal(date: Date) {
  const year = date.getFullYear()
  const month = `${date.getMonth() + 1}`.padStart(2, '0')
  const day = `${date.getDate()}`.padStart(2, '0')
  const hours = `${date.getHours()}`.padStart(2, '0')
  const minutes = `${date.getMinutes()}`.padStart(2, '0')
  return `${year}-${month}-${day}T${hours}:${minutes}`
}

function localDateTimeToIso(value: string) {
  if (!value) {
    return null
  }

  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString()
}
