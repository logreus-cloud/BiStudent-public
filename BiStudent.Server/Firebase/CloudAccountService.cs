using BiStudent.Server.Data;
using BiStudent.Server.Sync;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BiStudent.Server.Firebase;

public sealed record CloudLoginResult(Teacher? Teacher, int Status, string? Error, bool Online);

public sealed class CloudAccountService(
    AppDbContext db, IPasswordHasher<Teacher> hasher, FirebaseAuthClient auth,
    FirestoreClient firestore, FirebaseSessionStore sessions, SyncService sync,
    SyncSignal signal, IOptions<FirebaseOptions> options, ILogger<CloudAccountService> logger)
{
    private static readonly SemaphoreSlim loginGate = new(1, 1);
    private readonly FirebaseOptions settings = options.Value;

    public bool Enabled => settings.Enabled;

    public async Task<CloudLoginResult> SignInAsync(
        string login, string password, CancellationToken cancellationToken)
    {
        await loginGate.WaitAsync(cancellationToken);
        try { return await SignInCoreAsync(login, password, cancellationToken); }
        finally { loginGate.Release(); }
    }

    private async Task<CloudLoginResult> SignInCoreAsync(
        string login, string password, CancellationToken cancellationToken)
    {
        var lower = login.Trim().ToLowerInvariant();
        var local = await db.Teachers.SingleOrDefaultAsync(
            teacher => teacher.Login.ToLower() == lower, cancellationToken);
        CloudLoginResult result;
        if (!Enabled)
            result = LocalResult(local, password);
        else
        {
            try
            {
                var session = await auth.SignInAsync(settings.EmailFor(login), password,
                    cancellationToken);
                result = await LinkAndAcceptAsync(session, local, password, cancellationToken);
            }
            catch (FirebaseAuthException exception) when (exception.IsBadCredentials)
            {
                if (local is not { FirebaseUid: null } || !Matches(local, password))
                    result = Failure(401, "Неверный логин или пароль.");
                else
                {
                    try
                    {
                        var session = await auth.SignUpAsync(settings.EmailFor(login), password,
                            cancellationToken);
                        result = await LinkAndAcceptAsync(
                            session, local, password, cancellationToken);
                    }
                    catch (FirebaseAuthException signup) when (signup.Code == "WEAK_PASSWORD")
                    {
                        result = LocalResult(local, password);
                    }
                    catch (FirebaseAuthException signup) when (signup.Code == "EMAIL_EXISTS")
                    {
                        result = Failure(401, "Неверный логин или пароль.");
                    }
                    catch (FirebaseUnavailableException)
                    {
                        result = LocalResult(local, password);
                    }
                    catch (FirebaseAuthException signup)
                    {
                        logger.LogWarning(signup,
                            "Перенос учётной записи {Login} отложен", login);
                        result = LocalResult(local, password);
                    }
                }
            }
            catch (FirebaseAuthException exception)
            {
                result = exception.Code switch
                {
                    "USER_DISABLED" => Failure(403,
                        "Учётная запись отключена в Firebase."),
                    "TOO_MANY_ATTEMPTS_TRY_LATER" => Failure(429,
                        "Слишком много попыток входа. Подождите минуту."),
                    "OPERATION_NOT_ALLOWED" or "CONFIGURATION_NOT_FOUND" => Failure(503,
                        "В проекте Firebase не включён вход по e-mail и паролю."),
                    _ => Failure(401, "Неверный логин или пароль.")
                };
            }
            catch (FirebaseUnavailableException)
            {
                result = LocalResult(local, password);
            }
        }

        if (result.Teacher is null) return result;
        var teacher = result.Teacher;
        if (!teacher.IsActive)
            return Failure(403, "Учётная запись отключена. Обратитесь к администратору.");
        var verification = Verify(teacher, password);
        if (verification is PasswordVerificationResult.Failed or
            PasswordVerificationResult.SuccessRehashNeeded)
            teacher.PasswordHash = hasher.HashPassword(teacher, password);
        teacher.LastLoginAt = DateTime.Now;
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task ChangePasswordAsync(
        Teacher teacher, string currentPassword, string newPassword,
        CancellationToken cancellationToken)
    {
        await loginGate.WaitAsync(cancellationToken);
        try
        {
            await ChangePasswordCoreAsync(
                teacher, currentPassword, newPassword, cancellationToken);
        }
        finally { loginGate.Release(); }
    }

    private async Task ChangePasswordCoreAsync(
        Teacher teacher, string currentPassword, string newPassword,
        CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        if (teacher.FirebaseUid is not null)
        {
            try
            {
                FirebaseSession session;
                try
                {
                    session = await auth.SignInAsync(
                        settings.EmailFor(teacher.Login), currentPassword, cancellationToken);
                }
                catch (FirebaseAuthException exception) when (exception.IsBadCredentials)
                {
                    throw new InvalidOperationException("Текущий пароль указан неверно.", exception);
                }
                var changed = await auth.ChangePasswordAsync(
                    session.IdToken, newPassword, cancellationToken);
                AcceptSession(changed, teacher.Id);
            }
            catch (FirebaseUnavailableException exception)
            {
                throw new InvalidOperationException(
                    "Сменить пароль можно только при подключении к интернету.", exception);
            }
            return;
        }

        if (!Matches(teacher, currentPassword))
            throw new InvalidOperationException("Текущий пароль указан неверно.");
        try
        {
            var session = await auth.SignUpAsync(
                settings.EmailFor(teacher.Login), newPassword, cancellationToken);
            var result = await LinkAndAcceptAsync(
                session, teacher, currentPassword, cancellationToken);
            if (!result.Online)
                logger.LogWarning("Перенос учётной записи {Login} отложен: {Reason}",
                    teacher.Login, result.Error ?? "нет прав на привязку");
        }
        catch (FirebaseUnavailableException)
        {
            logger.LogWarning("Перенос учётной записи {Login} отложен: нет связи с Firebase",
                teacher.Login);
        }
        catch (FirebaseAuthException exception) when (exception.Code == "EMAIL_EXISTS")
        {
            logger.LogWarning(exception,
                "Перенос учётной записи {Login} отложен: e-mail уже занят", teacher.Login);
        }
    }

    public async Task<string?> CreateAccountAsync(
        Teacher teacher, string password, CancellationToken cancellationToken) =>
        await CreateAccountCoreAsync(teacher, password, false, cancellationToken);

    public async Task LinkWithPasswordAsync(
        Teacher teacher, string password, CancellationToken cancellationToken)
    {
        await CreateAccountCoreAsync(teacher, password, true, cancellationToken);
    }

    private async Task<string?> CreateAccountCoreAsync(
        Teacher teacher, string password, bool existing, CancellationToken cancellationToken)
    {
        if (existing && teacher.FirebaseUid is not null)
            throw new InvalidOperationException("Учётная запись уже связана с Firebase.");
        FirebaseSession? admin;
        try { admin = await sessions.GetAsync(cancellationToken); }
        catch (FirebaseUnavailableException exception)
        {
            throw new InvalidOperationException(
                "Нет связи с Firebase — учётную запись создать нельзя.", exception);
        }
        if (admin is null)
            throw new InvalidOperationException(
                "Чтобы создать учётную запись, войдите как администратор при подключении к интернету.");

        FirebaseSession created;
        try
        {
            created = await auth.SignUpAsync(
                settings.EmailFor(teacher.Login), password, cancellationToken);
        }
        catch (FirebaseAuthException exception) when (exception.Code == "EMAIL_EXISTS")
        {
            throw new InvalidOperationException(
                existing
                    ? "Учётная запись с таким логином уже есть в Firebase — сбросить пароль может владелец проекта в консоли Firebase."
                    : "Учётная запись с таким логином уже есть в Firebase.", exception);
        }
        catch (FirebaseUnavailableException exception)
        {
            throw new InvalidOperationException(
                "Нет связи с Firebase — учётную запись создать нельзя.", exception);
        }

        var previousUid = teacher.FirebaseUid;
        var previousMustChangePassword = teacher.MustChangePassword;
        try
        {
            SetFirebaseUid(teacher, created.Uid);
            if (existing) teacher.MustChangePassword = true;
            await SaveRemoteAsync(cancellationToken);
            var cloudId = await sync.EnsureLinkAsync(db, "Teachers", teacher.Id);
            await firestore.CommitAsync(admin.IdToken,
            [
                await sync.BuildWriteAsync(db, "Teachers", teacher.Id),
                new FirestoreWrite("TeacherUids", created.Uid,
                    new Dictionary<string, object?> { ["teacher"] = cloudId },
                    MustExist: false, ServerTimestamp: false)
            ], cancellationToken);
            signal.Notify();
            return created.Uid;
        }
        catch
        {
            try
            {
                SetFirebaseUid(teacher, previousUid);
                teacher.MustChangePassword = previousMustChangePassword;
                await SaveRemoteAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Не удалось откатить привязку преподавателя {Id}", teacher.Id);
            }
            try { await auth.DeleteAccountAsync(created.IdToken, CancellationToken.None); }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Не удалось удалить учётную запись Firebase {Uid}", created.Uid);
            }
            throw;
        }
    }

    public string? ResetPasswordBlockedReason(Teacher teacher) =>
        Enabled && teacher.FirebaseUid is not null
            ? "Пароль учётной записи Firebase может сменить только сам преподаватель (или владелец проекта в консоли Firebase → Authentication)."
            : null;

    public string? LoginChangeBlockedReason(Teacher teacher, string newLogin) =>
        Enabled && teacher.FirebaseUid is not null &&
        !string.Equals(teacher.Login, newLogin, StringComparison.OrdinalIgnoreCase)
            ? "Логин связан с учётной записью Firebase и не меняется."
            : null;

    private static bool IsTrue(FirestoreDocument document, string field) =>
        document.Fields.TryGetValue(field, out var value) &&
        value.TryGetProperty("booleanValue", out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True;

    private async Task<CloudLoginResult> LinkAndAcceptAsync(
        FirebaseSession session, Teacher? local, string password,
        CancellationToken cancellationToken)
    {
        try
        {
            var mapping = await firestore.GetAsync(
                session.IdToken, "TeacherUids", session.Uid, cancellationToken);
            if (mapping is not null)
            {
                try
                {
                    // Активность проверяем по самому облачному документу: локальная копия могла не обновиться
                    // (например, из-за неотправленных правок). Отключённому правила не дадут даже прочитать его.
                    var cloudTeacher = mapping.IdFromTeacher() is { } teacherCloudId
                        ? await firestore.GetAsync(session.IdToken, "Teachers", teacherCloudId, cancellationToken)
                        : null;
                    if (cloudTeacher is null || !IsTrue(cloudTeacher, "IsActive") || IsTrue(cloudTeacher, "_deleted"))
                        return Failure(403, "Учётная запись отключена. Обратитесь к администратору.");
                    await sync.PullAsync(["Teachers"], cancellationToken, session);
                    db.ChangeTracker.Clear();
                    var teacher = await db.Teachers.SingleOrDefaultAsync(
                        candidate => candidate.FirebaseUid == session.Uid, cancellationToken);
                    var linked = teacher is not null && await db.SyncLinks.AnyAsync(
                        candidate => candidate.Table == "Teachers" &&
                            candidate.LocalId == teacher.Id &&
                            candidate.CloudId == mapping.IdFromTeacher(), cancellationToken);
                    if (!linked)
                    {
                        return Failure(403,
                            "Учётная запись не найдена в облаке BiStudent. Обратитесь к администратору.");
                    }
                    if (!teacher!.IsActive)
                        return Failure(403,
                            "Учётная запись отключена. Обратитесь к администратору.");
                    AcceptSession(session, teacher!.Id);
                    return new CloudLoginResult(teacher, 200, null, true);
                }
                catch (FirestorePermissionException exception)
                {
                    logger.LogWarning(exception,
                        "Доступ к привязке Firebase {Uid} запрещён", session.Uid);
                    return Failure(403,
                        "Учётная запись отключена. Обратитесь к администратору.");
                }
            }

            if (local is { IsAdmin: true, IsActive: true, FirebaseUid: null } &&
                Matches(local, password) &&
                await firestore.GetAsync(
                    session.IdToken, "meta", "bootstrap", cancellationToken) is null)
            {
                SetFirebaseUid(local, session.Uid);
                await SaveRemoteAsync(cancellationToken);
                try
                {
                    var cloudId = await sync.EnsureLinkAsync(db, "Teachers", local.Id);
                    await firestore.CommitAsync(session.IdToken,
                    [
                        new FirestoreWrite("meta", "bootstrap",
                            new Dictionary<string, object?> { ["uid"] = session.Uid },
                            MustExist: false, ServerTimestamp: false),
                        await sync.BuildWriteAsync(db, "Teachers", local.Id),
                        new FirestoreWrite("TeacherUids", session.Uid,
                            new Dictionary<string, object?> { ["teacher"] = cloudId },
                            MustExist: false, ServerTimestamp: false)
                    ], cancellationToken);
                }
                catch
                {
                    SetFirebaseUid(local, null);
                    await SaveRemoteAsync(CancellationToken.None);
                    throw;
                }
                AcceptSession(session, local.Id);
                signal.Notify();
                return new CloudLoginResult(local, 200, null, true);
            }

            if (local is { FirebaseUid: null, ClaimToken: not null } &&
                Matches(local, password))
            {
                var link = await db.SyncLinks.FirstOrDefaultAsync(candidate =>
                    candidate.Table == "Teachers" && candidate.LocalId == local.Id,
                    cancellationToken);
                if (link is not null)
                {
                    await firestore.CommitAsync(session.IdToken,
                    [
                        new FirestoreWrite("Teachers", link.CloudId,
                            new Dictionary<string, object?>
                            {
                                ["FirebaseUid"] = session.Uid,
                                ["ClaimProof"] = local.ClaimToken
                            }, ["FirebaseUid", "ClaimProof"]),
                        new FirestoreWrite("TeacherUids", session.Uid,
                            new Dictionary<string, object?> { ["teacher"] = link.CloudId },
                            MustExist: false, ServerTimestamp: false)
                    ], cancellationToken);
                    try
                    {
                        await sync.PullAsync(["Teachers"], cancellationToken, session);
                    }
                    catch (FirestorePermissionException exception)
                    {
                        logger.LogWarning(exception,
                            "Доступ к привязке Firebase {Uid} запрещён", session.Uid);
                        return Failure(403,
                            "Учётная запись отключена. Обратитесь к администратору.");
                    }
                    db.ChangeTracker.Clear();
                    var teacher = await db.Teachers.SingleOrDefaultAsync(
                        candidate => candidate.FirebaseUid == session.Uid, cancellationToken);
                    if (teacher is null)
                        return Failure(403,
                            "Учётная запись не найдена в облаке BiStudent. Обратитесь к администратору.");
                    if (!teacher.IsActive)
                        return Failure(403,
                            "Учётная запись отключена. Обратитесь к администратору.");
                    AcceptSession(session, teacher.Id);
                    return new CloudLoginResult(teacher, 200, null, true);
                }
            }

            return Unlinked(local, password, "нет подходящего преподавателя");
        }
        catch (FirestorePermissionException exception)
        {
            return Unlinked(local, password, exception.Message);
        }
        catch (FirebaseUnavailableException)
        {
            return Matches(local, password)
                ? new CloudLoginResult(local, 200, null, false)
                : Failure(503, "Нет связи с Firebase, а на этом компьютере вы ещё не входили.");
        }
    }

    private void AcceptSession(FirebaseSession session, int teacherId)
    {
        var previousUid = sessions.Uid;
        sessions.Set(session, teacherId);
        if (previousUid != session.Uid)
            sync.Reset();
    }

    private static void SetFirebaseUid(Teacher teacher, string? uid)
    {
        if (teacher.FirebaseUid == uid) return;
        teacher.FirebaseUid = uid;
        teacher.SecurityStamp = Guid.NewGuid().ToString("N");
    }

    private CloudLoginResult Unlinked(Teacher? local, string password, string reason)
    {
        if (!Matches(local, password))
            return Failure(403,
                "Учётная запись Firebase не привязана к преподавателю BiStudent. Обратитесь к администратору.");
        logger.LogWarning("Вход {Login} оставлен локальным: {Reason}", local!.Login, reason);
        return new CloudLoginResult(local, 200, null, false);
    }

    private CloudLoginResult LocalResult(Teacher? local, string password) =>
        Matches(local, password)
            ? new CloudLoginResult(local, 200, null, false)
            : local is null || string.IsNullOrEmpty(local.PasswordHash)
                ? Failure(503, "Нет связи с Firebase, а на этом компьютере вы ещё не входили.")
                : Failure(401, "Неверный логин или пароль.");

    private PasswordVerificationResult Verify(Teacher teacher, string password) =>
        string.IsNullOrEmpty(teacher.PasswordHash) ? PasswordVerificationResult.Failed
            : hasher.VerifyHashedPassword(teacher, teacher.PasswordHash, password);

    private bool Matches(Teacher? teacher, string password) =>
        teacher is not null && Verify(teacher, password) != PasswordVerificationResult.Failed;

    private async Task SaveRemoteAsync(CancellationToken cancellationToken)
    {
        var applyingRemote = db.ApplyingRemote;
        db.ApplyingRemote = true;
        try { await db.SaveChangesAsync(cancellationToken); }
        finally { db.ApplyingRemote = applyingRemote; }
    }

    private static CloudLoginResult Failure(int status, string error) =>
        new(null, status, error, false);
}

internal static class TeacherMapping
{
    public static string? IdFromTeacher(this FirestoreDocument mapping) =>
        mapping.Fields.TryGetValue("teacher", out var value) &&
        value.TryGetProperty("stringValue", out var id) ? id.GetString() : null;
}
