import { execFile } from 'node:child_process';
import { readdir } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

const defaultNuGetFlatContainerUrl = 'https://api.nuget.org/v3-flatcontainer';
const defaultNpmRegistryUrl = 'https://registry.npmjs.org';
const registryRequestTimeoutMilliseconds = 15_000;

export class PublishedPackageVersionError extends Error {
  constructor(packages) {
    const details = packages
      .map((entry) => `  - ${entry.coordinate} (${entry.url})`)
      .join('\n');

    super(
      `Release version is already published for ${packages.length} package(s):\n${details}\n` +
        'Public package versions are immutable. Choose a new release version before publishing any artifacts.',
    );
    this.name = 'PublishedPackageVersionError';
    this.packages = packages;
  }
}

function withoutTrailingSlash(value) {
  return value.replace(/\/+$/, '');
}

async function listArtifacts(directory, extension) {
  const entries = await readdir(directory, { withFileTypes: true });
  return entries
    .filter((entry) => entry.isFile() && entry.name.endsWith(extension))
    .map((entry) => path.join(directory, entry.name))
    .sort((left, right) => left.localeCompare(right));
}

export async function readNpmPackageMetadata(packagePath) {
  let stdout;
  try {
    ({ stdout } = await execFileAsync('tar', ['-xOf', packagePath, 'package/package.json'], {
      encoding: 'utf8',
      maxBuffer: 1024 * 1024,
    }));
  } catch (error) {
    throw new Error(`Could not read package/package.json from ${packagePath}: ${error.message}`, {
      cause: error,
    });
  }

  let metadata;
  try {
    metadata = JSON.parse(stdout);
  } catch (error) {
    throw new Error(`Package metadata in ${packagePath} is not valid JSON: ${error.message}`, {
      cause: error,
    });
  }

  if (typeof metadata.name !== 'string' || metadata.name.length === 0) {
    throw new Error(`Package metadata in ${packagePath} does not declare a non-empty name.`);
  }
  if (typeof metadata.version !== 'string' || metadata.version.length === 0) {
    throw new Error(`Package metadata in ${packagePath} does not declare a non-empty version.`);
  }

  return { name: metadata.name, version: metadata.version };
}

function nuGetPackageFromArtifact(packagePath, version, registryUrl) {
  const fileName = path.basename(packagePath);
  const versionSuffix = `.${version}.nupkg`;
  if (!fileName.endsWith(versionSuffix)) {
    throw new Error(
      `NuGet artifact ${fileName} does not end with the requested version suffix ${versionSuffix}.`,
    );
  }

  const name = fileName.slice(0, -versionSuffix.length);
  if (name.length === 0) {
    throw new Error(`NuGet artifact ${fileName} does not contain a package ID.`);
  }

  const normalizedName = name.toLowerCase();
  const normalizedVersion = version.toLowerCase();
  const url =
    `${withoutTrailingSlash(registryUrl)}/${encodeURIComponent(normalizedName)}/` +
    `${encodeURIComponent(normalizedVersion)}/${encodeURIComponent(normalizedName)}.${encodeURIComponent(normalizedVersion)}.nupkg`;

  return {
    ecosystem: 'NuGet',
    coordinate: `${name}@${version}`,
    artifactPath: packagePath,
    method: 'HEAD',
    url,
  };
}

async function npmPackageFromArtifact(packagePath, version, registryUrl, readMetadata) {
  const metadata = await readMetadata(packagePath);
  if (metadata.version !== version) {
    throw new Error(
      `npm artifact ${path.basename(packagePath)} declares version ${metadata.version}; expected ${version}.`,
    );
  }
  if (!metadata.name.startsWith('@cohesivesystems/')) {
    throw new Error(
      `npm artifact ${path.basename(packagePath)} declares unexpected package ${metadata.name}.`,
    );
  }

  const encodedName = encodeURIComponent(metadata.name).replace(/^%40/i, '@');
  return {
    ecosystem: 'npm',
    coordinate: `${metadata.name}@${metadata.version}`,
    artifactPath: packagePath,
    method: 'GET',
    url: `${withoutTrailingSlash(registryUrl)}/${encodedName}/${encodeURIComponent(metadata.version)}`,
  };
}

