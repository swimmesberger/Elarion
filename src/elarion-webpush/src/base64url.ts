/**
 * Decodes a base64url string (the VAPID public key as the server hands it out) into the bytes
 * `pushManager.subscribe({ applicationServerKey })` expects.
 */
export function urlBase64ToUint8Array(value: string): Uint8Array<ArrayBuffer> {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (value.length % 4)) % 4)
  const binary = atob(base64)
  const bytes = new Uint8Array(new ArrayBuffer(binary.length))
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return bytes
}

/** Whether a subscription's `options.applicationServerKey` is the given key. */
export function isSameKey(current: ArrayBuffer | null | undefined, expected: Uint8Array): boolean {
  if (!current || current.byteLength !== expected.byteLength) return false
  const actual = new Uint8Array(current)
  for (let i = 0; i < actual.length; i++) if (actual[i] !== expected[i]) return false
  return true
}
