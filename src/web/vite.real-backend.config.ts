import { defineConfig, mergeConfig } from 'vite';
import normalConfig from './vite.config.ts';

const api = process.env.UI_REAL_API_URL;
if (!api || new URL(api).hostname !== '127.0.0.1' || new URL(api).protocol !== 'http:') {
  throw new Error('The acceptance API must be an explicitly supplied loopback HTTP URL.');
}

// Test-only configuration: normal development remains on its documented port 8080.
export default defineConfig(mergeConfig(normalConfig, {
  server: { proxy: { '/api': api, '/health': api } },
}));
