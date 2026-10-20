using System.Security.Claims;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Services;

namespace BiStudent.Server.Endpoints;

public static class GradeEndpoints
{
    internal static void MapGrades(this RouteGroupBuilder api)
    {
        api.MapPut("/lessons/{slotId:int}/{date}/grade", async (
            int slotId, DateOnly date, GradeRequest request, ClaimsPrincipal user, AppDbContext db,
            ScheduleService schedule, AttendanceService attendance) =>
        {
            await ApiEndpoints.EnsureSlotAsync(db, user, slotId);
            await ApiEndpoints.EnsureLessonAsync(schedule, slotId, date,
                skipFuture: request.Score is null, allowMissing: request.Score is null);
            await attendance.SetLessonGradeAsync(slotId, date, request.StudentId, request.Score);
            return Results.NoContent();
        });

        api.MapGet("/journal/{groupId:int}/subjects", (int groupId, ClaimsPrincipal user, GradeService grades) =>
            grades.SubjectsAsync(ApiEndpoints.TeacherId(user), groupId));

        api.MapGet("/assessments", (int? groupId, string? subject, ClaimsPrincipal user, GradeService grades) =>
            grades.ListAsync(ApiEndpoints.TeacherId(user), groupId, subject));
        api.MapPost("/assessments", (SaveAssessmentRequest request, ClaimsPrincipal user, GradeService grades) =>
            grades.SaveAsync(ApiEndpoints.TeacherId(user), null, request));
        api.MapPut("/assessments/{id:int}", (int id, SaveAssessmentRequest request, ClaimsPrincipal user, GradeService grades) =>
            grades.SaveAsync(ApiEndpoints.TeacherId(user), id, request));
        api.MapDelete("/assessments/{id:int}", async (int id, ClaimsPrincipal user, GradeService grades) =>
        {
            await grades.DeleteAsync(ApiEndpoints.TeacherId(user), id);
            return Results.NoContent();
        });
        api.MapPut("/assessments/{id:int}/grades", async (
            int id, GradeRequest request, ClaimsPrincipal user, GradeService grades) =>
        {
            await grades.SetGradeAsync(ApiEndpoints.TeacherId(user), id, request.StudentId, request.Score);
            return Results.NoContent();
        });

        api.MapPut("/journal/{groupId:int}/final", async (
            int groupId, FinalGradeRequest request, ClaimsPrincipal user, GradeService grades) =>
        {
            await grades.SetFinalAsync(ApiEndpoints.TeacherId(user), groupId, request.Subject, request.StudentId, request.Score);
            return Results.NoContent();
        });
        api.MapPost("/journal/{groupId:int}/final/fill", async (
            int groupId, FillFinalRequest request, ClaimsPrincipal user, GradeService grades) =>
            new CountResult(await grades.FillFinalAsync(ApiEndpoints.TeacherId(user), groupId, request.Subject)));
    }
}
