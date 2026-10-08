import { describe, expect, it, vi } from 'vitest'
import { createInstallPrompt, installOption, isAppleTouchDevice, type InstallEnvironment } from '../src/index.js'

const IPHONE =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1'
// iPadOS presents a desktop Mac user agent; so does a real Mac.
const MAC_USER_AGENT =
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15'
const ANDROID_CHROME =
  'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Mobile Safari/537.36'
const WINDOWS_FIREFOX = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:131.0) Gecko/20100101 Firefox/131.0'

const env = (userAgent: string, maxTouchPoints: number, standalone = false): InstallEnvironment => ({
  userAgent,
  maxTouchPoints,
  standalone,
})

describe('installOption', () => {
  it('counts iPhones and touch-screen "Macs" (iPads) as Apple touch devices, real Macs not', () => {
    expect(isAppleTouchDevice(IPHONE, 5)).toBe(true)
    expect(isAppleTouchDevice(MAC_USER_AGENT, 5)).toBe(true)
    expect(isAppleTouchDevice(MAC_USER_AGENT, 0)).toBe(false)
    expect(isAppleTouchDevice(ANDROID_CHROME, 5)).toBe(false)
  })

  it('offers a captured prompt wherever one exists', () => {
    expect(installOption(env(ANDROID_CHROME, 5), true)).toBe('prompt')
  })

  it('offers the share-sheet instructions on iPhone and iPad', () => {
    expect(installOption(env(IPHONE, 5), false)).toBe('ios')
    expect(installOption(env(MAC_USER_AGENT, 5), false)).toBe('ios')
  })

  it('offers nothing once installed, prompt or not', () => {
    expect(installOption(env(IPHONE, 5, true), false)).toBe('none')
    expect(installOption(env(ANDROID_CHROME, 5, true), true)).toBe('none')
  })

  it('offers no dead end where a browser cannot install, or outside a browser', () => {
    expect(installOption(env(WINDOWS_FIREFOX, 0), false)).toBe('none')
    // Chromium before the page qualified: the entry appears once the prompt arrives.
    expect(installOption(env(ANDROID_CHROME, 5), false)).toBe('none')
    expect(installOption(env(MAC_USER_AGENT, 0), false)).toBe('none')
    expect(installOption(null, true)).toBe('none')
  })
})

function promptEvent(outcome: 'accepted' | 'dismissed' = 'accepted', failPrompt = false) {
  const event = new Event('beforeinstallprompt', { cancelable: true }) as Event & {
    prompt: ReturnType<typeof vi.fn>
    userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
  }
  event.prompt = vi.fn(async () => {
    if (failPrompt) throw new DOMException('Not allowed', 'NotAllowedError')
  })
  event.userChoice = Promise.resolve({ outcome })
  return event
}

describe('createInstallPrompt', () => {
  function setup(environment: InstallEnvironment = env(ANDROID_CHROME, 5)) {
    const target = new EventTarget()
    const install = createInstallPrompt({ target, environment: () => environment })
    const changes = vi.fn()
    install.subscribe(changes)
    return { target, install, changes }
  }

  it('captures the prompt, hides the browser infobar, and notifies subscribers', () => {
    const { target, install, changes } = setup()
    expect(install.availability()).toBe('none')

    const event = promptEvent()
    target.dispatchEvent(event)

    expect(event.defaultPrevented).toBe(true)
    expect(install.availability()).toBe('prompt')
    expect(changes).toHaveBeenCalledTimes(1)
  })

  it('spends the prompt on first use, whatever the answer', async () => {
    const { target, install } = setup()
    const event = promptEvent('dismissed')
    target.dispatchEvent(event)

    expect(await install.prompt()).toBe('dismissed')
    expect(event.prompt).toHaveBeenCalledTimes(1)
    expect(install.availability()).toBe('none')
    expect(await install.prompt()).toBe('unavailable')
  })

  it('reports an accepted install and a refused prompt call', async () => {
    const accepted = setup()
    accepted.target.dispatchEvent(promptEvent('accepted'))
    expect(await accepted.install.prompt()).toBe('accepted')

    const refused = setup()
    refused.target.dispatchEvent(promptEvent('accepted', true))
    expect(await refused.install.prompt()).toBe('unavailable')
  })

  it('drops the prompt once the app was installed', () => {
    const { target, install } = setup()
    target.dispatchEvent(promptEvent())

    target.dispatchEvent(new Event('appinstalled'))

    expect(install.availability()).toBe('none')
  })

  it('offers the iOS instructions without any event', () => {
    expect(setup(env(IPHONE, 5)).install.availability()).toBe('ios')
  })

  it('stops listening on dispose', () => {
    const { target, install, changes } = setup()
    install.dispose()
    target.dispatchEvent(promptEvent())
    expect(install.availability()).toBe('none')
    expect(changes).not.toHaveBeenCalled()
  })

  it('is inert outside a browser', async () => {
    const install = createInstallPrompt()
    expect(install.availability()).toBe('none')
    expect(await install.prompt()).toBe('unavailable')
  })
})
