#!/usr/bin/env node
import { execFileSync } from 'node:child_process';

function git(args) {
  return execFileSync('git', args, { encoding: 'utf8' }).trim();
}

let range = '';

try {
  const lastTag = git(['describe', '--tags', '--abbrev=0']);
  range = `${lastTag}..HEAD`;
} catch {
  range = 'HEAD';
}

const log = git(['log', '--pretty=format:%s', range]);
const sections = new Map([
  ['feat', []],
  ['fix', []],
  ['docs', []],
  ['refactor', []],
  ['test', []],
  ['build', []],
  ['chore', []],
]);

for (const line of log.split('\n').filter(Boolean)) {
  const match = /^(?<type>[a-z]+)(\([^)]+\))?!?:\s*(?<message>.+)$/.exec(line);
  const type = match?.groups?.type;
  const message = match?.groups?.message ?? line;
  const section = sections.get(type ?? '') ?? sections.get('chore');
  section.push(message);
}

for (const [type, items] of sections) {
  if (items.length === 0) {
    continue;
  }

  console.log(`## ${type}`);
  for (const item of items) {
    console.log(`- ${item}`);
  }
  console.log();
}
