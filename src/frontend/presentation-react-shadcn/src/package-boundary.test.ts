import { readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const sourceRoot = dirname(fileURLToPath(import.meta.url))
const importSpecifierPattern =
  /\bimport\s+(?:type\s+)?(?:[^'"]+\s+from\s+)?['"]([^'"]+)['"]|\bexport\s+(?:type\s+)?[^'"]+\s+from\s+['"]([^'"]+)['"]/g

const forbiddenSpecifiers = [
  {
    reason: 'presentation-react-shadcn source should use relative imports for its own modules',
    matches: (specifier: string) =>
      specifier === '@cohesive/presentation-react-shadcn' ||
      specifier.startsWith('@cohesive/presentation-react-shadcn/'),
  },
  {
    reason: 'presentation-react-shadcn must not depend on other concrete presentation adapters',
    matches: (specifier: string) =>
      specifier === '@cohesive/presentation-react-mui' ||
      specifier.startsWith('@cohesive/presentation-react-mui/') ||
      specifier === '@cohesive/presentation-monaco' ||
      specifier.startsWith('@cohesive/presentation-monaco/'),
  },
  {
    reason: 'presentation-react-shadcn must not depend on MUI or Monaco packages',
    matches: (specifier: string) =>
      specifier.startsWith('@mui/') || specifier.startsWith('@monaco-editor/'),
  },
  {
    reason: 'presentation-react-shadcn must not depend on application code',
    matches: (specifier: string) =>
      specifier.startsWith('@/') ||
      specifier.includes('/products/') ||
      specifier.includes('admin-ui'),
  },
]

describe('@cohesive/presentation-react-shadcn package boundary', () => {
  it('does not import other concrete adapters, MUI, Monaco, or application modules', () => {
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
