const maxDecimal = '79228162514264337593543950335'

export function normalizeDecimal(value: string): string | null {
  const normalized = value.trim().replace(',', '.')
  if (!/^\d+(?:\.\d+)?$/.test(normalized)) return null
  if ((normalized.split('.')[1]?.length ?? 0) > 28) return null
  return compareDecimals(normalized, maxDecimal) <= 0 ? normalized : null
}

export function compareDecimals(left: string, right: string): number {
  const [leftInteger, leftFraction = ''] = left.split('.')
  const [rightInteger, rightFraction = ''] = right.split('.')
  const integerLeft = leftInteger.replace(/^0+/, '') || '0'
  const integerRight = rightInteger.replace(/^0+/, '') || '0'
  if (integerLeft.length !== integerRight.length) return Math.sign(integerLeft.length - integerRight.length)
  if (integerLeft !== integerRight) return integerLeft < integerRight ? -1 : 1
  const width = Math.max(leftFraction.length, rightFraction.length)
  const fractionLeft = leftFraction.padEnd(width, '0')
  const fractionRight = rightFraction.padEnd(width, '0')
  return fractionLeft === fractionRight ? 0 : fractionLeft < fractionRight ? -1 : 1
}
