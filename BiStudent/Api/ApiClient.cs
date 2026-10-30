using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;

namespace BiStudent;

internal sealed class ApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public ScheduleConflictDto? Conflict { get; }

    public ApiException(HttpStatusCode? statusCode, string message, Exception? inner = null, ScheduleConflictDto? conflict = null) : base(message, inner)
    {
        StatusCode = statusCode;
        Conflict = conflict;
    }
}

internal sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(60);
    private readonly HttpClient _http;
    private readonly SynchronizationContext? _uiContext;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly object _sessionGate = new();
    private string? _accessToken;
    private string? _refreshToken;
    private int _generation;
    private bool _ended;
    private int _unauthorizedRaised;

    public TeacherDto? Teacher { get; private set; }
    public string? RefreshToken => _refreshToken;
    public event Action? Unauthorized;
    public event Action<string?>? RefreshTokenChanged;

    public ApiClient() : this(ClientSettings.Load()) { }

    public ApiClient(ClientSettings settings) : this(settings, null) { }

    // handler подменяется в тестах, в приложении используется обычный HttpClientHandler.
    internal ApiClient(ClientSettings settings, HttpMessageHandler? handler)
    {
        var address = settings.ServerUrl?.Trim();
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ApiException(null, "Неверный адрес сервера.");
        _http = new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
        _uiContext = SynchronizationContext.Current;
    }

    public async Task LoginAsync(string login, string password, bool remember = false)
    {
        await _tokenLock.WaitAsync();
        try
        {
            var generation = Volatile.Read(ref _generation);
            var token = await RequestAsync<TokenResponse>(HttpMethod.Post, "api/auth/login", Json(new LoginRequest(login, password, remember)));
            if (SetTokens(token, generation)) Teacher = null;
        }
        finally { _tokenLock.Release(); }
    }

    public async Task<bool> TryResumeAsync(string refreshToken)
    {
        if (!await RefreshAsync(refreshToken, CancellationToken.None)) return false;
        await LoadMeAsync();
        return true;
    }

    public async Task ChangePasswordAsync(string current, string next)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string? failedToken = null;
            await _tokenLock.WaitAsync();
            try
            {
                var generation = Volatile.Read(ref _generation);
                failedToken = _accessToken;
                var token = await RequestAsync<TokenResponse>(HttpMethod.Post, "api/auth/change-password",
                    Json(new ChangePasswordRequest(current, next)));
                SetTokens(token, generation);
                break;
            }
            catch (ApiException ex) when (attempt == 0 && ex.StatusCode == HttpStatusCode.Unauthorized && failedToken is not null) { }
            finally { _tokenLock.Release(); }
            if (!await RefreshAfterUnauthorizedAsync(failedToken!, CancellationToken.None))
            {
                RaiseUnauthorized();
                throw new ApiException(HttpStatusCode.Unauthorized, "Сессия истекла — войдите снова.");
            }
        }
        try { await LoadMeAsync(); }
        catch (Exception)
        {
            try { await LoadMeAsync(); }
            catch (Exception) { }
        }
    }

    public async Task<TeacherDto> LoadMeAsync()
    {
        Teacher = await GetAsync<TeacherDto>("api/me");
        return Teacher;
    }

    public Task<AutomationSettingsDto> AutomationAsync() => GetAsync<AutomationSettingsDto>("api/settings/automation");
    public Task<EnrollResultDto> RecognitionStatusAsync() => GetAsync<EnrollResultDto>("api/recognition/status");
    public Task<SyncStatusDto> SyncStatusAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<SyncStatusDto>(HttpMethod.Get, "api/sync/status", cancellationToken: cancellationToken);
    public Task<SyncStatusDto> SyncNowAsync() => RequestAsync<SyncStatusDto>(HttpMethod.Post, "api/sync/run");
    public Task<List<BackupFileDto>> BackupsAsync() => GetAsync<List<BackupFileDto>>("api/admin/backups");
    public Task<BackupFileDto> CreateBackupAsync() =>
        RequestAsync<BackupFileDto>(HttpMethod.Post, "api/admin/backups", timeout: LongTimeout);
    public Task<RestoreResultDto> RestoreAsync(byte[] zip) =>
        RequestAsync<RestoreResultDto>(HttpMethod.Post, "api/admin/restore", new ByteArrayContent(zip) { Headers = { ContentType = new MediaTypeHeaderValue("application/zip") } }, timeout: TimeSpan.FromMinutes(5));

    public Task<List<TeacherAccountDto>> TeachersAsync() => GetAsync<List<TeacherAccountDto>>("api/admin/teachers");
    public Task<TemporaryPasswordDto> CreateTeacherAsync(SaveTeacherRequest request) =>
        RequestAsync<TemporaryPasswordDto>(HttpMethod.Post, "api/admin/teachers", Json(request));
    public Task UpdateTeacherAsync(int id, SaveTeacherRequest request) =>
        RequestAsync(HttpMethod.Put, $"api/admin/teachers/{id}", Json(request));
    public Task<TemporaryPasswordDto> ResetTeacherPasswordAsync(int id) =>
        RequestAsync<TemporaryPasswordDto>(HttpMethod.Post, $"api/admin/teachers/{id}/reset-password");

    public async Task<(byte[] Content, string FileName)> DownloadBackupAsync(string name)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var response = await SendWithDeadlineAsync(HttpMethod.Get, $"api/admin/backups/{Uri.EscapeDataString(name)}", null, deadline.Token, CancellationToken.None);
        try { return (await response.Content.ReadAsByteArrayAsync(deadline.Token), name); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw NetworkError(ex); }
        catch (OperationCanceledException ex) { throw TimeoutError(ex); }
    }
    public Task<DashboardDto> DashboardAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<DashboardDto>(HttpMethod.Get, "api/dashboard", cancellationToken: cancellationToken);
    public Task<ReportDto> ReportAsync() => GetAsync<ReportDto>("api/report");
    public Task<List<LessonDto>> DayAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        RequestAsync<List<LessonDto>>(HttpMethod.Get, $"api/schedule/day?date={QueryDate(date)}", cancellationToken: cancellationToken);
    public Task<List<LessonDto>> WeekAsync(DateOnly monday) => GetAsync<List<LessonDto>>($"api/schedule/week?monday={QueryDate(monday)}");
    public Task<WeekParity> ParityAsync(DateOnly date) => GetAsync<WeekParity>($"api/schedule/parity?date={QueryDate(date)}");

    public Task<SlotDto> SaveSlotAsync(int? id, SaveSlotRequest request) =>
        RequestAsync<SlotDto>(id.HasValue ? HttpMethod.Put : HttpMethod.Post, id.HasValue ? $"api/schedule/slots/{id.Value}" : "api/schedule/slots", Json(request));
    public Task DeleteSlotAsync(int id) => RequestAsync(HttpMethod.Delete, $"api/schedule/slots/{id}");
    public Task CancelLessonAsync(int slotId, DateOnly date, string? reason) =>
        RequestAsync(HttpMethod.Post, $"api/schedule/slots/{slotId}/cancel", Json(new CancelLessonRequest(date, reason)));
    public Task RestoreLessonAsync(int slotId, DateOnly date) =>
        RequestAsync(HttpMethod.Post, $"api/schedule/slots/{slotId}/restore", Json(new RestoreLessonRequest(date)));
    public Task<ImportResultDto> ImportScheduleAsync(string csv) =>
        RequestAsync<ImportResultDto>(HttpMethod.Post, "api/schedule/import", new StringContent(csv, Encoding.UTF8, "text/csv"));

    public Task<List<GroupDto>> GroupsAsync() => GetAsync<List<GroupDto>>("api/groups");
    public Task<GroupDto> CreateGroupAsync(CreateGroupRequest request) => RequestAsync<GroupDto>(HttpMethod.Post, "api/groups", Json(request));
    public Task DeleteGroupAsync(int id) => RequestAsync(HttpMethod.Delete, $"api/groups/{id}");
    public Task<List<StudentDto>> StudentsAsync(int groupId) => GetAsync<List<StudentDto>>($"api/groups/{groupId}/students");
    public Task<StudentDto> SaveStudentAsync(int? id, SaveStudentRequest request) =>
        RequestAsync<StudentDto>(id.HasValue ? HttpMethod.Put : HttpMethod.Post, id.HasValue ? $"api/students/{id.Value}" : "api/students", Json(request));
    public Task DeleteStudentAsync(int id) => RequestAsync(HttpMethod.Delete, $"api/students/{id}");
    public Task<ImportResultDto> ImportStudentsAsync(string csv) =>
        RequestAsync<ImportResultDto>(HttpMethod.Post, "api/students/import", new StringContent(csv, Encoding.UTF8, "text/csv"));
    // Файл списка как есть: CSV или журнал Excel (.xlsx), формат сервер определяет сам.
    public Task<ImportResultDto> ImportStudentsAsync(byte[] file) =>
        RequestAsync<ImportResultDto>(HttpMethod.Post, "api/students/import", Binary(file), timeout: LongTimeout);
    public Task<ScheduleImportPreviewDto> PreviewScheduleXlsxAsync(byte[] xlsx, int? teacherId = null) =>
        RequestAsync<ScheduleImportPreviewDto>(HttpMethod.Post, "api/schedule/import-xlsx/preview" +
            (teacherId is int id ? "?teacherId=" + id.ToString(CultureInfo.InvariantCulture) : ""), Binary(xlsx), timeout: LongTimeout);
    public Task<ScheduleImportResultDto> ImportScheduleXlsxAsync(byte[] xlsx, IEnumerable<string> groups, int? teacherId = null) =>
        RequestAsync<ScheduleImportResultDto>(HttpMethod.Post,
            "api/schedule/import-xlsx?" + string.Join("&", groups.Select(g => "groups=" + Uri.EscapeDataString(g))) +
            (teacherId is int id ? "&teacherId=" + id.ToString(CultureInfo.InvariantCulture) : ""), Binary(xlsx), timeout: LongTimeout);

    private static ByteArrayContent Binary(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }
    public Task<EnrollResultDto> EnrollAsync(int studentId, List<byte[]> images, bool allowSimilar = false) =>
        RequestAsync<EnrollResultDto>(HttpMethod.Post, $"api/students/{studentId}/enroll", Json(new EnrollRequest(images, allowSimilar)), timeout: LongTimeout);

    public async Task<EnrollmentCodeDto?> EnrollmentCodeAsync(int groupId)
    {
        using var deadline = new CancellationTokenSource(DefaultTimeout);
        using var response = await SendWithDeadlineAsync(HttpMethod.Get, $"api/groups/{groupId}/enrollment-code", null,
            deadline.Token, CancellationToken.None);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return await JsonSerializer.DeserializeAsync<EnrollmentCodeDto>(stream, JsonOptions, deadline.Token)
                ?? throw new ApiException(response.StatusCode, "Пустой ответ сервера.");
        }
        catch (JsonException ex) { throw new ApiException(response.StatusCode, "Некорректный ответ сервера.", ex); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw NetworkError(ex); }
        catch (OperationCanceledException ex) { throw TimeoutError(ex); }
    }

    public Task<EnrollmentCodeDto> CreateEnrollmentCodeAsync(int groupId, int hours) =>
        RequestAsync<EnrollmentCodeDto>(HttpMethod.Post, $"api/groups/{groupId}/enrollment-code", Json(new CreateEnrollmentCodeRequest(hours)));
    public Task RevokeEnrollmentCodeAsync(int groupId) =>
        RequestAsync(HttpMethod.Delete, $"api/groups/{groupId}/enrollment-code");
    public Task<List<FaceSubmissionDto>> FaceSubmissionsAsync() => GetAsync<List<FaceSubmissionDto>>("api/face-submissions");
    public async Task<byte[]> FaceSubmissionImageAsync(int id, int index)
    {
        using var deadline = new CancellationTokenSource(LongTimeout);
        using var response = await SendWithDeadlineAsync(HttpMethod.Get, $"api/face-submissions/{id}/images/{index}", null,
            deadline.Token, CancellationToken.None);
        try { return await response.Content.ReadAsByteArrayAsync(deadline.Token); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw NetworkError(ex); }
        catch (OperationCanceledException ex) { throw TimeoutError(ex); }
    }
    public Task ApproveSubmissionAsync(int id) => RequestAsync(HttpMethod.Post, $"api/face-submissions/{id}/approve");
    public Task RejectSubmissionAsync(int id, string? reason) =>
        RequestAsync(HttpMethod.Post, $"api/face-submissions/{id}/reject", Json(new RejectSubmissionRequest(reason)));

    public Task<List<AttendanceRowDto>> AttendanceAsync(int slotId, DateOnly date, CancellationToken cancellationToken = default) =>
        RequestAsync<List<AttendanceRowDto>>(HttpMethod.Get, $"api/lessons/{slotId}/{QueryDate(date)}/attendance", cancellationToken: cancellationToken);
    public Task MarkAsync(int slotId, DateOnly date, int studentId, AttendanceStatus? status,
        ExcuseReason? reason = null, string? comment = null, bool hasDocument = false) =>
        RequestAsync(HttpMethod.Put, $"api/lessons/{slotId}/{QueryDate(date)}/attendance",
            Json(new MarkRequest(studentId, status, reason, comment, hasDocument)));
    public Task LessonGradeAsync(int slotId, DateOnly date, int studentId, int? score) =>
        RequestAsync(HttpMethod.Put, $"api/lessons/{slotId}/{QueryDate(date)}/grade", Json(new GradeRequest(studentId, score)));
    public Task<CountResult> FinishAsync(int slotId, DateOnly date) =>
        RequestAsync<CountResult>(HttpMethod.Post, $"api/lessons/{slotId}/{QueryDate(date)}/finish");
    public Task<CountResult> AllPresentAsync(int slotId, DateOnly date) =>
        RequestAsync<CountResult>(HttpMethod.Post, $"api/lessons/{slotId}/{QueryDate(date)}/all-present");
    public Task<RecognitionResultDto> RecognizeAsync(int slotId, DateOnly date, byte[] jpeg, CancellationToken cancellationToken) =>
        RequestAsync<RecognitionResultDto>(HttpMethod.Post, "api/recognize", Json(new RecognizeRequest(slotId, date, jpeg)), cancellationToken);

    public Task<List<string>> SubjectsAsync(int groupId) => GetAsync<List<string>>($"api/journal/{groupId}/subjects");
    public Task<List<AssessmentDto>> AssessmentsAsync(int? groupId, string? subject)
    {
        var query = new List<string>();
        if (groupId.HasValue) query.Add($"groupId={groupId.Value}");
        if (!string.IsNullOrWhiteSpace(subject)) query.Add($"subject={Uri.EscapeDataString(subject)}");
        return GetAsync<List<AssessmentDto>>("api/assessments" + (query.Count == 0 ? "" : "?" + string.Join("&", query)));
    }
    public Task<AssessmentDto> SaveAssessmentAsync(int? id, SaveAssessmentRequest request) =>
        RequestAsync<AssessmentDto>(id.HasValue ? HttpMethod.Put : HttpMethod.Post,
            id.HasValue ? $"api/assessments/{id.Value}" : "api/assessments", Json(request));
    public Task DeleteAssessmentAsync(int id) => RequestAsync(HttpMethod.Delete, $"api/assessments/{id}");
    public Task AssessmentGradeAsync(int assessmentId, int studentId, int? score) =>
        RequestAsync(HttpMethod.Put, $"api/assessments/{assessmentId}/grades", Json(new GradeRequest(studentId, score)));
    public Task FinalGradeAsync(int groupId, string subject, int studentId, int? score) =>
        RequestAsync(HttpMethod.Put, $"api/journal/{groupId}/final", Json(new FinalGradeRequest(subject, studentId, score)));
    public Task<CountResult> FillFinalAsync(int groupId, string subject) =>
        RequestAsync<CountResult>(HttpMethod.Post, $"api/journal/{groupId}/final/fill", Json(new FillFinalRequest(subject)));

    public Task<JournalDto> JournalAsync(int groupId, DateOnly from, DateOnly to, string? subject, CancellationToken cancellationToken = default) =>
        RequestAsync<JournalDto>(HttpMethod.Get, JournalPath(groupId, from, to, subject, ""), cancellationToken: cancellationToken, timeout: LongTimeout);

    public async Task<(byte[] Content, string FileName)> JournalFileAsync(int groupId, DateOnly from, DateOnly to, string? subject, string format)
    {
        if (format is not ("xlsx" or "csv")) throw new ArgumentException("Формат должен быть xlsx или csv.", nameof(format));
        using var deadline = new CancellationTokenSource(LongTimeout);
        using var response = await SendWithDeadlineAsync(HttpMethod.Get, JournalPath(groupId, from, to, subject, $"/{format}"), null, deadline.Token, CancellationToken.None);
        try
        {
            var content = await response.Content.ReadAsByteArrayAsync(deadline.Token);
            var name = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName;
            name = string.IsNullOrWhiteSpace(name) ? "" : Path.GetFileName(name.Trim('"'));
            if (string.IsNullOrWhiteSpace(name))
                name = $"journal-{groupId}-{from.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{to.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.{format}";
            return (content, name);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw NetworkError(ex); }
        catch (OperationCanceledException ex) { throw TimeoutError(ex); }
    }

    private static string QueryDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string JournalPath(int groupId, DateOnly from, DateOnly to, string? subject, string suffix)
    {
        var path = $"api/journal/{groupId}{suffix}?from={QueryDate(from)}&to={QueryDate(to)}";
        return string.IsNullOrWhiteSpace(subject) ? path : $"{path}&subject={Uri.EscapeDataString(subject)}";
    }

    private static HttpContent Json<T>(T value) =>
        new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

    private Task<T> GetAsync<T>(string path) => RequestAsync<T>(HttpMethod.Get, path);

    private async Task<T> RequestAsync<T>(HttpMethod method, string path, HttpContent? content = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        using var response = await SendWithDeadlineAsync(method, path, content, deadline.Token, cancellationToken);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, deadline.Token)
                ?? throw new ApiException(response.StatusCode, "Пустой ответ сервера.");
        }
        catch (JsonException ex) { throw new ApiException(response.StatusCode, "Некорректный ответ сервера.", ex); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw NetworkError(ex); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { throw TimeoutError(ex); }
    }

    private async Task RequestAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        using var deadline = new CancellationTokenSource(DefaultTimeout);
        using var response = await SendWithDeadlineAsync(method, path, content, deadline.Token, CancellationToken.None);
    }

    private async Task<HttpResponseMessage> SendWithDeadlineAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken deadline, CancellationToken external)
    {
        try { return await SendAsync(method, path, content, deadline); }
        catch (OperationCanceledException ex) when (!external.IsCancellationRequested) { throw TimeoutError(ex); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, CancellationToken cancellationToken = default)
    {
        using var source = content;
        try
        {
            var body = source is null ? null : await source.ReadAsByteArrayAsync(cancellationToken);
            var headers = source?.Headers.ToArray();
            var token = _accessToken;
            var response = await SendOnceAsync(method, path, body, headers, token, cancellationToken);
            if (response.IsSuccessStatusCode) return response;
            var authRequest = path.StartsWith("api/auth/", StringComparison.OrdinalIgnoreCase);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !authRequest && token is not null)
            {
                response.Dispose();
                if (await RefreshAfterUnauthorizedAsync(token, cancellationToken))
                {
                    response = await SendOnceAsync(method, path, body, headers, _accessToken, cancellationToken);
                    if (response.IsSuccessStatusCode) return response;
                }
                else
                {
                    RaiseUnauthorized();
                    throw new ApiException(HttpStatusCode.Unauthorized, "Сессия истекла — войдите снова.");
                }
            }
            using (response)
            {
                throw await ResponseErrorAsync(response, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw NetworkError(ex); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { throw TimeoutError(ex); }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, byte[]? body,
        KeyValuePair<string, IEnumerable<string>>[]? headers, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (headers is not null)
                foreach (var header in headers) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (token is not null && path != "api/auth/login" && path != "api/auth/refresh")
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<bool> RefreshAfterUnauthorizedAsync(string failedToken, CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _generation);
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _ended)) return false;
            if (_accessToken is not null && _accessToken != failedToken) return true;
            return IsCurrentGeneration(generation) && _refreshToken is { } token
                && await RefreshCoreAsync(token, generation, cancellationToken);
        }
        finally { _tokenLock.Release(); }
    }

    private async Task<bool> RefreshAsync(string token, CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _generation);
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            return IsCurrentGeneration(generation)
                && await RefreshCoreAsync(token, generation, cancellationToken);
        }
        finally { _tokenLock.Release(); }
    }

    private async Task<bool> RefreshCoreAsync(string token, int generation, CancellationToken cancellationToken)
    {
        try
        {
            var refreshed = await RequestAsync<TokenResponse>(HttpMethod.Post, "api/auth/refresh",
                Json(new RefreshRequest(token)), cancellationToken);
            return SetTokens(refreshed, generation);
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            ClearTokens(generation);
            return false;
        }
    }

    private bool IsCurrentGeneration(int generation) =>
        !Volatile.Read(ref _ended) && Volatile.Read(ref _generation) == generation;

    private bool SetTokens(TokenResponse token, int generation)
    {
        lock (_sessionGate)
        {
            if (_ended || _generation != generation) return false;
            _accessToken = token.AccessToken;
            _refreshToken = token.RefreshToken;
            _generation++;
            _unauthorizedRaised = 0;
            RefreshTokenChanged?.Invoke(_refreshToken);
            return true;
        }
    }

    private void ClearTokens(int generation)
    {
        lock (_sessionGate)
        {
            if (_ended || _generation != generation) return;
            _accessToken = null;
            _refreshToken = null;
            _generation++;
            Teacher = null;
            RefreshTokenChanged?.Invoke(null);
        }
    }

    public void EndSession()
    {
        lock (_sessionGate)
        {
            if (_ended) return;
            _ended = true;
            _generation++;
            _accessToken = null;
            _refreshToken = null;
            Teacher = null;
            RefreshTokenChanged = null;
        }
    }

    private static async Task<ApiException> ResponseErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    using var json = JsonDocument.Parse(body);
                    if (json.RootElement.TryGetProperty("conflicts", out _))
                    {
                        var conflict = JsonSerializer.Deserialize<ScheduleConflictDto>(body, JsonOptions);
                        if (conflict is not null) return new ApiException(response.StatusCode, conflict.Message, conflict: conflict);
                    }
                }
                var error = JsonSerializer.Deserialize<ErrorDto>(body, JsonOptions);
                if (!string.IsNullOrWhiteSpace(error?.Message)) return new ApiException(response.StatusCode, error.Message);
            }
            catch (JsonException) { }
            return new ApiException(response.StatusCode, body);
        }
        return new ApiException(response.StatusCode, $"Ошибка сервера ({(int)response.StatusCode}).");
    }

    private ApiException NetworkError(Exception ex) =>
        new(null, $"Сервер недоступен ({_http.BaseAddress!.ToString().TrimEnd('/')}): {ex.Message}", ex);

    private ApiException TimeoutError(Exception ex) =>
        new(null, $"Сервер недоступен ({_http.BaseAddress!.ToString().TrimEnd('/')}): превышено время ожидания.", ex);

    private void RaiseUnauthorized()
    {
        if (Volatile.Read(ref _ended)) return;
        if (Interlocked.Exchange(ref _unauthorizedRaised, 1) != 0) return;
        if (_uiContext is null || _uiContext == SynchronizationContext.Current) Unauthorized?.Invoke();
        else _uiContext.Post(_ => { if (!Volatile.Read(ref _ended)) Unauthorized?.Invoke(); }, null);
    }

    // _tokenLock не освобождаем: незавершённый запрос обновления токена ещё вызовет Release(),
    // а SemaphoreSlim без AvailableWaitHandle не держит системных ресурсов.
    public void Dispose() { EndSession(); _http.Dispose(); }
}
