using AwesomeAssertions;
using Elarion.AppPasswords;
using Xunit;

namespace Elarion.Tests.AppPasswords;

public sealed class AppPasswordScopeTests {
    [Fact]
    public void Dav_HasTheStoredValueDav() {
        AppPasswordScope.Dav.Value.Should().Be("dav");
        AppPasswordScope.Dav.Kind.Should().Be(AppPasswordScopeKind.Dav);
        AppPasswordScope.Parse("dav").Should().BeSameAs(AppPasswordScope.Dav);
    }

    [Fact]
    public void CalendarFeed_AddressesOneCalendarBySourceAndKey() {
        var scope = AppPasswordScope.CalendarFeed("shifts", "u-42_a");

        scope.Value.Should().Be("calendar-feed:shifts.u-42_a");
        scope.Kind.Should().Be(AppPasswordScopeKind.CalendarFeed);
        scope.CalendarSource.Should().Be("shifts");
        scope.CalendarKey.Should().Be("u-42_a");
        AppPasswordScope.Parse(scope.Value).Should().Be(scope);
        (AppPasswordScope.Parse(scope.Value) == scope).Should().BeTrue();
    }

    [Theory]
    [InlineData("", "key")]
    [InlineData("shifts", "")]
    [InlineData("sh.ifts", "key")]
    [InlineData("shifts", "a/b")]
    [InlineData("schichtpläne", "key")]
    public void CalendarFeed_RefusesASegmentOutsideTheGrammar(string source, string key) {
        var create = () => AppPasswordScope.CalendarFeed(source, key);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CalendarFeed_RefusesASegmentLongerThan64() {
        var create = () => AppPasswordScope.CalendarFeed(new string('a', 65), "key");

        create.Should().Throw<ArgumentException>();
        AppPasswordScope.CalendarFeed(new string('a', 64), new string('b', 64)).Value.Length.Should().Be(143);
    }

    [Theory]
    [InlineData("carddav")]
    [InlineData("contact-feed:people.all")]
    public void Parse_KeepsAScopeOfALaterVersionAsOther(string value) {
        var scope = AppPasswordScope.Parse(value);

        scope.Kind.Should().Be(AppPasswordScopeKind.Other);
        scope.Value.Should().Be(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("DAV")]
    [InlineData("calendar-feed:shifts")]
    [InlineData("calendar-feed:.key")]
    [InlineData("has space")]
    public void TryParse_RefusesMalformedValues(string value) {
        AppPasswordScope.TryParse(value, out _).Should().BeFalse();
    }
}
