namespace Elarion.Abstractions.Paging;

/// <summary>
/// A keyset (seek) pagination request. Implemented by handler request DTOs so the pagination
/// fields stay flat — this lets HTTP GET endpoints bind <c>after</c>/<c>before</c>/<c>size</c>
/// from the query string while JSON-RPC and MCP bind them from the params object.
/// </summary>
/// <remarks>
/// Pass <see cref="After"/> to page forward from a cursor or <see cref="Before"/> to page
/// backward; supplying neither returns the first page. The cursors are the opaque
/// <see cref="Page{T}.StartCursor"/>/<see cref="Page{T}.EndCursor"/> values from a prior page.
/// </remarks>
public interface IKeysetPageRequest {
    /// <summary>Opaque cursor to page forward from. Mutually exclusive with <see cref="Before"/>.</summary>
    string? After { get; }

    /// <summary>Opaque cursor to page backward from. Mutually exclusive with <see cref="After"/>.</summary>
    string? Before { get; }

    /// <summary>Requested page size. The execution helper clamps this to a configured maximum.</summary>
    int Size { get; }
}
