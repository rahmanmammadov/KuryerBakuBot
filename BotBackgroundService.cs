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
        
        // We record the exact UTC time when this class is loaded
        private readonly DateTime _startupTime = DateTime.UtcNow;

        // Thread locks mapped per UserId to prevent database race conditions (e.g. albums)
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

            // Initialize TelegramBotClient with the cancellation token
            var bot = new TelegramBotClient(botToken, cancellationToken: stoppingToken);
            
            try
            {
                User me = await bot.GetMe(stoppingToken);
                _logger.LogInformation("Kuryer Baku Moderation Bot initialized successfully. Running as @{Username}", me.Username);
                _logger.LogInformation("Startup Time recorded: {StartupTime} UTC. Ignorning all messages sent before this time.", _startupTime.ToString("o"));
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to connect to the Telegram API. Verify your BotToken.");
                return;
            }

            // Register events in Telegram.Bot v22
            bot.OnMessage += OnMessageReceived;
            bot.OnError += OnErrorOccurred;

            // Keep the background task running indefinitely until the application stops
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
                // Unsubscribe from events on shutdown
                bot.OnMessage -= OnMessageReceived;
                bot.OnError -= OnErrorOccurred;
                _logger.LogInformation("Bot background service has stopped cleanly.");
            }
        }

        private async Task OnMessageReceived(Message msg, UpdateType type)
        {
            // 1. FILTER: Ignore historical messages sent before the bot was turned on
            // This prevents warning spams on startup
            if (msg.Date.ToUniversalTime() < _startupTime)
            {
                _logger.LogDebug("Skipped historical message {MessageId} sent at {MsgDate} (Startup was at {StartupTime})", 
                    msg.Id, msg.Date.ToUniversalTime().ToString("o"), _startupTime.ToString("o"));
                return;
            }

            // 2. FILTER: Filter out everything except messages in our target group
            long targetGroupId = _configuration.GetValue<long>("BotSettings:TargetGroupId");
            if (msg.Chat.Id != targetGroupId)
            {
                return;
            }

            // 3. FILTER: Ignore messages sent by group managers/admins
            if (msg.From == null) return;
            
            var botToken = _configuration["BotSettings:BotToken"]!;
            var bot = new TelegramBotClient(botToken);

            bool isAdmin = await IsUserAdminAsync(bot, msg.Chat.Id, msg.From.Id);
            if (isAdmin)
            {
                _logger.LogDebug("User {UserId} is an Admin. Skipping moderation.", msg.From.Id);
                return;
            }

            // 4. Inspect if the message contains media (Photo or Video)
            bool isMedia = msg.Photo is not null || msg.Video is not null;
            if (!isMedia)
            {
                return;
            }

            // 5. Acquire thread lock for this specific user to handle parallel inputs sequentially
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

                    // Get or create album state in RAM cache
                    if (!_memoryCache.TryGetValue(albumKey, out AlbumState? albumState))
                    {
                        albumState = new AlbumState
                        {
                            MediaGroupId = msg.MediaGroupId,
                            UserId = msg.From.Id,
                            MessageIds = new List<int>()
                        };
                    }

                    // If this album has already triggered a violation, silently delete trailing files
                    if (albumState!.IsViolated)
                    {
                        _logger.LogInformation("Silently deleting late-arriving media message {MessageId} for already violated album {GroupId}", msg.Id, msg.MediaGroupId);
                        await SafeDeleteMessageAsync(bot, msg.Chat.Id, msg.Id);
                        return;
                    }

                    // Add this message to our album history list
                    albumState.MessageIds.Add(msg.Id);

                    // Update memory cache with 10-second sliding expiration
                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetSlidingExpiration(TimeSpan.FromSeconds(10));
                    _memoryCache.Set(albumKey, albumState, cacheOptions);

                    // Process this message in the rolling window database
                    var modResult = await _databaseService.ProcessMediaMessageAsync(msg.From.Id, msg.Chat.Id, windowSeconds, maxMediaAllowed);

                    // Rule 1: Album Limit (3 or more items in a single album)
                    if (albumState.MessageIds.Count >= 3)
                    {
                        _logger.LogWarning("Album Limit Rule Violated! User {UserId} uploaded album {GroupId} with {Count} media files.", msg.From.Id, msg.MediaGroupId, albumState.MessageIds.Count);

                        albumState.IsViolated = true;
                        _memoryCache.Set(albumKey, albumState, cacheOptions);

                        // Delete the entire album history
                        foreach (int msgId in albumState.MessageIds)
                        {
                            await SafeDeleteMessageAsync(bot, msg.Chat.Id, msgId);
                        }

                        // Send only one warning
                        await SendWarningMessageAsync(bot, msg.Chat.Id, msg.From);

                        // Rollback: Since the entire album was deleted, do not count it toward the user's active media window
                        await _databaseService.DecrementMediaCountAsync(msg.From.Id, msg.Chat.Id, albumState.MessageIds.Count);
                        return;
                    }

                    // Rule 2: Active Window violation inside a non-violating album (e.g. album of 2, but window count exceeds limit)
                    if (!modResult.IsAllowed)
                    {
                        _logger.LogWarning("Media limit window violation inside album. Deleting message {MessageId}", msg.Id);
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
                        _logger.LogWarning("Window Limit Rule Violated! Deleting message {MessageId} for User {UserId}", msg.Id, msg.From.Id);
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

        // Checks if a user is an administrator (Uses IMemoryCache to avoid hitting Telegram rate limits)
        private async Task<bool> IsUserAdminAsync(TelegramBotClient bot, long chatId, long userId)
        {
            string cacheKey = $"Admins_{chatId}";
            
            if (!_memoryCache.TryGetValue(cacheKey, out HashSet<long>? adminIds))
            {
                _logger.LogInformation("Admin list cache missed. Querying Telegram API for chat {ChatId}...", chatId);
                try
                {
                    ChatMember[] administrators = await bot.GetChatAdministrators(chatId);
                    adminIds = new HashSet<long>();
                    
                    foreach (var admin in administrators)
                    {
                        adminIds.Add(admin.User.Id);
                    }

                    // Cache the admin list for 5 minutes
                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(TimeSpan.FromMinutes(5));
                    
                    _memoryCache.Set(cacheKey, adminIds, cacheOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to retrieve chat administrators for {ChatId}", chatId);
                    // Default to false if API fails to prevent blocking administrators
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
            // Formatting username safely. If the user doesn't have a @username, we construct a markdown link to their profile
            string usernameFormatted = string.IsNullOrEmpty(user.Username)
                ? $"[{user.FirstName}](tg://user?id={user.Id})"
                : $"@{user.Username}";

            int windowSeconds = _configuration.GetValue<int>("ModerationSettings:WindowSeconds");
            int maxMediaAllowed = _configuration.GetValue<int>("ModerationSettings:MaxMediaAllowed");

            // Azerbaijani Warning Message
            string warningText = $"⚠️ *Hörmətli* {usernameFormatted}\n\n" +
                                 $"Qrup qaydalarına əsasən, *{windowSeconds} saniyə* ərzində maksimum *{maxMediaAllowed} media* (şəkil və ya video) göndərə bilərsiniz.\n\n" +
                                 $"Qaydanı pozan media faylları avtomatik silinir.";

            try
            {
                await bot.SendMessage(
                    chatId: chatId,
                    text: warningText,
                    parseMode: ParseMode.Markdown
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send warning message to chat {ChatId}", chatId);
            }
        }
    }
}