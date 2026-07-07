import { readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const sourceRoot = dirname(fileURLToPath(import.meta.url))
const importSpecifierPattern =
  /\bimport\s+(?:type\s+)?(?:[^'"]+\s+from\s+)?['"]([^'"]+)['"]|\bexport\s+(?:type\s+)?[^'"]+\s+from\s+['"]([^'"]+)['"]/g

const forbiddenSpecifiers = [
  {
    reason: 'presentation-react source should use relative imports for its own modules',
    matches: (specifier: string) =>
      specifier === '@cohesivesystems/presentation-react' ||
      specifier.startsWith('@cohesivesystems/presentation-react/'),
  },
  {
    reason: 'presentation-react must not depend on concrete presentation adapters',
    matches: (specifier: string) =>
      specifier === '@cohesivesystems/presentation-react-shadcn' ||
      specifier.startsWith('@cohesivesystems/presentation-react-shadcn/') ||
      specifier === '@cohesivesystems/presentation-react-mui' ||
      specifier.startsWith('@cohesivesystems/presentation-react-mui/') ||
      specifier === '@cohesivesystems/presentation-tailwind' ||
      specifier.startsWith('@cohesivesystems/presentation-tailwind/') ||
      specifier === '@cohesivesystems/presentation-monaco' ||
      specifier.startsWith('@cohesivesystems/presentation-monaco/'),
  },
  {
    reason: 'presentation-react must not depend on design-system packages',
    matches: (specifier: string) =>
      specifier.startsWith('@mui/') ||
      specifier.startsWith('@monaco-editor/') ||
      specifier === 'lucide-react' ||
      specifier === 'class-variance-authority' ||
      specifier === 'clsx' ||
      specifier === 'tailwind-merge' ||
      specifier.startsWith('radix-ui'),
  },
  {
    reason: 'presentation-react must not depend on application code',
    matches: (specifier: string) =>
      specifier.startsWith('@/') ||
      specifier.includes('/products/') ||
      specifier.includes('admin-ui'),
  },
  {
    reason: 'presentation-react must not depend on table rendering adapters',
    matches: (specifier: string) => specifier === '@tanstack/react-table',
  },
]

describe('@cohesivesystems/presentation-react package boundary', () => {
  it('does not import concrete adapters, design systems, table renderers, or application modules', () => {
    const offenders = listSourceFiles(sourceRoot)
      .filter((filePath) => !filePath.endsWith('package-boundary.test.ts'))
      .flatMap(findForbiddenImports)

    expect(offenders).toEqual([])
  })
})

function findForbiddenImports(filePath: string) {
  const source = readFileSync(filePath, 'utf8')
  return extractImportSpecifiers(source).flatMap((specifier) =>
    forbiddenSpecifiers
      .filter((rule) => rule.matches(specifier))
      .map((rule) => `${filePath}: ${specifier} (${rule.reason})`),
  )
}

function extractImportSpecifiers(source: string) {
  return Array.from(source.matchAll(importSpecifierPattern), (match) => match[1] ?? match[2])
}

function listSourceFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((entry) => {
    const entryPath = join(directory, entry)
    const stats = statSync(entryPath)
    if (stats.isDirectory()) {
      return listSourceFiles(entryPath)
    }

    return /\.(ts|tsx)$/.test(entryPath) ? [entryPath] : []
  })
}
