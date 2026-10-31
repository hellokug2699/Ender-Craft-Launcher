// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using Ecl.Operations;
using Xunit;

namespace Ecl.Tests.Operations;

public sealed class OperationErrorTests
{
    [Theory]
    [InlineData(typeof(OperationCanceledException), OperationErrorKind.Cancelled)]
    [InlineData(typeof(InvalidDataException), OperationErrorKind.FileIntegrity)]
    [InlineData(typeof(IOException), OperationErrorKind.LocalIO)]
    [InlineData(typeof(UnauthorizedAccessException), OperationErrorKind.LocalIO)]
    [InlineData(typeof(SocketException), OperationErrorKind.Network)]
    [InlineData(typeof(TimeoutException), OperationErrorKind.Network)]
    public void ClassifiesExceptionTypes(Type exceptionType, OperationErrorKind expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.Equal(expected, OperationErrors.Classify(exception));
    }

    [Fact]
    public void ClassifiesHttpStatusCodes()
    {
        Assert.Equal(
            OperationErrorKind.Network,
            OperationErrors.Classify(new HttpRequestException("dns")));
        Assert.Equal(
            OperationErrorKind.Upstream,
            OperationErrors.Classify(new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable)));
        Assert.Equal(
            OperationErrorKind.Authentication,
            OperationErrors.Classify(new HttpRequestException("denied", null, HttpStatusCode.Unauthorized)));
        Assert.Equal(
            OperationErrorKind.Authentication,
            OperationErrors.Classify(new HttpRequestException("denied", null, HttpStatusCode.Forbidden)));
    }

    [Fact]
    public void ClassifiesSocketFailureWrappedInAnotherException()
    {
        var exception = new IOException("connection reset", new SocketException((int)SocketError.ConnectionReset));
        Assert.Equal(OperationErrorKind.LocalIO, OperationErrors.Classify(exception));

        var unknown = new Exception("wrapper", new SocketException((int)SocketError.HostNotFound));
        Assert.Equal(OperationErrorKind.Network, OperationErrors.Classify(unknown));
    }

    [Fact]
    public void AggregateFailureUsesTheMostActionableReason()
    {
        var aggregate = new AggregateException(
            new IOException("disk"),
            new InvalidDataException("hash"),
            new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable));

        Assert.Equal(OperationErrorKind.FileIntegrity, OperationErrors.Classify(aggregate));
        Assert.Equal(
            OperationErrorKind.Authentication,
            OperationErrors.Worst([
                OperationErrorKind.Network,
                OperationErrorKind.Authentication,
                OperationErrorKind.Upstream,
            ]));
    }

    [Fact]
    public void FromExceptionKeepsCodeMessageAndInnerDetail()
    {
        var error = OperationErrors.FromException(
            "download.failed",
            new IOException("outer", new Exception("inner")));

        Assert.Equal(OperationErrorKind.LocalIO, error.Kind);
        Assert.Equal("download.failed", error.Code);
        Assert.Equal("outer", error.Message);
        Assert.Equal("inner", error.Detail);
    }
}
