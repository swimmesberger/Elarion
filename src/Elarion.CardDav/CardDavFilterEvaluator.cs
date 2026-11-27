using System.Text;
using Elarion.Contacts;
using Elarion.WebDav;

namespace Elarion.CardDav;

/// <summary>
/// Evaluates a CardDAV <c>addressbook-query</c> filter (RFC 6352 §10.5) against the property list of one card (spec 0007
/// FR-022, FR-023): <c>prop-filter</c> and <c>param-filter</c> by name (a property's group is ignored, so
/// <c>item1.EMAIL</c> is an <c>EMAIL</c>), <c>is-not-defined</c>, <c>text-match</c> with the match types and the
/// collations of <see cref="DavTextMatch"/> (<c>i;unicode-casemap</c> by default) and <c>negate-condition</c>, and
/// <c>test="anyof|allof"</c> on the filter and on each <c>prop-filter</c>. It matches the card's properties, not the
/// <see cref="Contact"/> model, so <c>X-</c> properties and parameters are searchable.
/// </summary>
internal static class CardDavFilterEvaluator {
    /// <summary>A problem with a filter, and the precondition it answers.</summary>
    internal sealed record Problem(DavName Condition, string Message);

    /// <summary>
    /// The problem of <paramref name="filter"/>, or <see langword="null"/> when it can be evaluated: names on every
    /// filter, known collations (<c>supported-collation</c>) and match types and <c>test</c> values (<c>supported-filter</c>).
    /// </summary>
    public static Problem? Validate(DavXmlElement filter) {
        if (!IsTest(filter.Attribute("test"))) return Unsupported($"The test '{filter.Attribute("test")}' is not anyof or allof.");
        foreach (var propFilter in filter.Children) {
            if (!propFilter.Is(DavNames.Card.PropFilter)) return Unsupported($"A CARDDAV:filter holds only prop-filter elements, not {propFilter.Name.LocalName}.");
            if (ValidateFilter(propFilter) is { } problem) return problem;
        }

        return null;

        static Problem? ValidateFilter(DavXmlElement element) {
            if (string.IsNullOrWhiteSpace(element.Attribute("name"))) return Unsupported($"A {element.Name.LocalName} needs a name.");
            if (element.Is(DavNames.Card.PropFilter) && !IsTest(element.Attribute("test")))
                return Unsupported($"The test '{element.Attribute("test")}' is not anyof or allof.");
            foreach (var child in element.Children) {
                if (child.Is(DavNames.Card.TextMatch)) {
                    if (child.Attribute("collation") is { } collation && !DavTextMatch.IsSupported(collation))
                        return new(DavNames.Card.SupportedCollation, $"The collation '{collation}' is not supported.");
                    if (DavTextMatch.ParseMatchType(child.Attribute("match-type")) is null)
                        return Unsupported($"The match type '{child.Attribute("match-type")}' is not supported.");
                }
                else if (child.Is(DavNames.Card.ParamFilter) && element.Is(DavNames.Card.PropFilter)) {
                    if (ValidateFilter(child) is { } problem) return problem;
                }
                else if (!child.Is(DavNames.Card.IsNotDefined)) {
                    return Unsupported($"A {element.Name.LocalName} cannot hold {child.Name.LocalName}.");
                }
            }

            return null;
        }

        static bool IsTest(string? test) => test is null or "anyof" or "allof";
        static Problem Unsupported(string message) => new(DavNames.Card.SupportedFilter, message);
    }

    /// <summary>Whether the card of <paramref name="properties"/> matches <paramref name="filter"/>, a validated <c>CARDDAV:filter</c>.</summary>
    public static bool Matches(IReadOnlyList<ContactProperty> properties, DavXmlElement filter) {
        var propFilters = filter.ChildrenNamed(DavNames.Card.PropFilter).ToList();
        if (propFilters.Count == 0) return true;
        return filter.Attribute("test") == "allof"
            ? propFilters.All(f => MatchPropFilter(properties, f))
            : propFilters.Any(f => MatchPropFilter(properties, f));
    }

