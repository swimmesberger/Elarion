using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Elarion.Settings.InProcess;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// Unit tests for the commit-gating buffer that defers in-process settings change notifications made inside a
/// caller-owned transaction: it announces immediately when told to, announces the buffer on flush (commit), drops
/// it on discard (rollback), and truncates to a savepoint mark on partial rollback. No database required — the
/// buffer's logic is driven directly against a real in-process change source.
/// </summary>
public sealed class SettingsChangeDispatchScopeTests {
    private static SettingsChangeDispatchScope CreateScope(out InProcessSettingsChangeSource source) {
        source = new InProcessSettingsChangeSource();
        return new SettingsChangeDispatchScope(source, NullLogger<SettingsChangeDispatchScope>.Instance);
    }

    [Fact]
    public void PublishNow_AnnouncesImmediately() {
        var scope = CreateScope(out var source);
        var token = source.Watch(SettingsScope.Global, "app");

        scope.PublishNow(SettingsScope.Global, "app:title");

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void Defer_DoesNotAnnounceUntilFlush() {
        var scope = CreateScope(out var source);
        var token = source.Watch(SettingsScope.Global, "app");

        scope.Defer(SettingsScope.Global, "app:title");

        token.HasChanged.Should().BeFalse();
    }

    [Fact]
    public void Flush_AnnouncesEveryDeferredChange() {
        var scope = CreateScope(out var source);
        var title = source.Watch(SettingsScope.Global, "app:title");
        var smtp = source.Watch(SettingsScope.Global, "app:smtp");
        scope.Defer(SettingsScope.Global, "app:title");
        scope.Defer(SettingsScope.Global, "app:smtp:host");

        scope.Flush();

        title.HasChanged.Should().BeTrue();
        smtp.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void Discard_DropsDeferredChanges() {
        var scope = CreateScope(out var source);
        var token = source.Watch(SettingsScope.Global, "app");
        scope.Defer(SettingsScope.Global, "app:title");

        scope.Discard();
        scope.Flush();

        token.HasChanged.Should().BeFalse();
    }

    [Fact]
    public void RollbackToSavepoint_DropsChangesAfterTheSavepointOnly() {
        var scope = CreateScope(out var source);
        var before = source.Watch(SettingsScope.Global, "app:before");
        var after = source.Watch(SettingsScope.Global, "app:after");
        scope.Defer(SettingsScope.Global, "app:before:x");
        scope.PushSavepoint();
        scope.Defer(SettingsScope.Global, "app:after:y");

        scope.RollbackToSavepoint();
        scope.Flush();

        before.HasChanged.Should().BeTrue();
        after.HasChanged.Should().BeFalse();
    }

    [Fact]
    public void ReleaseSavepoint_KeepsChangesBufferedAfterIt() {
        var scope = CreateScope(out var source);
        var after = source.Watch(SettingsScope.Global, "app:after");
        scope.Defer(SettingsScope.Global, "app:before:x");
        scope.PushSavepoint();
        scope.Defer(SettingsScope.Global, "app:after:y");

        scope.ReleaseSavepoint();
        scope.Flush();

        after.HasChanged.Should().BeTrue();
    }
}
