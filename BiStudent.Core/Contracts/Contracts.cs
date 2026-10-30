namespace BiStudent.Contracts;

public enum WeekParity { Every, Odd, Even }
// Числовые значения закреплены: в старых базах 1 означало «Опоздал», миграция переводит такие отметки в Present.
public enum AttendanceStatus { Present = 0, Absent = 2, Excused = 3 }
public enum ExcuseReason { Illness = 0, Family = 1, Official = 2, Other = 3 }
public enum MarkMethod { Camera, Manual, Demo }
public enum AssessmentKind { Test = 0, ControlWork = 1, Exam = 2, Credit = 3, Other = 4 }
public enum JournalColumnKind { Lesson, Assessment }
public enum FaceSubmissionStatus { Pending = 0, Approved = 1, Rejected = 2, Superseded = 3 }
public enum PublicFaceState { None, Pending, Registered }

public sealed record LoginRequest(string Login, string Password, bool Remember = false);
public sealed record RefreshRequest(string RefreshToken);
public sealed record TokenResponse(string AccessToken, long ExpiresIn, string? RefreshToken = null);
public sealed record TeacherDto(int Id, string FullName, string Login, bool IsAdmin = false, bool MustChangePassword = false, int PendingFaceSubmissions = 0);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record SyncStatusDto(bool Enabled, string State, DateTimeOffset? LastSuccessAt, int Pending, string? Error);

public sealed record TeacherAccountDto(int Id, string Login, string FullName, bool IsAdmin, bool IsActive, bool MustChangePassword, DateTime CreatedAt, DateTime? LastLoginAt);
public sealed record SaveTeacherRequest(string Login, string FullName, bool IsAdmin, bool IsActive);
public sealed record TemporaryPasswordDto(string Login, string Password);

public sealed record GroupDto(int Id, string Name, int Students, int WithFace);
public sealed record StudentDto(int Id, string FullName, int GroupId, bool HasFace, bool FaceUnreadable = false, bool FacePending = false);
public sealed record CreateGroupRequest(string Name);
public sealed record SaveStudentRequest(string FullName, int GroupId);

public sealed record SlotDto(
    int Id, int GroupId, string GroupName, string Subject, string? Room, DayOfWeek DayOfWeek,
    TimeOnly Start, TimeOnly End, WeekParity Parity, DateOnly ValidFrom, DateOnly? ValidTo, DateOnly? OneOffDate);

public sealed record SaveSlotRequest(
    int GroupId, string? NewGroupName, string Subject, string? Room, DayOfWeek DayOfWeek,
    TimeOnly Start, TimeOnly End, WeekParity Parity, DateOnly? OneOffDate, List<int>? ReplaceSlotIds = null);

public sealed record LessonDto(SlotDto Slot, DateOnly Date, DateTime StartsAt, DateTime EndsAt, bool Cancelled);
public sealed record ScheduleConflictDto(string Message, List<SlotDto> Conflicts);

public sealed record CancelLessonRequest(DateOnly Date, string? Reason);
public sealed record RestoreLessonRequest(DateOnly Date);

public sealed record AttendanceRowDto(int StudentId, string FullName, AttendanceStatus? Status, MarkMethod? Method, DateTime? MarkedAt,
    ExcuseReason? Reason = null, string? Comment = null, bool HasDocument = false, int? Score = null);
public sealed record MarkRequest(int StudentId, AttendanceStatus? Status, ExcuseReason? Reason = null, string? Comment = null, bool HasDocument = false);
public sealed record GradeRequest(int StudentId, int? Score);
public sealed record CountResult(int Count);

public sealed record AssessmentDto(int Id, int GroupId, string GroupName, string Subject, string Title, AssessmentKind Kind, DateOnly Date);
public sealed record SaveAssessmentRequest(int GroupId, string Subject, string Title, AssessmentKind Kind, DateOnly Date);
public sealed record FinalGradeRequest(string Subject, int StudentId, int? Score);
public sealed record FillFinalRequest(string Subject);

public sealed record JournalColumnDto(JournalColumnKind Kind, int Id, DateOnly Date, TimeOnly? Start, string Subject,
    string? Title = null, AssessmentKind? AssessmentKind = null);
public sealed record JournalCellDto(AttendanceStatus? Status, ExcuseReason? Reason, string? Comment, int? Score, bool InGroup = true);
public sealed record JournalStudentDto(int StudentId, string FullName, List<JournalCellDto> Cells,
    int Present, int Absent, int Excused, int MissedHours, int ExcusedHours, double Percent, double? Average, int? Final);
