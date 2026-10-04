using Elarion.Settings;

namespace Elarion.Tests.Settings;

internal static class SettingsStoreTestExtensions {
    public static async ValueTask<string?> GetValueAsync(
        this ISettingsStore store, SettingsScope scope, string key, CancellationToken cancellationToken) {
        return (await store.GetAsync(scope, key, cancellationToken))?.Value;
    }
}
