import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'

const source = readFileSync(fileURLToPath(new URL('../src/pages/discoveryDecimal.ts', import.meta.url)), 'utf8')
const compiled = ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
}).outputText
const { normalizeDecimal, compareDecimals } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`)

test('decimal bounds retain precision beyond JavaScript Number', () => {
  const lower = normalizeDecimal('1.0000000000000000000000000000')
  const higher = normalizeDecimal('1.0000000000000000000000000001')
  assert.equal(compareDecimals(higher, lower), 1)
  assert.equal(compareDecimals(lower, higher), -1)
  assert.equal(compareDecimals('001.20', '1.2'), 0)
})

test('bounds outside the server decimal range are rejected before sending', () => {
  assert.equal(normalizeDecimal('79228162514264337593543950335'), '79228162514264337593543950335')
  assert.equal(normalizeDecimal('79228162514264337593543950336'), null)
  assert.equal(normalizeDecimal('1.00000000000000000000000000001'), null)
  assert.equal(normalizeDecimal('-1'), null)
})