public sealed record JournalDto(string TeacherName, string GroupName, string? Subject, DateOnly From, DateOnly To,
    int HoursPerLesson, List<JournalColumnDto> Columns, List<JournalStudentDto> Students);

public sealed record GroupAttendanceDto(int GroupId, string GroupName, double Percent);
public sealed record AtRiskStudentDto(int StudentId, string FullName, string GroupName, double Percent, string Reason);
public sealed record TrendPointDto(DateOnly Date, double Percent);
public sealed record DashboardDto(
    int LessonsToday, LessonDto? Current, LessonDto? Next, List<LessonDto> Today,
    double Attendance7Days, double Attendance30Days, List<GroupAttendanceDto> Groups, List<AtRiskStudentDto> AtRisk,
    int CameraMarks7Days, int CameraMarks30Days, double SavedMinutes7Days, double SavedMinutes30Days,
    string SavingsFormula, List<TrendPointDto> Trend, double? AverageScore30Days = null, int PendingFaceSubmissions = 0);

public sealed record ReportDto(
    DashboardDto Dashboard, int ReportsExported30Days, int LessonsPerWeek, int GroupCount, int AverageGroupSize,
    int SecondsPerManualMark, int MinutesPerManualReport);

public sealed record AutomationSettingsDto(int SecondsPerManualMark, int MinutesPerManualReport, int FramesInRow);
public sealed record RecognitionSettingsDto(double MatchThreshold, double MinimumMargin, int FramesInRow);

public sealed record RecognizeRequest(int SlotId, DateOnly Date, byte[] Image);
public sealed record RecognizedFaceDto(int X, int Y, int W, int H, int? StudentId, string? Name, double Similarity,
    bool Marked, bool AlreadyMarked, bool OtherGroup, string? CandidateName = null, double Margin = 0);
public sealed record RecognitionResultDto(List<RecognizedFaceDto> Faces, int FrameWidth, int FrameHeight, string? Error = null);

public sealed record EnrollRequest(List<byte[]> Images, bool AllowSimilar = false);
public sealed record EnrollResultDto(bool Ok, string Message, string? SimilarTo = null, double SimilarScore = 0, List<string>? Problems = null);

// Регистрация лица по коду группы: преподаватель выдаёт код, студент сам снимается на сайте, преподаватель подтверждает.
public sealed record EnrollmentCodeDto(int GroupId, string GroupName, string Code, DateTime CreatedAt, DateTime ExpiresAt, List<string> Links);
public sealed record CreateEnrollmentCodeRequest(int Hours = 24);
public sealed record FaceSubmissionDto(int Id, int StudentId, string StudentName, int GroupId, string GroupName, DateTime CreatedAt,
    FaceSubmissionStatus Status, int ImageCount, bool ReplacesExisting, string? SimilarTo, double SimilarScore, string? RejectReason);
public sealed record RejectSubmissionRequest(string? Reason);

public sealed record PublicCodeRequest(string Code);
public sealed record PublicStudentDto(int Id, string FullName, PublicFaceState State);
public sealed record PublicGroupDto(string GroupName, DateTime ExpiresAt, List<PublicStudentDto> Students);
public sealed record PublicSubmitRequest(string Code, int StudentId, List<string> Images, bool Consent);
public sealed record PublicSubmitResultDto(bool Ok, string Message, List<string>? Problems = null);

public sealed record ImportResultDto(int Added, int Skipped, List<string> Errors);

// Импорт расписания колледжа из Excel: сначала предпросмотр (какие группы и преподаватели найдены), затем импорт выбранных групп.
public sealed record ScheduleImportGroupDto(string Sheet, string Group, int Lessons, bool Exists, bool AlreadyLoaded = false);
public sealed record ScheduleImportPreviewDto(DateOnly SemesterStart, List<ScheduleImportGroupDto> Groups, List<string> Teachers, List<string> Warnings,
    string? FileTeacher = null, int? FileTeacherId = null);
public sealed record ImportedTeacherDto(string FullName, string Login, string? TemporaryPassword, bool Created, bool Active);
public sealed record ScheduleImportResultDto(int Added, int Skipped, int GroupsCreated, List<ImportedTeacherDto> Teachers, List<string> Warnings,
    int Updated = 0, int Removed = 0, List<string>? Unchanged = null);
public sealed record ErrorDto(string Message);

public sealed record BackupFileDto(string Name, DateTime CreatedAt, long Size, bool Automatic);
public sealed record RestoreResultDto(bool Ok, string Message, int Students, int Marks, bool RestartRecommended);
