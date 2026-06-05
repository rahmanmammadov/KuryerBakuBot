using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KuryerBakuBot
{
    // This model holds the result of our rate-limiting checks
    public class ModerationResult
    {
        public bool IsAllowed { get; set; }
        public bool ShouldWarn { get; set; }
    }

    public class DatabaseService
    {
        private readonly string _connectionString;
        private readonly ILogger<DatabaseService> _logger;
        
        // This lock ensures only one thread writes to SQLite at a time, preventing database locks
        private readonly SemaphoreSlim _dbLock = new(1, 1);

        public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
        {
            _logger = logger;
            
            // We get the connection string from appsettings.json, or default to a local bot.db file
            string? configConnection = configuration.GetConnectionString("DefaultConnection");
            _connectionString = configConnection ?? "Data Source=bot.db";
        }

        // Creates the database tables if they do not exist
        public async Task InitializeAsync()
        {
            await _dbLock.WaitAsync();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                string createTableSql = @"
                    CREATE TABLE IF NOT EXISTS UserWindows (
                        UserId INTEGER NOT NULL,
                        ChatId INTEGER NOT NULL,
                        WindowStart TEXT NOT NULL,
                        MediaCount INTEGER NOT NULL,
                        HasWarned INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (UserId, ChatId)
                    );";

                using var command = new SqliteCommand(createTableSql, connection);
                await command.ExecuteNonQueryAsync();
                
                _logger.LogInformation("SQLite Database initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while initializing the database.");
                throw;
            }
            finally
            {
                _dbLock.Release();
            }
        }

        // Processes an incoming media message. It automatically handles window expiration and increments counts.
        public async Task<ModerationResult> ProcessMediaMessageAsync(long userId, long chatId, int windowSeconds, int maxMediaAllowed)
        {
            await _dbLock.WaitAsync();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                DateTime now = DateTime.UtcNow;
                string nowString = now.ToString("o"); // ISO 8601 string format (safe for database storage)

                // 1. Fetch the user's current window state
                string selectSql = "SELECT WindowStart, MediaCount, HasWarned FROM UserWindows WHERE UserId = @UserId AND ChatId = @ChatId";
                using var selectCommand = new SqliteCommand(selectSql, connection);
                selectCommand.Parameters.AddWithValue("@UserId", userId);
                selectCommand.Parameters.AddWithValue("@ChatId", chatId);

                DateTime windowStart = now;
                int mediaCount = 0;
                int hasWarned = 0;
                bool recordExists = false;

                using (var reader = await selectCommand.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        recordExists = true;
                        // CRITICAL FIX: We force C# to parse the DB string into UTC format strictly
                        windowStart = DateTime.Parse(reader.GetString(0)).ToUniversalTime();
                        mediaCount = reader.GetInt32(1);
                        hasWarned = reader.GetInt32(2);
                    }
                }

                // 2. Check if the active window has expired
                bool isExpired = !recordExists || (now - windowStart).TotalSeconds >= windowSeconds;

                if (isExpired)
                {
                    // The window is expired or doesn't exist yet. We reset the counter and start a new window.
                    mediaCount = 1;
                    hasWarned = 0;
                    windowStart = now;

                    string insertOrReplaceSql = @"
                        INSERT OR REPLACE INTO UserWindows (UserId, ChatId, WindowStart, MediaCount, HasWarned)
                        VALUES (@UserId, @ChatId, @WindowStart, @MediaCount, @HasWarned)";
                    
                    using var updateCommand = new SqliteCommand(insertOrReplaceSql, connection);
                    updateCommand.Parameters.AddWithValue("@UserId", userId);
                    updateCommand.Parameters.AddWithValue("@ChatId", chatId);
                    updateCommand.Parameters.AddWithValue("@WindowStart", nowString);
                    updateCommand.Parameters.AddWithValue("@MediaCount", mediaCount);
                    updateCommand.Parameters.AddWithValue("@HasWarned", hasWarned);
                    
                    await updateCommand.ExecuteNonQueryAsync();

                    return new ModerationResult { IsAllowed = true, ShouldWarn = false };
                }
                else
                {
                    // The window is still active. Increment the media count.
                    mediaCount++;
                    bool shouldWarn = false;
                    bool isAllowed = true;

                    if (mediaCount > maxMediaAllowed)
                    {
                        isAllowed = false;
                        
                        // We only warn once per active window.
                        if (hasWarned == 0)
                        {
                            shouldWarn = true;
                            hasWarned = 1; // Mark warning as sent
                        }
                    }

                    string updateSql = @"
                        UPDATE UserWindows 
                        SET MediaCount = @MediaCount, HasWarned = @HasWarned 
                        WHERE UserId = @UserId AND ChatId = @ChatId";

                    using var updateCommand = new SqliteCommand(updateSql, connection);
                    updateCommand.Parameters.AddWithValue("@MediaCount", mediaCount);
                    updateCommand.Parameters.AddWithValue("@HasWarned", hasWarned);
                    updateCommand.Parameters.AddWithValue("@UserId", userId);
                    updateCommand.Parameters.AddWithValue("@ChatId", chatId);

                    await updateCommand.ExecuteNonQueryAsync();

                    return new ModerationResult { IsAllowed = isAllowed, ShouldWarn = shouldWarn };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProcessMediaMessageAsync for User {UserId} in Chat {ChatId}", userId, chatId);
                // In case of database error, we default to allowing the message to prevent breaking the group
                return new ModerationResult { IsAllowed = true, ShouldWarn = false };
            }
            finally
            {
                _dbLock.Release();
            }
        }

        // Rollback mechanism: Decrements the user's active media count if an album was deleted
        public async Task DecrementMediaCountAsync(long userId, long chatId, int countToSubtract)
        {
            await _dbLock.WaitAsync();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                // We decrement the count, but ensure it never falls below 0 using SQLite's MAX function
                string rollbackSql = @"
                    UPDATE UserWindows 
                    SET MediaCount = MAX(0, MediaCount - @SubCount) 
                    WHERE UserId = @UserId AND ChatId = @ChatId";

                using var command = new SqliteCommand(rollbackSql, connection);
                command.Parameters.AddWithValue("@SubCount", countToSubtract);
                command.Parameters.AddWithValue("@UserId", userId);
                command.Parameters.AddWithValue("@ChatId", chatId);

                await command.ExecuteNonQueryAsync();
                _logger.LogInformation("Rolled back {Count} media items for User {UserId} in Chat {ChatId}", countToSubtract, userId, chatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rolling back media count for User {UserId} in Chat {ChatId}", userId, chatId);
            }
            finally
            {
                _dbLock.Release();
            }
        }
    }
}