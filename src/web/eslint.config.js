import tseslint from 'typescript-eslint';
import reactHooks from 'eslint-plugin-react-hooks';

export default tseslint.config(
  { ignores: ['dist', 'coverage', 'node_modules', '.npm-cache', 'test-results', 'playwright-report'] },
  ...tseslint.configs.recommended,
  reactHooks.configs.flat.recommended,
);
