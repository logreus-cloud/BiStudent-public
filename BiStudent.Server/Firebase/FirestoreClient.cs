using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BiStudent.Server.Firebase;

public sealed record FirestoreDocument(
    string Collection, string Id, IReadOnlyDictionary<string, JsonElement> Fields,
    DateTimeOffset? UpdatedAt, JsonElement? RawUpdatedAt = null,
    string Name = "");

public sealed record FirestoreWrite(
    string Collection, string Id, Dictionary<string, object?>? Fields,
    string[]? UpdateMask = null, bool? MustExist = null,
    bool ServerTimestamp = true, bool IsDelete = false)
{
    public static FirestoreWrite Delete(string collection, string id) =>
        new(collection, id, null, ServerTimestamp: false, IsDelete: true);
}

public sealed record FirestoreWriteResult(DateTimeOffset? UpdateTime);

public sealed class FirestoreClient(HttpClient http, IOptions<FirebaseOptions> options)
{
    private readonly FirebaseOptions settings = options.Value;

    private string Root
    {
        get
        {
            var host = string.IsNullOrWhiteSpace(settings.FirestoreEmulatorHost)
                ? "https://firestore.googleapis.com"
                : $"http://{settings.FirestoreEmulatorHost}";
            return $"{host}/v1/projects/{Uri.EscapeDataString(settings.ProjectId)}/databases/(default)/documents";
        }
    }

    public async Task<FirestoreDocument?> GetAsync(
        string idToken, string collection, string id, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get,
            Root + "/" + Uri.EscapeDataString(collection) + "/" + Uri.EscapeDataString(id), idToken);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var document = await ReadAsync(response, cancellationToken);
        return ParseDocument(document.RootElement, collection);
    }

    public async Task<IReadOnlyList<FirestoreDocument>> QueryChangedAsync(
        string idToken, string collection, DateTimeOffset? after, int limit,
        CancellationToken cancellationToken = default, FirestoreDocument? afterDocument = null)
    {
        var query = new Dictionary<string, object?>
        {
            ["from"] = new[] { new { collectionId = collection } },
            ["orderBy"] = new[]
            {
                new { field = new { fieldPath = "_updatedAt" }, direction = "ASCENDING" },
                new { field = new { fieldPath = "__name__" }, direction = "ASCENDING" }
            },
            ["limit"] = limit
        };
        if (after is not null)
            query["where"] = new
            {
                fieldFilter = new
                {
                    field = new { fieldPath = "_updatedAt" },
                    op = "GREATER_THAN",
                    value = new { timestampValue = after.Value.ToString("O") }
                }
            };
        if (afterDocument is not null)
        {
            if (afterDocument.RawUpdatedAt is null ||
                afterDocument.Collection != collection ||
                string.IsNullOrEmpty(afterDocument.Name))
                throw new ArgumentException("Неверная позиция страницы.", nameof(afterDocument));
            query["startAt"] = new
            {
                values = new object[]
                {
                    afterDocument.RawUpdatedAt.Value,
                    new { referenceValue = afterDocument.Name }
                },
                before = false
            };
        }
        using var request = Request(HttpMethod.Post, Root + ":runQuery", idToken);
        request.Content = JsonContent.Create(new { structuredQuery = query });
        using var response = await SendAsync(request, cancellationToken);
        using var document = await ReadAsync(response, cancellationToken);
        var result = new List<FirestoreDocument>();
        foreach (var item in document.RootElement.EnumerateArray())
            if (item.TryGetProperty("document", out var entry))
                result.Add(ParseDocument(entry, collection));
        return result;
    }

    public async Task<IReadOnlyList<FirestoreWriteResult>> CommitAsync(
        string idToken, IReadOnlyList<FirestoreWrite> writes,
        CancellationToken cancellationToken = default)
    {
        var payload = new List<Dictionary<string, object?>>(writes.Count);
        foreach (var write in writes)
        {
            var name = $"projects/{settings.ProjectId}/databases/(default)/documents/" +
                write.Collection + "/" + write.Id;
            var item = new Dictionary<string, object?>();
            if (write.IsDelete)
            {
                item["delete"] = name;
            }
            else
            {
                item["update"] = new
                {
                    name,
                    fields = write.Fields!.ToDictionary(
                        field => field.Key, field => FirestoreValues.ToValue(field.Value))
                };
                if (write.UpdateMask is not null)
                    item["updateMask"] = new { fieldPaths = write.UpdateMask };
                if (write.ServerTimestamp)
                    item["updateTransforms"] = new[]
                    {
                        new { fieldPath = "_updatedAt", setToServerValue = "REQUEST_TIME" }
                    };
            }
            if (write.MustExist is not null)
                item["currentDocument"] = new { exists = write.MustExist.Value };
            payload.Add(item);
        }

        using var request = Request(HttpMethod.Post, Root + ":commit", idToken);
        request.Content = JsonContent.Create(new { writes = payload });
        using var response = await SendAsync(request, cancellationToken);
        using var document = await ReadAsync(response, cancellationToken);
        var results = new List<FirestoreWriteResult>();
        if (document.RootElement.TryGetProperty("writeResults", out var entries))
            foreach (var entry in entries.EnumerateArray())
                results.Add(new FirestoreWriteResult(
                    entry.TryGetProperty("updateTime", out var value)
                        ? DateTimeOffset.Parse(value.GetString()!,
                            System.Globalization.CultureInfo.InvariantCulture)
                        : null));
        return results;
    }

    private static FirestoreDocument ParseDocument(JsonElement element, string collection)
    {
        var name = element.GetProperty("name").GetString()!;
        var fields = new Dictionary<string, JsonElement>();
        if (element.TryGetProperty("fields", out var values))
            foreach (var field in values.EnumerateObject())
                fields[field.Name] = field.Value.Clone();
        JsonElement? rawUpdatedAt = fields.TryGetValue("_updatedAt", out var timestamp)
            ? timestamp : null;
        DateTimeOffset? updated = null;
        if (timestamp.ValueKind == JsonValueKind.Object &&
            timestamp.TryGetProperty("timestampValue", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            updated = parsed;
        return new FirestoreDocument(collection, name[(name.LastIndexOf('/') + 1)..],
            fields, updated, rawUpdatedAt, name);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FirebaseUnavailableException("Firestore временно недоступен.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new FirebaseUnavailableException("Firestore временно недоступен.", exception);
        }

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound &&
            request.Method == HttpMethod.Get) return response;

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
                throw new FirebaseUnavailableException("Firestore временно недоступен.");

            var status = response.StatusCode.ToString();
            var message = "Ошибка запроса к Firestore.";
            try
            {
                using var error = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(cancellationToken));
                var details = error.RootElement.GetProperty("error");
                if (details.TryGetProperty("status", out var code))
                    status = code.GetString() ?? status;
                if (details.TryGetProperty("message", out var text))
                    message = text.GetString() ?? message;
            }
            catch (JsonException) { }
            catch (KeyNotFoundException) { }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ||
                status is "PERMISSION_DENIED" or "UNAUTHENTICATED")
                throw new FirestorePermissionException(message);
            throw new FirestoreRequestException(status, message);
        }
    }

    private static async Task<JsonDocument> ReadAsync(
        HttpResponseMessage response, CancellationToken cancellationToken) =>
        await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
}
