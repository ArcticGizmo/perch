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
      description: 'Know the moment a Claude Code session needs you, and get straight back to it.',
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
            { label: 'First run', slug: 'getting-started/first-run' },
            { label: 'Updating', slug: 'getting-started/updating' },
          ],
        },
        {
          // One page per feature; order comes from each page's `sidebar.order` frontmatter.
          label: 'Features',
          items: [{ autogenerate: { directory: 'features' } }],
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
