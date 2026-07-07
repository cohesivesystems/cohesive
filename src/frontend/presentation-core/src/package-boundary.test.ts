import { readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const sourceRoot = dirname(fileURLToPath(import.meta.url))
const forbiddenImportPatterns = [
  /from\s+['"]react(?:\/[^'"]*)?['"]/,
  /from\s+['"]react-router(?:\/[^'"]*)?['"]/,
  /from\s+['"]@tanstack\//,
  /from\s+['"]@monaco-editor\//,
  /from\s+['"]@mui\//,
  /from\s+['"]lucide-react['"]/,
  /from\s+['"]radix-ui(?:\/[^'"]*)?['"]/,
  /from\s+['"]@\/(?:auth|components|generated|lib|ui)\//,
  /from\s+['"]\.\.\/\.\.\/products\//,
]

describe('@cohesive/presentation-core package boundary', () => {
  it('does not import framework, design-system, editor, or application modules', () => {
    const offenders = listSourceFiles(sourceRoot)
      .filter((filePath) => !filePath.endsWith('package-boundary.test.ts'))
      .flatMap((filePath) => {
        const text = readFileSync(filePath, 'utf8')
        return forbiddenImportPatterns
          .filter((pattern) => pattern.test(text))
          .map((pattern) => `${filePath}: ${pattern}`)
      })

    expect(offenders).toEqual([])
  })
})

function listSourceFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((entry) => {
    const entryPath = join(directory, entry)
    const stats = statSync(entryPath)
    if (stats.isDirectory()) {
      return listSourceFiles(entryPath)
    }

    return entryPath.endsWith('.ts') ? [entryPath] : []
  })
}
