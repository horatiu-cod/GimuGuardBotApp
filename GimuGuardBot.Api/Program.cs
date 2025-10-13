using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using GimuGuardBot.Api;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.Configure<BotConfiguration>(builder.Configuration.GetSection(BotConfiguration.ConfigurationSectionName));
builder.Services.AddHttpClient("telegram_bot_client")
    .AddTypedClient<ITelegramBotClient>((httpClient, sp) =>
    {
        var config = sp.GetRequiredService<IOptions<BotConfiguration>>().Value;
        return new TelegramBotClient(new TelegramBotClientOptions(config.BotToken), httpClient);
    });

// Use a thread-safe dictionary to store active CAPTCHA challenges
// Key: UserId, Value: Correct Answer
var activeChallenges = new ConcurrentDictionary<long, int>();

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

// --- 3. Webhook Endpoint ---
app.MapPost("/webhook", async (Update update, ITelegramBotClient botClient, IOptions<BotConfiguration> options) =>
{
    var config = options.Value;

    if (update.Message is { } message)
    {
        // 1. Handle New Member Join
        if (message.Type == MessageType.NewChatMembers && message.Chat.Id == config.PrivateGroupId)
        {
            foreach (var newMember in message.NewChatMembers)
            {
                if (newMember.IsBot) continue; // Ignore other bots

                await InitiateVerification(botClient, message.Chat.Id, newMember);
            }
        }
    }
    else if (update.CallbackQuery is { } callbackQuery)
    {
        // 2. Handle CAPTCHA Button Click
        if (callbackQuery.Data != null && callbackQuery.Data.StartsWith("captcha_"))
        {
            await HandleCaptchaAnswer(botClient, callbackQuery, activeChallenges);
        }
    }

    return Results.Ok();
});

// --- 4. Webhook Registration (Keep this for deployment) ---
app.MapGet("/setwebhook", async (ITelegramBotClient botClient, IOptions<BotConfiguration> options, IConfiguration configuration) =>
{
    var config = options.Value;
    var externalUrl = configuration["ASPNETCORE_URLS"]?.Split(';').FirstOrDefault(url => url.StartsWith("https"))
                      ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';').FirstOrDefault(url => url.StartsWith("https"));

    if (string.IsNullOrEmpty(externalUrl))
    {
        return Results.Problem("Could not determine the public URL for webhook registration.");
    }

    var webhookUrl = $"{externalUrl}/webhook";

    // Listen for Message (for new members) and CallbackQuery (for button clicks)
    await botClient.SetWebhookAsync(
        url: webhookUrl,
        allowedUpdates: new[] { UpdateType.Message, UpdateType.CallbackQuery }
    );

    return Results.Ok($"Webhook successfully set to: {webhookUrl}");
});

app.Run();