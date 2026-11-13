// Copies the repo files the site publishes into the places Astro reads them from, so each one has a single
// source of truth outside site/. Runs before every dev/build (see package.json). The copies are gitignored.
//
//   CHANGELOG.md              -> src/content/docs/changelog.md                    ("What's new")
//   docs/definition-format.md -> src/content/docs/reference/definition-format.md
//   docs/schema/              -> public/schema/   (served at each schema's $id, /sprig/schema/v1/repo.json)
//   install.ps1               -> public/install.ps1   (byte-for-byte)
//   landing-icon.png          -> src/assets/logo.png + public/favicon.png
//
// Only these named files are published. The rest of docs/ is internal plans and history, never site content.

import { copyFileSync, cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const site = join(dirname(fileURLToPath(import.meta.url)), '..');
const root = join(site, '..');

function ensureDir(file) {
  mkdirSync(dirname(file), { recursive: true });
}

function copy(from, to) {
  const dest = join(site, to);
  ensureDir(dest);
  copyFileSync(join(root, from), dest);
}

// Links between the published files are written repo-relative, for GitHub; on the site they point at pages.
const pageLinks = {
  'docs/definition-format.md': '/sprig/reference/definition-format/',
};

// Swap a Markdown file's leading "# Title" for Starlight frontmatter.
function page(from, to, title, description) {
  let body = readFileSync(join(root, from), 'utf8').replace(/^﻿?#\s+[^\n]*\n/, '');
  for (const [file, url] of Object.entries(pageLinks)) body = body.replaceAll(`](${file})`, `](${url})`);
  const front = `---\ntitle: ${JSON.stringify(title)}\ndescription: ${JSON.stringify(description)}\n` +
    `editUrl: https://github.com/ArcticGizmo/sprig/edit/main/${from}\n---\n`;
  const dest = join(site, to);
  ensureDir(dest);
  writeFileSync(dest, front + body);
}

page('CHANGELOG.md', 'src/content/docs/changelog.md', "What's new", 'Every sprig release, newest first.');
page('docs/definition-format.md', 'src/content/docs/reference/definition-format.md', 'Definition format',
  'Every key of sprig.yaml and *.orchestration.yaml.');

// Replaced whole, so a schema version removed from docs/schema doesn't linger in a local build.
rmSync(join(site, 'public/schema'), { recursive: true, force: true });
cpSync(join(root, 'docs/schema'), join(site, 'public/schema'), { recursive: true });

copy('install.ps1', 'public/install.ps1');
copy('landing-icon.png', 'src/assets/logo.png');
copy('landing-icon.png', 'public/favicon.png');
