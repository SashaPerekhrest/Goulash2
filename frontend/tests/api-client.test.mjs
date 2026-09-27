import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'

test('CSRF token follows the current authentication state', async () => {
  const source = readFileSync(fileURLToPath(new URL('../src/shared/api/apiClient.ts', import.meta.url)), 'utf8')
  const compiled = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
  }).outputText
  const originalFetch = globalThis.fetch
  const originalWindow = globalThis.window
  let authenticated = false
  let tokenRequests = 0
  let unauthorizedEvents = 0

  globalThis.window = { dispatchEvent: () => { unauthorizedEvents++ } }
  globalThis.fetch = async (input, init) => {
    const path = new URL(input, 'http://localhost').pathname
    const token = new Headers(init?.headers).get('X-CSRF-Token')
    const expectedToken = authenticated ? 'authenticated-token' : 'anonymous-token'

    if (path === '/api/v1/auth/csrf') {
      tokenRequests++
      return Response.json({ csrfToken: expectedToken })
    }
    if (path === '/api/v1/auth/session') {
      return authenticated
        ? Response.json({ authenticated: true })
        : Response.json({ code: 'UNAUTHORIZED' }, { status: 401 })
    }
    if (path === '/api/v1/ai/settings' && init?.method === 'GET') {
      return authenticated
        ? Response.json({ hasApiKey: false })
        : Response.json({ code: 'UNAUTHORIZED' }, { status: 401 })
    }
    if (token !== expectedToken) {
      return Response.json({ code: 'CSRF_INVALID' }, { status: 403 })
    }
    if (path === '/api/v1/auth/login') {
      authenticated = true
      return new Response(null, { status: 204 })
    }
    if (path === '/api/v1/auth/logout') {
      authenticated = false
      return new Response(null, { status: 204 })
    }
    if (path === '/api/v1/ai/settings' && init?.method === 'PUT') {
      return Response.json({ providerId: 'perplexity', model: 'sonar', hasApiKey: true })
    }
    throw new Error(`Unexpected request: ${init?.method} ${path}`)
  }

  try {
    const client = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`)
    await client.login('password')
    await client.updateAiSettings({ providerId: 'perplexity', model: 'sonar', apiKey: 'secret' })
    await client.logout()
    await client.login('password')
    assert.equal(tokenRequests, 3)

    authenticated = false // The server expires the session without a client-side logout.
    await assert.rejects(client.getAiSettings(), { status: 401 })
    assert.equal(unauthorizedEvents, 1)
    await client.login('password')
    assert.equal(tokenRequests, 4)
  } finally {
    globalThis.fetch = originalFetch
    globalThis.window = originalWindow
  }
})
