import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test, { after } from 'node:test';
import { promisify } from 'node:util';

import {
  checkPackageVersionAvailability,
  PublishedPackageVersionError,
  readNpmPackageMetadata,
} from './check-package-version-availability.mjs';

const execFileAsync = promisify(execFile);
const version = '0.1.0-alpha.99';
const temporaryRoots = [];

after(async () => {
  await Promise.all(temporaryRoots.map((root) => rm(root, { recursive: true, force: true })));
});

async function createArtifacts({ npmVersion = version } = {}) {
  const root = await mkdtemp(path.join(tmpdir(), 'cohesive-release-preflight-'));
  temporaryRoots.push(root);
  const nuGetDirectory = path.join(root, 'nuget');
  const npmDirectory = path.join(root, 'npm');
  const npmContents = path.join(root, 'npm-contents', 'package');
  await Promise.all([
    mkdir(nuGetDirectory, { recursive: true }),
    mkdir(npmDirectory, { recursive: true }),
    mkdir(npmContents, { recursive: true }),
  ]);

  await writeFile(path.join(nuGetDirectory, `Cohesive.Model.${version}.nupkg`), 'fixture');
  await writeFile(
    path.join(npmContents, 'package.json'),
    JSON.stringify({ name: '@cohesivesystems/relations', version: npmVersion }),
  );
  const npmArtifact = path.join(npmDirectory, `cohesivesystems-relations-${npmVersion}.tgz`);
  await execFileAsync('tar', ['-czf', npmArtifact, '-C', path.join(root, 'npm-contents'), 'package']);

  return { nuGetDirectory, npmDirectory, npmArtifact };
}

test('accepts a coherent release set when every registry coordinate is absent', async () => {
  const artifacts = await createArtifacts();
  const requestedUrls = [];

  const observations = await checkPackageVersionAvailability({
    version,
    nuGetDirectory: artifacts.nuGetDirectory,
    npmDirectory: artifacts.npmDirectory,
    nuGetFlatContainerUrl: 'https://nuget.test/v3-flatcontainer/',
    npmRegistryUrl: 'https://npm.test/',
    requestStatus: async (url, method) => {
      requestedUrls.push(`${method} ${url}`);
      return 404;
    },
  });

  assert.deepEqual(
    observations.map((entry) => entry.coordinate),
    [`Cohesive.Model@${version}`, `@cohesivesystems/relations@${version}`],
  );
  assert.deepEqual(requestedUrls.sort(), [
    `GET https://npm.test/@cohesivesystems%2Frelations/${version}`,
    `HEAD https://nuget.test/v3-flatcontainer/cohesive.model/${version}/cohesive.model.${version}.nupkg`,
  ]);
});

test('reports every already-published coordinate before publishing begins', async () => {
  const artifacts = await createArtifacts();

  await assert.rejects(
    checkPackageVersionAvailability({
      version,
      nuGetDirectory: artifacts.nuGetDirectory,
      npmDirectory: artifacts.npmDirectory,
      requestStatus: async () => 200,
    }),
    (error) => {
      assert.ok(error instanceof PublishedPackageVersionError);
      assert.deepEqual(
        error.packages.map((entry) => entry.coordinate),
        [`Cohesive.Model@${version}`, `@cohesivesystems/relations@${version}`],
      );
      assert.match(error.message, /Choose a new release version/);
      return true;
    },
  );
});

test('fails closed when a registry cannot establish availability', async () => {
  const artifacts = await createArtifacts();

  await assert.rejects(
    checkPackageVersionAvailability({
      version,
      nuGetDirectory: artifacts.nuGetDirectory,
      requestStatus: async () => 503,
    }),
    /HTTP 503.*Cohesive\.Model/,
  );
});

test('rejects an npm artifact whose embedded version differs from the release', async () => {
  const artifacts = await createArtifacts({ npmVersion: '0.1.0-alpha.98' });

  await assert.rejects(
    checkPackageVersionAvailability({
      version,
      nuGetDirectory: artifacts.nuGetDirectory,
      npmDirectory: artifacts.npmDirectory,
      requestStatus: async () => 404,
    }),
    /declares version 0\.1\.0-alpha\.98; expected 0\.1\.0-alpha\.99/,
  );
});

test('reads the authoritative name and version from an npm tarball', async () => {
  const artifacts = await createArtifacts();
  assert.deepEqual(await readNpmPackageMetadata(artifacts.npmArtifact), {
    name: '@cohesivesystems/relations',
    version,
  });
});
