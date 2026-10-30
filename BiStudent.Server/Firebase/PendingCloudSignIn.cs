namespace BiStudent.Server.Firebase;

/// <summary>
/// Хранит пароль для повторного облачного входа после потери связи только в памяти процесса.
/// </summary>
public sealed class PendingCloudSignIn
{
    private readonly object gate = new();
    private string? login;
    private string? password;

    public void Remember(string login, string password)
    {
        lock (gate)
        {
            this.login = login;
            this.password = password;
        }
    }

    public bool TryGet(out string login, out string password)
    {
        lock (gate)
        {
            login = this.login ?? "";
            password = this.password ?? "";
            return this.login is not null && this.password is not null;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            login = null;
            password = null;
        }
    }
}
