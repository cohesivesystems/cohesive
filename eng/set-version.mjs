#!/usr/bin/env node
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const version = process.argv[2];
const tsOnly = process.argv.includes('--ts-only');

if (!version) {
  console.error('Usage: corepack pnpm version:set <version> [--ts-only]');
  process.exit(1);
}

const repoRoot = resolve(import.meta.dirname, '..');

if (!tsOnly) {
  const propsPath = join(repoRoot, 'Directory.Build.props');
  const props = readFileSync(propsPath, 'utf8');
  const versionPrefix = version.split('-')[0];
  const nextProps = props.replace(
    /<VersionPrefix>[^<]+<\/VersionPrefix>/,
    `<VersionPrefix>${versionPrefix}</VersionPrefix>`,
  );
  writeFileSync(propsPath, nextProps);
}

const frontendRoot = join(repoRoot, 'src', 'frontend');

for (const dir of readdirSync(frontendRoot)) {
  const packagePath = join(frontendRoot, dir, 'package.json');
  let pkg;

  try {
    pkg = JSON.parse(readFileSync(packagePath, 'utf8'));
  } catch {
    continue;
  }

  pkg.version = version;
  writeFileSync(packagePath, `${JSON.stringify(pkg, null, 2)}\n`);
}

console.log(`Set Cohesive TypeScript package version to ${version}`);
