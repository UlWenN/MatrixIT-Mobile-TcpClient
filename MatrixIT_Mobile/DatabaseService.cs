using Microsoft.Maui.Storage;
using SQLite;

namespace MatrixIT_Mobile;

public sealed class DatabaseService : IDatabaseService
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SemaphoreSlim _databaseOperationLock = new(1, 1);

    private SQLiteAsyncConnection? _database;

    private async Task<SQLiteAsyncConnection> GetDatabaseAsync()
    {
        if (_database is not null)
        {
            return _database;
        }

        await _initializationLock.WaitAsync();

        try
        {
            if (_database is not null)
            {
                return _database;
            }

            string databasePath = Path.Combine(
                FileSystem.AppDataDirectory,
                "TerminalData.db3");

            var database = new SQLiteAsyncConnection(
                databasePath,
                SQLiteOpenFlags.ReadWrite |
                SQLiteOpenFlags.Create |
                SQLiteOpenFlags.SharedCache);

            await database.CreateTableAsync<LogMessage>();

            _database = database;

            return database;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task SaveLogAsync(LogMessage log)
    {
        ArgumentNullException.ThrowIfNull(log);

        await _databaseOperationLock.WaitAsync();

        try
        {
            SQLiteAsyncConnection database = await GetDatabaseAsync();

            await database.InsertAsync(log);
        }
        finally
        {
            _databaseOperationLock.Release();
        }
    }

    public async Task<IReadOnlyList<LogMessage>> GetAllLogsAsync()
    {
        await _databaseOperationLock.WaitAsync();

        try
        {
            SQLiteAsyncConnection database = await GetDatabaseAsync();

            List<LogMessage> logs = await database
                .Table<LogMessage>()
                .OrderBy(log => log.Timestamp)
                .ToListAsync();

            // при одинаковом времени сортируется по id
            return logs
                .OrderBy(log => log.Timestamp)
                .ThenBy(log => log.Id)
                .ToList();
        }
        finally
        {
            _databaseOperationLock.Release();
        }
    }

    public async Task ClearLogsAsync()
    {
        await _databaseOperationLock.WaitAsync();

        try
        {
            SQLiteAsyncConnection database = await GetDatabaseAsync();

            await database.DeleteAllAsync<LogMessage>();
        }
        finally
        {
            _databaseOperationLock.Release();
        }
    }
}