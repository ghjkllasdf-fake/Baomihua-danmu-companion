import eslint from '@eslint/js';
import tseslint from 'typescript-eslint';

export default [
  { ignores: ['dist/**', 'node_modules/**', 'test-results/**', 'playwright-report/**', 'e2e/*.mjs'] },
  eslint.configs.recommended,
  ...tseslint.configs.recommended,
];
