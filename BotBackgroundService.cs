using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace KuryerBakuBot
{
    public class BotBackgroundService : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly DatabaseService _databaseService;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<BotBackgroundService> _logger;
        
        private readonly DateTime _startupTime = DateTime.UtcNow;
        private readonly ConcurrentDictionary<long, SemaphoreSlim> _userLocks = new();

        public BotBackgroundService(
            IConfiguration configuration,
            DatabaseService databaseService,
            IMemoryCache memoryCache,
            ILogger<BotBackgroundService> logger)
        {
            _configuration = configuration;
            _databaseService = databaseService;
            _memoryCache = memoryCache;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            string? botToken = _configuration["BotSettings:BotToken"];
            if (string.IsNullOrEmpty(botToken) || botToken == "YOUR_TELEGRAM_BOT_TOKEN_HERE")
            {
                _logger.LogCritical("BotToken is missing or not configured in appsettings.json! The bot cannot start.");
                return;
            }

            var bot = new TelegramBotClient(botToken, cancellationToken: stoppingToken);
            
            try
            {
                User me = await bot.GetMe(stoppingToken);
                _logger.LogInformation("Kuryer Baku Moderation Bot initialized successfully. Running as @{Username}", me.Username);
                _logger.LogInformation("Startup Time recorded: {StartupTime} UTC.", _startupTime.ToString("o"));
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to connect to the Telegram API. Verify your BotToken.");
                return;
            }

            // Start the non-blocking background warning deletion task
            _ = StartWarningDeletionPollerAsync(bot, stoppingToken);

            bot.OnMessage += OnMessageReceived;
            bot.OnError += OnErrorOccurred;

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("Bot background service cancellation requested.");
            }
            finally
            {
                bot.OnMessage -= OnMessageReceived;
                bot.OnError -= OnErrorOccurred;
                _logger.LogInformation("Bot background service has stopped cleanly.");
            }
        }

        // Parallel non-blocking worker that processes scheduled deletions every 10 seconds
        private async Task StartWarningDeletionPollerAsync(TelegramBotClient bot, CancellationToken stoppingToken)
        {
            _logger.LogInformation("Background warning deletion poller started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Check every 10 seconds
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

                    List<PendingDeletion> dueDeletions = await _databaseService.GetDueDeletionsAsync();
                    foreach (var item in dueDeletions)
                    {
                        _logger.LogInformation("Automatically deleting expired warning message {MessageId} in chat {ChatId}", item.MessageId, item.ChatId);
                        
                        // Delete the message. Errors (e.g. message already deleted manually) are caught inside this helper safely
                        await SafeDeleteMessageAsync(bot, item.ChatId, item.MessageId);

                        // Always remove from the database to prevent re-attempts
                        await _databaseService.RemovePendingDeletionAsync(item.ChatId, item.MessageId);
                    }
                }
                catch (TaskCanceledException)
                {
                    // Clean shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred inside the background warning deletion loop.");
                }
            }
        }

        // Reverted completely to your working Stage 5 moderation logic
        private async Task OnMessageReceived(Message msg, UpdateType type)
        {
            if (msg.Date.ToUniversalTime() < _startupTime)
            {
                return;
            }

            long targetGroupId = _configuration.GetValue<long>("BotSettings:TargetGroupId");
            if (msg.Chat.Id != targetGroupId)
            {
                return;
            }

            if (msg.From == null) return;
            
            var botToken = _configuration["BotSettings:BotToken"]!;
            var bot = new TelegramBotClient(botToken);

            bool isAdmin = await IsUserAdminAsync(bot, msg.Chat.Id, msg.From.Id);
            if (isAdmin)
            {
                return;
            }

            bool isMedia = msg.Photo is not null || msg.Video is not null;
            if (!isMedia)
            {
                return;
            }

            var userLock = _userLocks.GetOrAdd(msg.From.Id, _ => new SemaphoreSlim(1, 1));
            await userLock.WaitAsync();

            try
            {
                int windowSeconds = _configuration.GetValue<int>("ModerationSettings:WindowSeconds");
                int maxMediaAllowed = _configuration.GetValue<int>("ModerationSettings:MaxMediaAllowed");

                // ==========================================
                // CASE 1: MESSAGE IS PART OF AN ALBUM
                // ==========================================
                if (!string.IsNullOrEmpty(msg.MediaGroupId))
                {
                    string albumKey = $"Album_{msg.MediaGroupId}";

                    if (!_memoryCache.TryGetValue(albumKey, out AlbumState? albumState))
                    {
                        albumState = new AlbumState
                        {
                            MediaGroupId = msg.MediaGroupId,
                            UserId = msg.From.Id,
                            MessageIds = new List<int>()
                        };
                    }

                    if (albumState!.IsViolated)
                    {
                        await SafeDeleteMessageAsync(bot, msg.Chat.Id, msg.Id);
                        return;
                    }

                    albumState.MessageIds.Add(msg.Id);

                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetSlidingExpiration(TimeSpan.FromSeconds(10));
                    _memoryCache.Set(albumKey, albumState, cacheOptions);

                    var modResult = await _databaseService.ProcessMediaMessageAsync(msg.From.Id, msg.Chat.Id, windowSeconds, maxMediaAllowed);

                    if (albumState.MessageIds.Count >= 3)
                    {
                        albumState.IsViolated = true;
                        _memoryCache.Set(albumKey, albumState, cacheOptions);

                        foreach (int msgId in albumState.MessageIds)
                        {
                            await SafeDeleteMessageAsync(bot, msg.Chat.Id, msgId);
                        }

                        await SendWarningMessageAsync(bot, msg.Chat.Id, msg.From);

                        await _databaseService.DecrementMediaCountAsync(msg.From.Id, msg.Chat.Id, albumState.MessageIds.Count);
                        return;
                    }

                    if (!modResult.IsAllowed)
                    {
                        await SafeDeleteMessageAsync(bot, msg.Chat.Id, msg.Id);

                        if (modResult.ShouldWarn)
                        {
                            await SendWarningMessageAsync(bot, msg.Chat.Id, msg.From);
                        }
                    }
                }
                // ==========================================
                // CASE 2: MESSAGE IS AN INDIVIDUAL MEDIA FILE
                // ==========================================
                else
                {
                    var modResult = await _databaseService.ProcessMediaMessageAsync(msg.From.Id, msg.Chat.Id, windowSeconds, maxMediaAllowed);

                    if (!modResult.IsAllowed)
                    {
                        await SafeDeleteMessageAsync(bot, msg.Chat.Id, msg.Id);

                        if (modResult.ShouldWarn)
                        {
                            await SendWarningMessageAsync(bot, msg.Chat.Id, msg.From);
                        }
                    }
                }
            }
            finally
            {
                userLock.Release();
            }
        }

        private async Task OnErrorOccurred(Exception exception, HandleErrorSource source)
        {
            _logger.LogError(exception, "Telegram Error Occurred at Source: {Source}", source);
            await Task.CompletedTask;
        }

        private async Task<bool> IsUserAdminAsync(TelegramBotClient bot, long chatId, long userId)
        {
            string cacheKey = $"Admins_{chatId}";
            
            if (!_memoryCache.TryGetValue(cacheKey, out HashSet<long>? adminIds))
            {
                try
                {
                    ChatMember[] administrators = await bot.GetChatAdministrators(chatId);
                    adminIds = new HashSet<long>();
                    
                    foreach (var admin in administrators)
                    {
                        adminIds.Add(admin.User.Id);
                    }

                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(TimeSpan.FromMinutes(5));
                    
                    _memoryCache.Set(cacheKey, adminIds, cacheOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to retrieve chat administrators for {ChatId}", chatId);
                    return false;
                }
            }

            return adminIds?.Contains(userId) ?? false;
        }

        private async Task SafeDeleteMessageAsync(TelegramBotClient bot, long chatId, int messageId)
        {
            try
            {
                await bot.DeleteMessage(chatId, messageId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to delete message {MessageId} in chat {ChatId}: {Error}", messageId, chatId, ex.Message);
            }
        }

        private async Task SendWarningMessageAsync(TelegramBotClient bot, long chatId, User user)
        {
            string usernameFormatted = string.IsNullOrEmpty(user.Username)
                ? $"[{user.FirstName}](tg://user?id={user.Id})"
                : $"@{user.Username}";

            int maxMediaAllowed = _configuration.GetValue<int>("ModerationSettings:MaxMediaAllowed");

            string warningText = $"⚠️ *Hörmətli* {usernameFormatted}\n\n" +
                                 $"Qrup qaydalarına əsasən, maksimum *{maxMediaAllowed} media* (şəkil və ya video) göndərə bilərsiniz.\n\n" +
                                 $"Qaydanı pozan media faylları avtomatik silinir.";

            try
            {
                Message warningMsg = await bot.SendMessage(
                    chatId: chatId,
                    text: warningText,
                    parseMode: ParseMode.Markdown
                );

                // Read warning expiration duration from config (defaults to 300 seconds / 5 mins)
                int deleteAfterSeconds = _configuration.GetValue<int>("ModerationSettings:WarningDeleteAfterSeconds", 300);
                DateTime deleteAt = DateTime.UtcNow.AddSeconds(deleteAfterSeconds);

                // Save scheduled deletion to database (This is our ONLY addition)
                await _databaseService.AddPendingDeletionAsync(chatId, warningMsg.Id, deleteAt);
                _logger.LogInformation("Warning message {MessageId} scheduled to delete at {DeleteAt} UTC", warningMsg.Id, deleteAt.ToString("o"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send warning message to chat {ChatId}", chatId);
            }
        }
    }
}