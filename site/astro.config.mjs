// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// Served as a GitHub Pages project site, so every URL lives under /perch. Links written in Markdown must
// include the base themselves (Starlight only rewrites its own navigation).
export default defineConfig({
  site: 'https://arcticgizmo.github.io',
  base: '/perch',
  integrations: [
    starlight({
      title: 'Perch',
      description: 'Keep an eye on every Claude Code session from one floating panel.',
      logo: { src: './src/assets/logo.png' },
      favicon: '/favicon.png',
      customCss: ['./src/styles/custom.css'],
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/ArcticGizmo/perch' }],
      editLink: { baseUrl: 'https://github.com/ArcticGizmo/perch/edit/main/site/' },
      lastUpdated: true,
      sidebar: [
        {
          label: 'Getting started',
          items: [
            { label: 'Install', slug: 'getting-started/install' },
            { label: 'Updating', slug: 'getting-started/updating' },
          ],
        },
        {
          label: 'Help',
          items: [
            { label: 'FAQ & troubleshooting', slug: 'faq' },
            { label: 'Privacy', slug: 'privacy' },
          ],
        },
        { label: "What's new", slug: 'changelog' },
        { label: 'Contributing', slug: 'contributing' },
      ],
    }),
  ],
});