async function fetchStatus(url, method) {
  const response = await fetch(url, {
    method,
    headers: { Accept: 'application/json' },
    signal: AbortSignal.timeout(registryRequestTimeoutMilliseconds),
  });
  await response.body?.cancel();
  return response.status;
}

export async function checkPackageVersionAvailability({
  version,
  nuGetDirectory,
  npmDirectory,
  nuGetFlatContainerUrl = defaultNuGetFlatContainerUrl,
  npmRegistryUrl = defaultNpmRegistryUrl,
  requestStatus = fetchStatus,
  readNpmMetadata = readNpmPackageMetadata,
}) {
  if (typeof version !== 'string' || version.length === 0) {
    throw new Error('A non-empty release version is required.');
  }
  if (typeof nuGetDirectory !== 'string' || nuGetDirectory.length === 0) {
    throw new Error('A NuGet artifact directory is required.');
  }

  const nuGetArtifacts = await listArtifacts(nuGetDirectory, '.nupkg');
  if (nuGetArtifacts.length === 0) {
    throw new Error(`No NuGet packages were found in ${nuGetDirectory}.`);
  }

  const packages = nuGetArtifacts.map((artifactPath) =>
    nuGetPackageFromArtifact(artifactPath, version, nuGetFlatContainerUrl),
  );

  if (npmDirectory !== undefined) {
    const npmArtifacts = await listArtifacts(npmDirectory, '.tgz');
    if (npmArtifacts.length === 0) {
      throw new Error(`No npm packages were found in ${npmDirectory}.`);
    }

    packages.push(
      ...(await Promise.all(
        npmArtifacts.map((artifactPath) =>
          npmPackageFromArtifact(artifactPath, version, npmRegistryUrl, readNpmMetadata),
        ),
      )),
    );
  }

  const coordinates = new Set();
  for (const packageEntry of packages) {
    const registryCoordinate = `${packageEntry.ecosystem}:${packageEntry.coordinate}`;
    if (coordinates.has(registryCoordinate)) {
      throw new Error(`Duplicate release artifact for ${packageEntry.coordinate}.`);
    }
    coordinates.add(registryCoordinate);
  }

  const observations = await Promise.all(
    packages.map(async (packageEntry) => {
      let status;
      try {
        status = await requestStatus(packageEntry.url, packageEntry.method);
      } catch (error) {
        throw new Error(
          `Could not verify whether ${packageEntry.coordinate} is published: ${error.message}`,
          { cause: error },
        );
      }

      if (status !== 200 && status !== 404) {
        throw new Error(
          `Registry returned HTTP ${status} while checking ${packageEntry.coordinate} at ${packageEntry.url}.`,
        );
      }

      return { ...packageEntry, status };
    }),
  );

  const published = observations.filter((entry) => entry.status === 200);
  if (published.length > 0) {
    throw new PublishedPackageVersionError(published);
  }

  return observations;
}

function parseArguments(arguments_) {
  const values = new Map();
  for (let index = 0; index < arguments_.length; index += 2) {
    const name = arguments_[index];
    const value = arguments_[index + 1];
    if (!name?.startsWith('--') || value === undefined || value.startsWith('--')) {
      throw new Error(`Expected arguments as --name value pairs; received ${arguments_.join(' ')}.`);
    }
    if (values.has(name)) {
      throw new Error(`Argument ${name} was provided more than once.`);
    }
    values.set(name, value);
  }

  const supported = new Set(['--version', '--nuget-dir', '--npm-dir']);
  for (const name of values.keys()) {
    if (!supported.has(name)) {
      throw new Error(`Unsupported argument ${name}.`);
    }
  }

  return {
    version: values.get('--version'),
    nuGetDirectory: values.get('--nuget-dir'),
    npmDirectory: values.get('--npm-dir'),
  };
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  const observations = await checkPackageVersionAvailability({
    ...options,
    nuGetFlatContainerUrl:
      process.env.COHESIVE_NUGET_FLAT_CONTAINER_URL ?? defaultNuGetFlatContainerUrl,
    npmRegistryUrl: process.env.COHESIVE_NPM_REGISTRY_URL ?? defaultNpmRegistryUrl,
  });

  for (const observation of observations) {
    console.log(`Available: ${observation.coordinate}`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