    // RFC 6352 §10.5.1: the prop-filter matches when a property of its name passes its tests; is-not-defined when none is there.
    private static bool MatchPropFilter(IReadOnlyList<ContactProperty> properties, DavXmlElement filter) {
        var name = filter.Attribute("name")!.Trim();
        var named = properties.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (filter.Child(DavNames.Card.IsNotDefined) is not null) return named.Count == 0;
        if (named.Count == 0) return false;
        var tests = filter.Children.Where(static c => c.Is(DavNames.Card.TextMatch) || c.Is(DavNames.Card.ParamFilter)).ToList();
        if (tests.Count == 0) return true;
        var all = filter.Attribute("test") == "allof";
        return named.Any(property => all ? tests.All(t => Passes(property, t)) : tests.Any(t => Passes(property, t)));

        static bool Passes(ContactProperty property, DavXmlElement test) =>
            test.Is(DavNames.Card.TextMatch) ? ValueMatches(property, test) : MatchParamFilter(property, test);
    }

    // Properties whose value is a comma-separated list (RFC 6350: NICKNAME, CATEGORIES): each value is matched on its own,
    // as Cyrus does, so "equals eric" finds "foo,eric"; the whole value is matched too, so a search for "foo,eric" still
    // finds it. A negated match holds only when no value matches.
    private static bool ValueMatches(ContactProperty property, DavXmlElement textMatch) {
        var text = Unescape(property.Value);
        if (!property.Name.Equals("NICKNAME", StringComparison.OrdinalIgnoreCase) && !property.Name.Equals("CATEGORIES", StringComparison.OrdinalIgnoreCase))
            return TextMatches(text, textMatch);
        var negate = string.Equals(textMatch.Attribute("negate-condition"), "yes", StringComparison.OrdinalIgnoreCase);
        var found = Matched(text, textMatch) || SplitList(property.Value).Any(value => Matched(value, textMatch));
        return found != negate;

        static bool Matched(string value, DavXmlElement match) => DavTextMatch.Matches(value, match.Text,
            match.Attribute("collation") ?? DavTextMatch.UnicodeCasemap, DavTextMatch.ParseMatchType(match.Attribute("match-type")) ?? DavMatchType.Contains);
    }

    // Splits a still-escaped list value at unescaped commas and unescapes each value.
    private static IEnumerable<string> SplitList(string value) {
        var start = 0;
        for (var i = 0; i < value.Length; i++) {
            if (value[i] == '\\') {
                i++;
                continue;
            }

            if (value[i] != ',') continue;
            yield return Unescape(value[start..i]);
            start = i + 1;
        }

        yield return Unescape(value[start..]);
    }

    // RFC 6352 §10.5.2: a parameter of the name, any of whose values passes the text-match; is-not-defined when absent.
    private static bool MatchParamFilter(ContactProperty property, DavXmlElement filter) {
        var name = filter.Attribute("name")!.Trim();
        var parameters = property.Parameters.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (filter.Child(DavNames.Card.IsNotDefined) is not null) return parameters.Count == 0;
        if (parameters.Count == 0) return false;
        var textMatch = filter.Child(DavNames.Card.TextMatch);
        return textMatch is null || parameters.SelectMany(static p => p.Values).Any(value => TextMatches(value, textMatch));
    }

    // RFC 6352 §10.5.4: i;unicode-casemap and contains by default.
    private static bool TextMatches(string value, DavXmlElement textMatch) {
        var collation = textMatch.Attribute("collation") ?? DavTextMatch.UnicodeCasemap;
        var matchType = DavTextMatch.ParseMatchType(textMatch.Attribute("match-type")) ?? DavMatchType.Contains;
        var found = DavTextMatch.Matches(value, textMatch.Text, collation, matchType);
        var negate = string.Equals(textMatch.Attribute("negate-condition"), "yes", StringComparison.OrdinalIgnoreCase);
        return found != negate;
    }

    // A property value is kept escaped (ADR-0110); the text a person searches for is the unescaped one. Structured values
    // (N, ADR) keep their ';' separators and are matched as a whole, as sabre/vobject does.
    private static string Unescape(string value) {
        if (value.IndexOf('\\') < 0) return value;
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++) {
            if (value[i] != '\\' || i + 1 == value.Length) {
                builder.Append(value[i]);
                continue;
            }

            var next = value[++i];
            builder.Append(next is 'n' or 'N' ? '\n' : next);
        }

        return builder.ToString();
    }
}
