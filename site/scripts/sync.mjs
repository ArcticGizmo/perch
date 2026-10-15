// Copies the repo-root files the site publishes into the places Astro reads them from, so each one has a single
// source of truth at the root. Runs before every dev/build (see package.json). The copies are gitignored.
//
//   CHANGELOG.md     -> src/content/docs/changelog.md   ("What's new"; frontmatter added, the H1 dropped)
//   PRIVACY.md       -> src/content/docs/privacy.md     (frontmatter added, the H1 dropped)
//   install.ps1      -> public/install.ps1              (byte-for-byte; served at /perch/install.ps1)
//   landing-icon.png -> src/assets/logo.png + public/favicon.png

import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
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

// Swap a Markdown file's leading "# Title" for Starlight frontmatter.
function page(from, to, title, description) {
  const body = readFileSync(join(root, from), 'utf8').replace(/^﻿?#\s+[^\n]*\n/, '');
  const front = `---\ntitle: ${JSON.stringify(title)}\ndescription: ${JSON.stringify(description)}\n` +
    `editUrl: https://github.com/ArcticGizmo/perch/edit/main/${from}\n---\n`;
  const dest = join(site, to);
  ensureDir(dest);
  writeFileSync(dest, front + body);
}

page('CHANGELOG.md', 'src/content/docs/changelog.md', "What's new", 'Every Perch release, newest first.');
page('PRIVACY.md', 'src/content/docs/privacy.md', 'Privacy', 'What Perch reads, what leaves your machine, and how to delete it.');
copy('install.ps1', 'public/install.ps1');
copy('landing-icon.png', 'src/assets/logo.png');
copy('landing-icon.png', 'public/favicon.png');
