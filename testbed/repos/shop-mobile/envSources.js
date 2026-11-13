import fs from 'node:fs'
import path from 'node:path'

// Which file gave each VITE_ value, in Vite's own precedence order (later wins), with the process environment on
// top. Vite merges these without saying where a value came from; the config page needs to.
export function envSources(root, mode) {
  const files = ['.env', '.env.local', `.env.${mode}`, `.env.${mode}.local`]
  const sources = {}
  for (const file of files) {
    const full = path.join(root, file)
    if (!fs.existsSync(full)) continue
    for (const line of fs.readFileSync(full, 'utf8').split(/\r?\n/)) {
      const match = /^\s*(VITE_[A-Za-z0-9_]+)\s*=/.exec(line)
      if (match) sources[match[1]] = file
    }
  }
  for (const key of Object.keys(process.env)) {
    if (key.startsWith('VITE_')) sources[key] = 'environment variable'
  }
  return sources
}
