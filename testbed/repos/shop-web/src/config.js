import { ref } from 'vue'

/* global __ENV_SOURCES__ */
const sources = __ENV_SOURCES__

// The values this app runs on: what each is, where it came from, and whether the service it names answers.
export const settings = [
  { key: 'VITE_CATALOG_URL', value: import.meta.env.VITE_CATALOG_URL },
  { key: 'VITE_ORDERS_URL', value: import.meta.env.VITE_ORDERS_URL },
].map((s) => ({ ...s, source: sources[s.key] ?? 'not set', status: ref('checking') }))

export async function probeAll() {
  await Promise.all(settings.map(async (s) => {
    if (!s.value) { s.status.value = 'not set'; return }
    try {
      const res = await fetch(`${s.value}/health`)
      const body = await res.json()
      s.status.value = `${body.app}: ${body.status}`
    } catch {
      s.status.value = 'nothing answering'
    }
  }))
}
