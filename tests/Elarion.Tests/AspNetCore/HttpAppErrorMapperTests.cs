using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.AspNetCore;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>Tests the <see cref="ErrorKind"/> → HTTP status code mapping used for ProblemDetails responses.</summary>
public sealed class HttpAppErrorMapperTests {
    [Theory]
    [InlineData(ErrorKind.Validation, 400)]
    [InlineData(ErrorKind.NotFound, 404)]
    [InlineData(ErrorKind.Conflict, 409)]
    [InlineData(ErrorKind.Forbidden, 403)]
    [InlineData(ErrorKind.Unauthorized, 401)]
    [InlineData(ErrorKind.BusinessRule, 422)]
    [InlineData(ErrorKind.Internal, 500)]
    public void MapToStatusCode_MapsEachKind(ErrorKind kind, int expected) =>
        HttpAppErrorMapper.MapToStatusCode(kind).Should().Be(expected);
}
