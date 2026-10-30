using BiStudent.Server.Firebase;
using Xunit;

namespace BiStudent.Tests;

public sealed class PendingCloudSignInTests
{
    [Fact]
    public void EmptyUntilRemembered()
    {
        var pending = new PendingCloudSignIn();
        Assert.False(pending.TryGet(out _, out _));
    }

    [Fact]
    public void RememberReplacesCredentials()
    {
        var pending = new PendingCloudSignIn();
        pending.Remember("anna", "first");
        Assert.True(pending.TryGet(out var login, out var password));
        Assert.Equal("anna", login);
        Assert.Equal("first", password);
        pending.Remember("petrov", "second");
        Assert.True(pending.TryGet(out login, out password));
        Assert.Equal("petrov", login);
        Assert.Equal("second", password);
    }

    [Fact]
    public void ClearRemovesCredentials()
    {
        var pending = new PendingCloudSignIn();
        pending.Remember("anna", "password");
        pending.Clear();
        Assert.False(pending.TryGet(out _, out _));
    }
}
