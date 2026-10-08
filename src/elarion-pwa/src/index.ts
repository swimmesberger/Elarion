// The page half of the Elarion installable-app shell (ADR-0083): how the app can be installed (the deferred
// `beforeinstallprompt` prompt, or the iOS share-sheet instructions), reloading into a new deploy at a safe moment,
// and registering the service worker. Framework-neutral: subscribe/snapshot pairs plug into React's
// `useSyncExternalStore`, and the update watcher takes navigation events from any router.
//
// The service-worker half (the shell router and its caching strategies) is the `/sw` sub-export; the Vite plugin
// that publishes the build version is `/vite`.

export {
  createInstallPrompt,
  installOption,
  isAppleTouchDevice,
  isStandalone,
  readInstallEnvironment,
  type InstallEnvironment,
  type InstallOption,
  type InstallPromptController,
  type InstallPromptOptions,
  type InstallPromptOutcome,
} from './install.js'
export { registerServiceWorker, type RegisterServiceWorkerOptions, type ServiceWorkerContainerLike } from './register.js'
export {
  BUSY_SELECTOR,
  createUpdateWatcher,
  currentEntryScript,
  DEFAULT_CHECK_INTERVAL_MS,
  DEFAULT_RELOAD_GUARD_MS,
  DEFAULT_RELOAD_STORAGE_KEY,
  DEFAULT_VERSION_URL,
  entryScriptVersionSource,
  isDocumentBusy,
  isTextEntry,
  jsonVersionSource,
  parseEntryScript,
  parseServedVersion,
  shouldCheckForUpdate,
  shouldReloadOnNavigation,
  updateDecision,
  type BusyDocument,
  type FocusTarget,
  type ReloadGuardStorage,
  type UpdateDecision,
  type UpdateWatcher,
  type UpdateWatcherOptions,
  type VersionSource,
  type VisibilityDocument,
} from './update.js'
