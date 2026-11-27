using System.Globalization;
using Elarion.Abstractions.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Elarion.WebDav;

/// <summary>
/// Answers WebDAV requests without a wire (ADR-0111, spec 0006 FR-014–FR-025): <c>OPTIONS</c>, <c>PROPFIND</c>,
/// <c>REPORT</c> (<c>sync-collection</c> here, the rest by the protocols), <c>GET</c> and <c>HEAD</c> over the current
/// user's principal, homes, published collections and their members. Every write method answers <c>403</c> with
/// <c>need-privileges</c>: published collections are read-only. Another user's principal or home, and a collection no
/// protocol resolved for the user, answer <c>404</c>.
/// </summary>
/// <example>
/// <code>
/// var response = await dispatcher.HandleAsync(new DavRequest {
///     Method = "PROPFIND", Path = "/calendars/u1/", HrefBase = "/dav",
///     Headers = new Dictionary&lt;string, string&gt; { ["Depth"] = "1" },
/// }, requestServices, ct);
/// await response.WriteBodyAsync(output, ct);
/// </code>
/// </example>
public sealed class DavDispatcher(IEnumerable<IDavProtocol> protocols, IOptions<WebDavOptions> options) {
    /// <summary>The methods the <c>Allow</c> header lists: no write method, so Evolution sees the calendars as read-only.</summary>
    public const string AllowedMethods = "OPTIONS, GET, HEAD, PROPFIND, REPORT";

    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase) {
        "PUT", "DELETE", "PROPPATCH", "MKCOL", "MKCALENDAR", "MOVE", "COPY", "POST", "PATCH", "LOCK", "UNLOCK", "ACL", "BIND", "UNBIND",
        "REBIND",
    };

    private readonly IReadOnlyList<IDavProtocol> _protocols = [.. protocols];

    /// <summary>The <c>DAV</c> header: <c>1, 3</c> and each protocol's classes, such as <c>calendar-access</c>; never scheduling.</summary>
    public string ComplianceHeader => string.Join(", ", new[] { "1", "3" }.Concat(_protocols.SelectMany(static p => p.ComplianceClasses)).Distinct());

    /// <summary>The registered protocols.</summary>
    public IReadOnlyList<IDavProtocol> Protocols => _protocols;

    /// <summary>Answers <paramref name="request"/> as the user of <paramref name="services"/>.</summary>
    /// <param name="request">The request.</param>
    /// <param name="services">The request's DI scope, seeded with the authenticated user (<see cref="ICurrentUser"/>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async ValueTask<DavResponse> HandleAsync(DavRequest request, IServiceProvider services, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(services);
        var user = services.GetService<ICurrentUser>();
        if (user is not { IsAuthenticated: true } || string.IsNullOrEmpty(user.UserId)) return DavResponse.Status(401);

        var method = request.Method.ToUpperInvariant();
        if (method == "OPTIONS") return Options();
        if (!TryParsePath(request.Path, out var segments)) return DavResponse.Status(404);

        var context = new DavContext(request, services, user, options.Value, _protocols);
        if (WriteMethods.Contains(method)) return await RefuseWriteAsync(context, segments, method, cancellationToken).ConfigureAwait(false);
        if (method is not ("PROPFIND" or "REPORT" or "GET" or "HEAD")) return WithAllow(DavResponse.Status(405));

        var (resource, other) = await ResolveAsync(context, segments, method is "GET" or "HEAD", cancellationToken).ConfigureAwait(false);
        if (other is not null) return other;
        if (resource is null) return DavResponse.Status(404);

        return method switch {
            "PROPFIND" => await PropFindAsync(context, resource, cancellationToken).ConfigureAwait(false),
            "REPORT" => await ReportAsync(context, resource, cancellationToken).ConfigureAwait(false),
            _ => await GetAsync(context, resource, cancellationToken).ConfigureAwait(false),
        };
    }

    private DavResponse Options() {
        var response = DavResponse.Status(200);
        response.Headers["DAV"] = ComplianceHeader;
        return WithAllow(response);
    }

    private static DavResponse WithAllow(DavResponse response) {
        response.Headers["Allow"] = AllowedMethods;
        return response;
    }

    internal static bool TryParsePath(string path, out List<string> segments) {
        segments = [];
        foreach (var raw in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            string segment;
            try {
                segment = Uri.UnescapeDataString(raw);
            }
            catch (UriFormatException) {
                return false;
            }

            if (segment is "." or ".." || segment.Contains('/')) return false;
            segments.Add(segment);
        }

        return true;
    }

    private async ValueTask<(DavResource? Resource, DavResponse? Other)> ResolveAsync(DavContext context, List<string> segments, bool withContent,
        CancellationToken cancellationToken) {
        var userId = context.User.UserId;
        if (segments.Count == 0) return (new DavResource(context, DavResourceKind.Root, context.HrefBase + "/"), null);
        if (segments[0] == "principals") {
            return segments.Count switch {
                1 => (new DavResource(context, DavResourceKind.Principals, context.PrincipalsHref), null),
                2 when segments[1] == userId => (new DavResource(context, DavResourceKind.Principal, context.PrincipalHref), null),
                _ => (null, null),
            };
        }

        var protocol = _protocols.FirstOrDefault(p => string.Equals(p.HomeSegment, segments[0], StringComparison.Ordinal));
        if (protocol is null) {
            foreach (var candidate in _protocols) {
                var answer = await candidate.HandleOtherAsync(context, segments, cancellationToken).ConfigureAwait(false);
                if (answer is not null) return (null, answer);
            }

            return (null, null);
        }

        // Another user's home, or anything below it, does not exist for this user (spec 0006 FR-012).
        if (segments.Count < 2 || segments[1] != userId || segments.Count > 4) return (null, null);
        if (segments.Count == 2)
            return (new DavResource(context, DavResourceKind.Home, context.HomeHref(protocol)) { Protocol = protocol }, null);

        var collections = await context.GetCollectionsAsync(protocol, cancellationToken).ConfigureAwait(false);
        var collection = collections.FirstOrDefault(c => string.Equals(c.Segment, segments[2], StringComparison.Ordinal));
        if (collection is null) return (null, null);
        if (segments.Count == 3) {
            var states = await context.Store.GetStatesAsync([collection.Address], cancellationToken).ConfigureAwait(false);
            return (new DavResource(context, DavResourceKind.Collection, context.CollectionHref(protocol, collection)) {
                Protocol = protocol, Collection = collection, State = states[0],
            }, null);
        }

        DavStoredObject? member = null;
        await foreach (var stored in context.Store.GetObjectsAsync(collection.Address.Id, [segments[3]], withContent, cancellationToken)
                           .ConfigureAwait(false))
            member = stored;
        if (member is null) return (null, null);
        return (new DavResource(context, DavResourceKind.Member, context.MemberHref(protocol, collection, member.Name)) {
            Protocol = protocol, Collection = collection, Member = member,
        }, null);
    }

    // Published collections are read-only (spec 0006 FR-023): a write to anything the user can see is refused with the
    // need-privileges precondition, which DAVx5 reads to discard the local change; to anything else it is a 404.
    private async ValueTask<DavResponse> RefuseWriteAsync(DavContext context, List<string> segments, string method,
        CancellationToken cancellationToken) {
        var (target, other) = await ResolveAsync(context, segments, false, cancellationToken).ConfigureAwait(false);
        if (other is not null) return WithAllow(DavResponse.Status(405));
        var href = target?.Href;
        if (target is null && segments.Count > 0) {
            var (parent, _) = await ResolveAsync(context, segments[..^1], false, cancellationToken).ConfigureAwait(false);
            if (parent is null || parent.Kind is DavResourceKind.Member) return DavResponse.Status(404);
            href = parent.Href + DavContext.Escape(segments[^1]);
        }

        if (href is null) return DavResponse.Status(404);
        var privilege = method switch {
            "PROPPATCH" => DavNames.D.WriteProperties,
            "DELETE" or "MOVE" or "UNBIND" => DavNames.D.Unbind,
            "MKCOL" or "MKCALENDAR" or "BIND" or "COPY" => DavNames.D.Bind,
            _ => DavNames.D.WriteContent,
        };
        return WithAllow(DavResponse.Xml(403, (output, _) => {
            var writer = output.Writer;
            DavXml.StartElement(writer, DavNames.D.Error);
            DavXml.DeclarePrefixes(writer);
            DavXml.StartElement(writer, DavNames.D.NeedPrivileges);
            DavXml.StartElement(writer, DavNames.D.Resource);
            DavXml.TextElement(writer, DavNames.D.Href, href);
            DavXml.StartElement(writer, DavNames.D.Privilege);
            DavXml.EmptyElement(writer, privilege);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            return ValueTask.CompletedTask;
        }));
    }

    private static async ValueTask<DavResponse> GetAsync(DavContext context, DavResource resource, CancellationToken cancellationToken) {
        if (resource.Kind != DavResourceKind.Member || resource.Member is not DavStoredObject stored) return WithAllow(DavResponse.Status(405));
        if (DavETag.Matches(context.Request.Header("If-None-Match"), stored.ETag)) {
            var notModified = DavResponse.Status(304);
            notModified.Headers["ETag"] = stored.ETag;
            return notModified;
        }

        // A protocol may answer another representation the client asked for (CardDAV: vCard 4.0 by Accept); the ETag is
        // the stored object's either way.
        var representation = resource.Protocol is { } protocol
            ? await protocol.GetRepresentationAsync(context, stored, cancellationToken).ConfigureAwait(false)
            : null;
        var response = representation is null
            ? DavResponse.Bytes(200, stored.Content, stored.ContentType)
            : DavResponse.Bytes(200, representation.Content, representation.ContentType);
        response.Headers["ETag"] = stored.ETag;
        return response;
    }

    private async ValueTask<DavResponse> PropFindAsync(DavContext context, DavResource resource, CancellationToken cancellationToken) {
        // RFC 4918 reads a missing Depth as infinity; sabre/dav and the clients surveyed send it, and a missing one is read
        // as 1, which answers every client that omits it without walking a tree.
        var depthHeader = context.Request.Header("Depth")?.Trim();
        int depth;
        if (depthHeader is null) depth = 1;
        else if (depthHeader == "0") depth = 0;
        else if (depthHeader == "1") depth = 1;
        else if (string.Equals(depthHeader, "infinity", StringComparison.OrdinalIgnoreCase)) return DavResponse.Error(403, DavNames.D.PropFindFiniteDepth);
        else return DavResponse.Text(400, "The Depth header must be 0, 1 or infinity.");

        var (body, refusal) = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return refusal;
        DavPropertySelection selection;
        if (body is null) {
            selection = DavPropertySelection.AllProp;
        }
        else if (!body.Is(DavNames.D.PropFind)) {
            return DavResponse.Text(400, "The PROPFIND body must be a DAV:propfind element.");
        }
        else if (body.Child(DavNames.D.PropName) is not null) {
            selection = DavPropertySelection.PropName;
        }
        else if (body.Child(DavNames.D.Prop) is { } prop) {
            selection = DavPropertySelection.Named(prop);
        }
        else {
            selection = DavPropertySelection.AllPropIncluding(body.Child(DavNames.D.Include));
        }

        var minimal = IsMinimal(context.Request);
        List<DavResource>? children = null;
        if (depth == 1 && resource.Kind == DavResourceKind.Home) children = await ListHomeAsync(context, resource.Protocol!, cancellationToken).ConfigureAwait(false);
        if (depth == 1 && resource.Kind == DavResourceKind.Principals)
            children = [new DavResource(context, DavResourceKind.Principal, context.PrincipalHref)];

        var response = DavResponse.Xml(207, async (output, ct) => {
            DavMultistatus.Start(output);
            await DavMultistatus.WriteResourceAsync(output, resource, selection, minimal, ct).ConfigureAwait(false);
            if (children is not null)
                foreach (var child in children)
                    await DavMultistatus.WriteResourceAsync(output, child, selection, minimal, ct).ConfigureAwait(false);
            if (depth == 1 && resource.Kind == DavResourceKind.Collection) {
                await foreach (var member in context.Store.ListMembersAsync(resource.Collection!.Address.Id, ct).ConfigureAwait(false)) {
                    var child = new DavResource(context, DavResourceKind.Member, context.MemberHref(resource.Protocol!, resource.Collection, member.Name)) {
                        Protocol = resource.Protocol, Collection = resource.Collection, Member = member,
                    };
                    await DavMultistatus.WriteResourceAsync(output, child, selection, minimal, ct).ConfigureAwait(false);
                }
            }

            DavMultistatus.End(output);
        });
        // macOS reads the DAV header on PROPFIND answers too (sabre/dav's client notes).
        response.Headers["DAV"] = ComplianceHeader;
        return response;
    }

    private static async ValueTask<List<DavResource>> ListHomeAsync(DavContext context, IDavProtocol protocol, CancellationToken cancellationToken) {
        var collections = await context.GetCollectionsAsync(protocol, cancellationToken).ConfigureAwait(false);
        if (collections.Count == 0) return [];
        var states = await context.Store.GetStatesAsync([.. collections.Select(static c => c.Address)], cancellationToken).ConfigureAwait(false);
        return [
            .. collections.Select((collection, index) =>
                new DavResource(context, DavResourceKind.Collection, context.CollectionHref(protocol, collection)) {
                    Protocol = protocol, Collection = collection, State = states[index],
                }),
        ];
    }

    /// <summary>Whether the client asked for a minimal answer (RFC 7240 <c>return=minimal</c>, or <c>Brief: t</c>).</summary>
    public static bool IsMinimal(DavRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return request.Header("Prefer") is { } prefer && prefer.Contains("return=minimal", StringComparison.OrdinalIgnoreCase)
               || string.Equals(request.Header("Brief")?.Trim(), "t", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads and parses the body within the limits (spec 0006 FR-025): <see langword="null"/> for an empty body, or a
    /// <c>413</c> or <c>400</c> refusal.
    /// </summary>
    internal static async ValueTask<(DavXmlElement? Body, DavResponse? Refusal)> ReadBodyAsync(DavContext context, CancellationToken cancellationToken) {
        var settings = context.Options;
        if (context.Request.ContentLength > settings.MaxBodyBytes)
            return (null, DavResponse.Text(413, $"The body is larger than {settings.MaxBodyBytes} bytes."));
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true) {
            var read = await context.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > settings.MaxBodyBytes)
                return (null, DavResponse.Text(413, $"The body is larger than {settings.MaxBodyBytes} bytes."));
            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0 || IsWhitespace(buffer)) return (null, null);
        var parsed = DavXml.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length),
            new DavXmlLimits(settings.MaxBodyBytes, settings.MaxXmlDepth, settings.MaxXmlElements));
        return parsed.Error switch {
            null => (parsed.Root, null),
            DavXmlErrorKind.TooLarge => (null, DavResponse.Text(413, parsed.Message!)),
            _ => (null, DavResponse.Text(400, parsed.Message!)),
        };

        static bool IsWhitespace(MemoryStream stream) {
            foreach (var b in stream.GetBuffer().AsSpan(0, (int)stream.Length))
                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                    return false;
            return true;
        }
    }

    private async ValueTask<DavResponse> ReportAsync(DavContext context, DavResource resource, CancellationToken cancellationToken) {
        var (body, refusal) = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return refusal;
        if (body is null) return DavResponse.Text(400, "A REPORT needs a body.");
        if (body.Is(DavNames.D.SyncCollection)) {
            return resource.Kind == DavResourceKind.Collection
                ? await SyncCollectionAsync(context, resource, body, cancellationToken).ConfigureAwait(false)
                : DavResponse.Error(403, DavNames.D.SupportedReport);
        }

        if (resource.Kind is DavResourceKind.Collection or DavResourceKind.Member && resource.Protocol is { } protocol) {
            var answer = await protocol.ReportAsync(context, resource, body, cancellationToken).ConfigureAwait(false);
            if (answer is not null) return answer;
        }

        return DavResponse.Error(403, DavNames.D.SupportedReport);
    }

    // RFC 6578 sync-collection, level 1 only: calendars have no child collections.
    private static async ValueTask<DavResponse> SyncCollectionAsync(DavContext context, DavResource resource, DavXmlElement body,
        CancellationToken cancellationToken) {
        var level = body.Child(DavNames.D.SyncLevel)?.Text.Trim();
        if (level is not null && level != "1") return DavResponse.Error(403, DavNames.D.SyncTraversalSupported);
        int? limit = null;
        if (body.Child(DavNames.D.Limit)?.Child(DavNames.D.NResults)?.Text.Trim() is { Length: > 0 } nresults) {
            if (!int.TryParse(nresults, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit) || parsedLimit < 1)
                return DavResponse.Text(400, "DAV:nresults must be a positive integer.");
            limit = parsedLimit;
        }

        var selection = DavPropertySelection.Named(body.Child(DavNames.D.Prop));
        var protocol = resource.Protocol!;
        var collection = resource.Collection!;
        var needsContent = protocol.MemberProperties.Any(p => selection.Names(p.Name));
        var token = body.Child(DavNames.D.SyncToken)?.Text.Trim();
        var minimal = IsMinimal(context.Request);
        var store = context.Store;

        if (string.IsNullOrEmpty(token)) {
            // The initial sync lists the members as they are; it never lists removed ones (RFC 6578 §3.4).
            var state = resource.State!;
            return DavResponse.Xml(207, async (output, ct) => {
                DavMultistatus.Start(output);
                if (needsContent) {
                    await foreach (var stored in store.GetCandidatesAsync(collection.Address.Id, null, null, ct).ConfigureAwait(false))
                        await WriteMemberAsync(output, stored, ct).ConfigureAwait(false);
                }
                else {
                    await foreach (var member in store.ListMembersAsync(collection.Address.Id, ct).ConfigureAwait(false))
                        await WriteMemberAsync(output, member, ct).ConfigureAwait(false);
                }

                DavXml.TextElement(output.Writer, DavNames.D.SyncToken, state.SyncToken);
                DavMultistatus.End(output);
            });
        }

        // A token of another collection, below the journal's low-water mark, or beyond the counter is refused in the form
        // every surveyed client recognises: 403 with valid-sync-token (spec 0006 FR-020).
        if (!DavSyncToken.TryParse(token, out var tokenCollection, out var since) || tokenCollection != collection.Address.Id)
            return DavResponse.Error(403, DavNames.D.ValidSyncToken);
        var changes = await store.GetChangesAsync(collection.Address.Id, since, cancellationToken).ConfigureAwait(false);
        if (changes is null) return DavResponse.Error(403, DavNames.D.ValidSyncToken);

        // The last change per member decides what it is; members are answered in the order of that change, so a
        // truncated answer's token (the counter of its last change) continues exactly after it.
        var last = new Dictionary<string, DavChange>(StringComparer.Ordinal);
        foreach (var change in changes.Changes) last[change.Name] = change;
        var ordered = last.Values.OrderBy(static c => c.Seq).ToList();
        var truncated = limit is { } max && ordered.Count > max;
        if (truncated) ordered = ordered[..limit!.Value];
        var newSeq = truncated ? ordered[^1].Seq : changes.CurrentSeq;

        var present = new Dictionary<string, DavStoredObject>(StringComparer.Ordinal);
        var wanted = ordered.Where(static c => c.Kind != DavChangeKind.Removed).Select(static c => c.Name).ToList();
        if (wanted.Count > 0)
            await foreach (var stored in store.GetObjectsAsync(collection.Address.Id, wanted, needsContent, cancellationToken).ConfigureAwait(false))
                present[stored.Name] = stored;

        return DavResponse.Xml(207, async (output, ct) => {
            DavMultistatus.Start(output);
            foreach (var change in ordered) {
                if (present.TryGetValue(change.Name, out var stored)) await WriteMemberAsync(output, stored, ct).ConfigureAwait(false);
                else DavMultistatus.WriteStatus(output, context.MemberHref(protocol, collection, change.Name), 404);
                await output.FlushIfFullAsync(ct).ConfigureAwait(false);
            }

            // RFC 6578 §3.6: truncation is a 507 on the request-URI; DAVx5 and Thunderbird continue from the token.
            if (truncated) DavMultistatus.WriteStatus(output, resource.Href, 507);
            DavXml.TextElement(output.Writer, DavNames.D.SyncToken, DavSyncToken.Format(collection.Address.Id, newSeq));
            DavMultistatus.End(output);
        });

        ValueTask WriteMemberAsync(DavXmlOutput output, DavMember member, CancellationToken ct) {
            var child = new DavResource(context, DavResourceKind.Member, context.MemberHref(protocol, collection, member.Name)) {
                Protocol = protocol, Collection = collection, Member = member,
            };
            return DavMultistatus.WriteResourceAsync(output, child, selection, minimal, ct);
        }
    }
}
