namespace BiStudent.Server.Firebase;

public sealed class FirebaseOptions
{
    public string ApiKey { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string? AuthEmulatorHost { get; set; }
    public string? FirestoreEmulatorHost { get; set; }
    public string EmailDomain { get; set; } = "bistudent.app";
    public int SyncIntervalSeconds { get; set; } = 60;
    public int RequestTimeoutSeconds { get; set; } = 15;

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(ProjectId);

    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Firebase:ApiKey"]) &&
        !string.IsNullOrWhiteSpace(configuration["Firebase:ProjectId"]);

    public string EmailFor(string login) => login.Trim().ToLowerInvariant() + "@" + EmailDomain;
}
