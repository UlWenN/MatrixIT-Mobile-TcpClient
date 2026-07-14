namespace MatrixIT_Mobile;

public interface IDatabaseService
{
    Task<IReadOnlyList<LogMessage>> GetAllLogsAsync();

    Task SaveLogAsync(LogMessage log);

    Task ClearLogsAsync();
}