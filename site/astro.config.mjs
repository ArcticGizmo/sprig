// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// Served as a GitHub Pages project site, so every URL lives under /sprig. Links written in Markdown must
// include the base themselves (Starlight only rewrites its own navigation).
export default defineConfig({
  site: 'https://arcticgizmo.github.io',
  base: '/sprig',
  integrations: [
    starlight({
      title: 'Sprig',
      description: 'Describe your repos in code. Bring any slice of them up, isolated and fully wired.',
      logo: { src: './src/assets/logo.png' },
      favicon: '/favicon.png',
      customCss: ['./src/styles/custom.css'],
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/ArcticGizmo/sprig' }],
      editLink: { baseUrl: 'https://github.com/ArcticGizmo/sprig/edit/main/site/' },
      lastUpdated: true,
      sidebar: [
        {
          label: 'Getting started',
          items: [
            { label: 'Install', slug: 'getting-started/install' },
            { label: 'Quick start', slug: 'getting-started/quick-start' },
          ],
        },
        {
          label: 'Guides',
          items: [
            { label: 'Workspaces', slug: 'guides/workspaces' },
            { label: 'Editor support', slug: 'guides/editor-support' },
          ],
        },
        {
          label: 'Reference',
          items: [
            { label: 'Definition format', slug: 'reference/definition-format' },
            { label: 'Commands', slug: 'reference/commands' },
          ],
        },
        { label: "What's new", slug: 'changelog' },
        { label: 'Contributing', slug: 'contributing' },
      ],
    }),
  ],
});
