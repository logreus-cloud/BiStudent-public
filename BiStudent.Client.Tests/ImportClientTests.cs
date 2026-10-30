using System.Runtime.ExceptionServices;
using BiStudent.Contracts;

namespace BiStudent.Client.Tests;

[Collection("settings")]
public sealed class ImportClientTests
{
    private static readonly ScheduleImportPreviewDto Preview = new(new DateOnly(2026, 8, 31),
        [new("4 курс", "23-ПО-103-4р-1", 144, true), new("4 курс", "23-ПО-103-4р-2", 153, false)],
        ["Ниязова М.Е.", "Вакансия"], ["4 курс, 23-ПО-103-4р-2, пн 08:00: не удалось разобрать «…»."]);

    [Fact]
    public async Task ImportMethodsSendFileAndSelectedGroups()
    {
        var server = new FakeServer();
        server.On(HttpMethod.Post, "api/students/import", _ => FakeServer.Json(new ImportResultDto(86, 0, [])));
        server.On(HttpMethod.Post, "api/schedule/import-xlsx/preview", _ => FakeServer.Json(Preview));
        server.On(HttpMethod.Post, "api/schedule/import-xlsx", _ => FakeServer.Json(new ScheduleImportResultDto(297, 0, 0,
            [new("Ниязова М.Е.", "niyazova.me", "Abc23456xy", true, true)], [])));
        using var api = new ApiClient(new ClientSettings { ServerUrl = "http://test/" }, server);
        var file = new byte[] { (byte)'P', (byte)'K', 3, 4 };

        Assert.Equal(86, (await api.ImportStudentsAsync(file)).Added);
        Assert.Equal(2, (await api.PreviewScheduleXlsxAsync(file)).Groups.Count);
        var result = await api.ImportScheduleXlsxAsync(file, ["23-ПО-103-4р-1", "23-ПО-103-4р-2"]);
        Assert.Equal(297, result.Added);

        var import = Assert.Single(server.Requests, r => r.Path.StartsWith("api/schedule/import-xlsx?", StringComparison.Ordinal));
        Assert.Equal("api/schedule/import-xlsx?groups=" + Uri.EscapeDataString("23-ПО-103-4р-1") + "&groups=" + Uri.EscapeDataString("23-ПО-103-4р-2"), import.Path);
        Assert.All(server.Requests, r => Assert.Equal(HttpMethod.Post, r.Method));
    }

    [Fact]
    public async Task ImportScheduleXlsxSendsSelectedTeacher()
    {
        var server = new FakeServer();
        server.On(HttpMethod.Post, "api/schedule/import-xlsx", _ => FakeServer.Json(new ScheduleImportResultDto(1, 0, 0, [], [])));
        using var api = new ApiClient(new ClientSettings { ServerUrl = "http://test/" }, server);

        await api.ImportScheduleXlsxAsync([1, 2, 3], ["23-ПО-103-4р-1"], 42);

        Assert.Equal("api/schedule/import-xlsx?groups=" + Uri.EscapeDataString("23-ПО-103-4р-1") + "&teacherId=42",
            Assert.Single(server.Requests).Path);
    }

    [Fact]
    public async Task PreviewScheduleXlsxSendsSelectedTeacher()
    {
        var server = new FakeServer();
        server.On(HttpMethod.Post, "api/schedule/import-xlsx/preview", _ => FakeServer.Json(Preview));
        using var api = new ApiClient(new ClientSettings { ServerUrl = "http://test/" }, server);

        await api.PreviewScheduleXlsxAsync([1, 2, 3], 42);

        Assert.Equal("api/schedule/import-xlsx/preview?teacherId=42", Assert.Single(server.Requests).Path);
    }

    [Fact]
    public void ScheduleImportDialogShowsPreview()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var api = new ApiClient(new ClientSettings { ServerUrl = "http://test/" }, new FakeServer());
                using var dialog = new ScheduleImportDialog(api, [1, 2, 3], Preview, []);
                dialog.CreateControl();
                Assert.Null(dialog.Result);
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
