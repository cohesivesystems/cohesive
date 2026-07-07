#!/usr/bin/env node
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const version = process.argv[2];
const tsOnly = process.argv.includes('--ts-only');
const dotnetOnly = process.argv.includes('--dotnet-only');

if (!version || (tsOnly && dotnetOnly)) {
  console.error('Usage: corepack pnpm version:set <version> [--ts-only|--dotnet-only]');
  process.exit(1);
}

const versionMatch = /^(?<prefix>\d+\.\d+\.\d+)(?:-(?<suffix>[0-9A-Za-z][0-9A-Za-z.-]*))?$/.exec(version);

if (!versionMatch?.groups) {
  console.error(`Invalid semantic version: ${version}`);
  process.exit(1);
}

const repoRoot = resolve(import.meta.dirname, '..');
const versionPrefix = versionMatch.groups.prefix;
const versionSuffix = versionMatch.groups.suffix ?? null;

if (!tsOnly) {
  const propsPath = join(repoRoot, 'Directory.Build.props');
  const props = readFileSync(propsPath, 'utf8');
  let nextProps = setMsBuildProperty(props, 'VersionPrefix', versionPrefix);
  nextProps = versionSuffix === null
    ? removeMsBuildProperty(nextProps, 'VersionSuffix')
    : setMsBuildProperty(nextProps, 'VersionSuffix', versionSuffix, 'VersionPrefix');
  writeFileSync(propsPath, nextProps);
}

if (!dotnetOnly) {
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
}

console.log(`Set Cohesive package version to ${version}`);

function setMsBuildProperty(xml, name, value, insertAfter = null) {
  const propertyPattern = new RegExp(`<${name}>[^<]*</${name}>`);
  const propertyText = `<${name}>${value}</${name}>`;

  if (propertyPattern.test(xml)) {
    return xml.replace(propertyPattern, propertyText);
  }

  if (insertAfter !== null) {
    const insertPattern = new RegExp(`(?<indent>[ \\t]*)<${insertAfter}>[^<]*</${insertAfter}>`);
    const match = insertPattern.exec(xml);

    if (match?.groups) {
      return xml.replace(
        insertPattern,
        matched => `${matched}\n${match.groups.indent}${propertyText}`,
      );
    }
  }

  return xml.replace('</PropertyGroup>', `    ${propertyText}\n  </PropertyGroup>`);
}

function removeMsBuildProperty(xml, name) {
  const propertyPattern = new RegExp(`\\n\\s*<${name}>[^<]*</${name}>`);
  return xml.replace(propertyPattern, '');
}
