using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KuryerBakuBot
{
    public class ModerationResult
    {
        public bool IsAllowed { get; set; }
        public bool ShouldWarn { get; set; }
    }

    public class PendingDeletion
    {
        public long ChatId { get; set; }
        public int MessageId { get; set; }
    }

    public class DatabaseService
    {
        private readonly string _connectionString;
        private readonly ILogger<DatabaseService> _logger;
        private readonly SemaphoreSlim _dbLock = new(1, 1);

        public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
        {
            _logger = logger;
            string? configConnection = configuration.GetConnectionString("DefaultConnection");
            _connectionString = configConnection ?? "Data Source=bot.db";
        }

        public async Task InitializeAsync()
        {
            await _dbLock.WaitAsync();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                // Table 1: User Windows
                string createUserWindowsTableSql = @"
                    CREATE TABLE IF NOT EXISTS UserWindows (
                        UserId INTEGER NOT NULL,
                        ChatId INTEGER NOT NULL,
                        WindowStart TEXT NOT NULL,
                        MediaCount INTEGER NOT NULL,
                        HasWarned INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (UserId, ChatId)
                    );";

                using var command1 = new SqliteCommand(createUserWindowsTableSql, connection);
                await command1.ExecuteNonQueryAsync();

                // Table 2: Pending Deletions
                string createPendingDeletionsTableSql = @"
                    CREATE TABLE IF NOT EXISTS PendingDeletions (
                        ChatId INTEGER NOT NULL,
                        MessageId INTEGER NOT NULL,
                        DeleteAt TEXT NOT NULL,
                        PRIMARY KEY (ChatId, MessageId)
                    );";

                using var command2 = new SqliteCommand(createPendingDeletionsTableSql, connection);
                await command2.ExecuteNonQueryAsync();
                
                _logger.LogInformation("SQLite Database tables initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while initializing database tables.");
                throw;
            }
            finally
            {
                _dbLock.Release();
            }
        }

        public async Task<ModerationResult> ProcessMediaMessageAsync(long userId, long chatId, int windowSeconds, int maxMediaAllowed)
        {
            await _dbLock.WaitAsync();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                DateTime now = DateTime.UtcNow;
                string nowString = now.ToString("o");

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
                        windowStart = DateTime.Parse(reader.GetString(0)).ToUniversalTime();
                        mediaCount = reader.GetInt32(1);
                        hasWarned = reader.GetInt32(2);
                    }
                }

                bool isExpired = !recordExists || (now - windowStart).TotalSeconds >= windowSeconds;

                if (isExpired)
                {
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
                    mediaCount++;
                    bool shouldWarn = false;
                    bool isAllowed = true;

                    if (mediaCount > maxMediaAllowed)
                    {
                        isAllowed = false;
                        if (hasWarned == 0)
                        {
                            shouldWarn = true;
                            hasWarned = 1;
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
                return new ModerationResult { IsAllowed = true, ShouldWarn = false };
            }
            finally
            {
                _dbLock.Release();
            }
        }

        public async Task DecrementMediaCountAsync(long userId, long chatId, int countToSubtract)
        {
            await _dbLock.WaitAsync();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

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

        public async Task AddPendingDeletionAsync(long chatId, int messageId, DateTime deleteAt)
        {
            await _dbLock.WaitAsync();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                string insertSql = @"
                    INSERT OR REPLACE INTO PendingDeletions (ChatId, MessageId, DeleteAt)
                    VALUES (@ChatId, @MessageId, @DeleteAt)";

                using var command = new SqliteCommand(insertSql, connection);
                command.Parameters.AddWithValue("@ChatId", chatId);
                command.Parameters.AddWithValue("@MessageId", messageId);
                command.Parameters.AddWithValue("@DeleteAt", deleteAt.ToString("o"));

                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log pending deletion for message {MessageId} in chat {ChatId}", messageId, chatId);
            }
            finally
            {
                _dbLock.Release();
            }
        }

        public async Task<List<PendingDeletion>> GetDueDeletionsAsync()
        {
            await _dbLock.WaitAsync();
            var list = new List<PendingDeletion>();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                string selectSql = "SELECT ChatId, MessageId FROM PendingDeletions WHERE DeleteAt <= @Now";
                using var command = new SqliteCommand(selectSql, connection);
                command.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("o"));

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    list.Add(new PendingDeletion
                    {
                        ChatId = reader.GetInt64(0),
                        MessageId = reader.GetInt32(1)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve due deletions from database.");
            }
            finally
            {
                _dbLock.Release();
            }
            return list;
        }

        public async Task RemovePendingDeletionAsync(long chatId, int messageId)
        {
            await _dbLock.WaitAsync();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                string deleteSql = "DELETE FROM PendingDeletions WHERE ChatId = @ChatId AND MessageId = @MessageId";
                using var command = new SqliteCommand(deleteSql, connection);
                command.Parameters.AddWithValue("@ChatId", chatId);
                command.Parameters.AddWithValue("@MessageId", messageId);

                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear pending deletion for message {MessageId} in chat {ChatId}", messageId, chatId);
            }
            finally
            {
                _dbLock.Release();
            }
        }
    }
}