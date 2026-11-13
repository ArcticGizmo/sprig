import { defineConfig, loadEnv } from 'vite'
import vue from '@vitejs/plugin-vue'
import { envSources } from './envSources.js'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  return {
    plugins: [vue()],
    define: {
      __ENV_SOURCES__: JSON.stringify(envSources(process.cwd(), mode)),
    },
    server: {
      // PORT from the environment or an env file, so whatever starts the app can choose it.
      port: Number(process.env.PORT ?? env.PORT ?? 8100),
      strictPort: true,
    },
  }
})
