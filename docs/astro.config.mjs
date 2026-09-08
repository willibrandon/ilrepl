// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// https://astro.build/config
export default defineConfig({
  site: 'https://ilrepl.dev',
  integrations: [
    starlight({
      title: 'ilrepl',
      description: 'An interactive CIL REPL with a live evaluation stack.',
      logo: { src: './src/assets/icon.svg', alt: 'ilrepl' },
      favicon: '/favicon.svg',
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/willibrandon/ilrepl' }],
      customCss: ['./src/styles/custom.css'],
      sidebar: [
        {
          label: 'Getting started',
          items: [
            { label: 'Installation', slug: 'getting-started/installation' },
            { label: 'Quick start', slug: 'getting-started/quick-start' },
          ],
        },
        {
          label: 'Usage',
          items: [
            { label: 'Cells and the stack', slug: 'usage/cells' },
            { label: 'Member references', slug: 'usage/member-references' },
            { label: 'Exception blocks', slug: 'usage/exception-blocks' },
            { label: 'Methods', slug: 'usage/methods' },
            { label: 'Editing blocks', slug: 'usage/editing' },
            { label: 'Disassembly', slug: 'usage/disassembly' },
            { label: 'Types', slug: 'usage/types' },
            { label: 'Arguments and generics', slug: 'usage/arguments-and-generics' },
            { label: 'Saving cells', slug: 'usage/saving-cells' },
            { label: 'Commands', slug: 'usage/commands' },
          ],
        },
        {
          label: 'Try it',
          items: [{ label: 'Live session', slug: 'try' }],
        },
        {
          label: 'Reference',
          items: [
            { label: 'Command line', slug: 'reference/cli' },
            { label: 'Keyboard', slug: 'reference/keyboard' },
            { label: 'Opcodes', slug: 'reference/opcodes' },
            { label: 'How it works', slug: 'reference/architecture' },
          ],
        },
      ],
    }),
  ],
});
