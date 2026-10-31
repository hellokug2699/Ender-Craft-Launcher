// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Xunit;

namespace Ecl.Tests.Core;

public sealed class SecretRedactionTests
{
    [Fact]
    public void MaskKeepsBothEndsAndHidesTheMiddle()
    {
        var masked = SecretRedaction.Mask("eyJraWQiOiIwNDkxODEiLCJhbGciOiJSUzI1NiJ9.payload.signature");

        Assert.StartsWith("eyJraWQi", masked, StringComparison.Ordinal);
        Assert.EndsWith("ture", masked, StringComparison.Ordinal);
        Assert.Contains("********", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("signature", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskHandlesEmptyAndShortValues()
    {
        Assert.Equal(string.Empty, SecretRedaction.Mask(null));
        Assert.Equal(string.Empty, SecretRedaction.Mask(string.Empty));
        Assert.Equal("***", SecretRedaction.Mask("abc"));
    }

    [Fact]
    public void CommandLineRedactsOnlyTheAccessTokenArgument()
    {
        var commandLine =
            "java -cp libs net.minecraft.client.main.Main --username hellokug --uuid fab9598d --accessToken eyJhbGciOi.eyJ4dWlkIjoiMjUzNTQyODY5In0.sig --userType msa";

        var redacted = SecretRedaction.CommandLine(commandLine);

        Assert.DoesNotContain("eyJhbGciOi.eyJ4dWlkIjoiMjUzNTQyODY5In0.sig", redacted, StringComparison.Ordinal);
        Assert.Contains("--accessToken eyJhbGci********.sig", redacted, StringComparison.Ordinal);
        Assert.Contains("--username hellokug", redacted, StringComparison.Ordinal);
        Assert.Contains("--uuid fab9598d", redacted, StringComparison.Ordinal);
        Assert.Contains("--userType msa", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLineWithoutTokenIsUnchanged()
    {
        const string commandLine = "java -cp libs net.minecraft.client.main.Main --username hellokug";
        Assert.Equal(commandLine, SecretRedaction.CommandLine(commandLine));
    }
}
