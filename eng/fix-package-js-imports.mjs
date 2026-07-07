#!/usr/bin/env node
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const targetDirectory = resolve(process.argv[2] ?? 'dist');

if (!existsSync(targetDirectory) || !statSync(targetDirectory).isDirectory()) {
  console.error(`Directory not found: ${targetDirectory}`);
  process.exit(1);
}

for (const filePath of listGeneratedFiles(targetDirectory)) {
  const source = readFileSync(filePath, 'utf8');
  const rewritten = rewriteModuleSpecifiers(filePath, source);
  if (rewritten !== source)
    writeFileSync(filePath, rewritten);
}

function rewriteModuleSpecifiers(filePath, source) {
  return source.replace(
    /(\bfrom\s+|\bimport\s*(?:\(\s*)?)['"](\.{1,2}\/[^'"]+)['"]/g,
    (match, prefix, specifier) => {
      if (!shouldRewriteSpecifier(filePath, specifier))
        return match;

      return `${prefix}'${specifier}.js'`;
    },
  );
}

function shouldRewriteSpecifier(filePath, specifier) {
  if (
    specifier.includes('?') ||
    specifier.includes('#') ||
    specifier.endsWith('.js') ||
    specifier.endsWith('.json') ||
    specifier.endsWith('.css') ||
    specifier.endsWith('.svg')
  )
    return false;

  const resolved = resolve(dirname(filePath), specifier);
  return existsSync(`${resolved}.js`) || existsSync(`${resolved}.d.ts`);
}

function listGeneratedFiles(directory) {
  return readdirSync(directory).flatMap((entry) => {
    const entryPath = join(directory, entry);
    const stats = statSync(entryPath);
    if (stats.isDirectory())
      return listGeneratedFiles(entryPath);

    return entryPath.endsWith('.js') || entryPath.endsWith('.d.ts') ? [entryPath] : [];
  });
}
