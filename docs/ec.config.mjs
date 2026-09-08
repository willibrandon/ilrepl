// Code blocks tagged cil or ilrepl are coloured with the terminal's own tokenizer and palette:
// scripts/Highlight-Cil.cs writes their tokens to src/generated/cil-tokens.json, and this plugin
// lays those colours on the rendered lines, the terminal's own on the dark theme and the same
// roles in the palette for a light ground on the light one. Shiki never sees the two languages.
import { definePlugin, InlineStyleAnnotation } from '@expressive-code/core';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';

const map = JSON.parse(readFileSync(new URL('./src/generated/cil-tokens.json', import.meta.url), 'utf8'));
const terminalLanguages = new Set(['cil', 'ilrepl']);

// The block's text as the generator keys it: its lines, trailing blank lines dropped, joined by newlines.
const key = (code) => {
  const lines = code.split('\n');
  while (lines.length > 0 && lines[lines.length - 1].trim().length === 0) lines.pop();
  return createHash('sha256').update(lines.join('\n'), 'utf8').digest('hex');
};

const terminalColours = definePlugin({
  name: 'cil-terminal',
  hooks: {
    postprocessAnalyzedCode: ({ codeBlock, styleVariants }) => {
      if (!terminalLanguages.has(codeBlock.language)) return;
      const block = map.blocks[key(codeBlock.code)];
      if (!block) return;
      codeBlock.getLines().forEach((line, i) => {
        for (const [start, length, style] of block.lines[i] ?? []) {
          styleVariants.forEach((variant, styleVariantIndex) => {
            const palette = variant.theme.type === 'light' ? map.palette.light : map.palette.dark;
            line.addAnnotation(new InlineStyleAnnotation({
              inlineRange: { columnStart: start, columnEnd: start + length },
              color: palette[style],
              underline: style === 'Error',
              styleVariantIndex,
            }));
          });
        }
      });
    },
  },
});

export default {
  // The two languages exist so the blocks are not reported as unknown; they carry no grammar,
  // since the colours come from the plugin.
  shiki: {
    langs: [
      { name: 'cil', scopeName: 'source.cil', patterns: [] },
      { name: 'ilrepl', scopeName: 'source.ilrepl', patterns: [] },
    ],
  },
  plugins: [terminalColours],
};
