/**
 * Supported visual tones for compact text badges rendered with Tailwind
 * utility classes.
 */
export type PresentationTextBadgeTone = 'amber' | 'red' | 'sky' | 'slate' | 'teal' | 'violet'

/**
 * Resolves the Tailwind utility classes for a compact presentation text badge.
 *
 * The returned classes are intentionally static so Tailwind can discover them
 * during content scanning.
 */
export function getPresentationTextBadgeClassName(tone: PresentationTextBadgeTone) {
  switch (tone) {
    case 'amber':
      return 'border-amber-700/15 bg-amber-50 text-amber-800'
    case 'red':
      return 'border-red-700/15 bg-red-50 text-red-700'
    case 'sky':
      return 'border-sky-700/15 bg-sky-50 text-sky-700'
    case 'teal':
      return 'border-teal-700/15 bg-teal-50 text-teal-700'
    case 'violet':
      return 'border-violet-700/15 bg-violet-50 text-violet-700'
    default:
      return 'border-slate-950/10 bg-slate-100 text-slate-700'
  }
}
