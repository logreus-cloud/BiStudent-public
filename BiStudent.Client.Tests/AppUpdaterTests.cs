using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace BiStudent.Client.Tests;

public sealed class AppUpdaterTests
{
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void DescribeExplainsGitHubLimitStatus(HttpStatusCode status)
    {
        var error = new HttpRequestException("Ошибка GitHub", null, status);

        Assert.Equal("GitHub временно ограничил запросы с этой сети (лимит 60 в час). Повторите позже.",
            AppUpdater.Describe(error));
    }

    [Fact]
    public void DescribeExplainsRateLimitMessage()
    {
        var error = new HttpRequestException("API rate limit exceeded");

        Assert.Equal("GitHub временно ограничил запросы с этой сети (лимит 60 в час). Повторите позже.",
            AppUpdater.Describe(error));
    }

    [Fact]
    public void DescribeExplainsHttpRequestWithoutStatus()
    {
        var error = new HttpRequestException("Запрос не выполнен");

        Assert.Equal("Нет связи с GitHub — проверьте интернет или ограничения сети колледжа.",
            AppUpdater.Describe(error));
    }

    [Fact]
    public void DescribeKeepsServerErrorStatusOutOfNetworkMessage()
    {
        var error = new HttpRequestException("Not Found", null, HttpStatusCode.NotFound);

        Assert.Equal("Не удалось проверить обновления: Not Found", AppUpdater.Describe(error));
    }

    [Fact]
    public void DescribeFindsSocketExceptionInside()
    {
        var error = new InvalidOperationException("Проверка не выполнена",
            new SocketException((int)SocketError.NetworkUnreachable));

        Assert.Equal("Нет связи с GitHub — проверьте интернет или ограничения сети колледжа.",
            AppUpdater.Describe(error));
    }

    [Fact]
    public void DescribeExplainsDownloadWriteError()
    {
        var error = new IOException("Диск заполнен");

        Assert.Equal("Не удалось сохранить обновление: Диск заполнен", AppUpdater.Describe(error));
    }

    [Fact]
    public void DescribeExplainsOtherError()
    {
        var error = new InvalidOperationException("Неизвестная ошибка");

        Assert.Equal("Не удалось проверить обновления: Неизвестная ошибка", AppUpdater.Describe(error));
    }
}
