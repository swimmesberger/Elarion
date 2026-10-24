using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Elarion.Settings.InProcess;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// Regression guard for the per-model SQL caches: they are static, so a strongly keyed cache would keep every
/// EF model a process ever built alive. A test suite that builds one model per host then runs out of memory.
/// The settings store stands in for every store that caches model-derived SQL the same way.
/// </summary>
public sealed class EfCoreSettingsStoreModelCacheTests(SqliteSettingsStoreFixture fixture)
    : IClassFixture<SqliteSettingsStoreFixture> {
    [Fact]
    public async Task CachedSql_DoesNotKeepTheModelAlive() {
        var model = await WriteThroughOwnModelAsync();

        // A leaking static cache roots the model forever, but work the disposed context left behind (a thread-pool item
        // still finishing, a scan of its internal caches) can root it for a moment on a busy machine; collecting once
        // more after a short pause tells the two apart without making the test slow when it passes.
        for (var attempt = 0; attempt < 50 && model.IsAlive; attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (model.IsAlive) {
                await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
            }
        }

        model.IsAlive.Should().BeFalse("only a static cache keeps a disposed context's model alive for a whole second");
    }

    // Separate, non-inlined frame so no local in the test method roots the context or its model.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> WriteThroughOwnModelAsync() {
        await using var context = fixture.CreateContextWithOwnModel();
        var changeSource = new InProcessSettingsChangeSource();
        var dispatch = new SettingsChangeDispatchScope(changeSource, NullLogger<SettingsChangeDispatchScope>.Instance);
        var store = new EfCoreSettingsStore<SettingsIntegrationDbContext>(
            context, new ChangePublisherSettingsChangeNotifier(dispatch), TimeProvider.System);
        var key = $"app:{Guid.CreateVersion7():N}";

        await store.SetAsync(SettingsScope.Global, key, "v1", null, cancellationToken: TestContext.Current.CancellationToken);
        await store.SetAsync(SettingsScope.Global, key, "v2", null, cancellationToken: TestContext.Current.CancellationToken);

        return new WeakReference(context.Model);
    }
}
