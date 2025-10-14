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
builder.Services.AddHttpClient("telegram_bot_client").RemoveAllLoggers()
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
app.MapPost("bot/webhook", async (Update update, ITelegramBotClient botClient, IOptions<BotConfiguration> options) =>
{
    var config = options.Value;
   
    
    var myChatMember = update.MyChatMember;

    if(myChatMember is null)
    {
        return Results.Ok(); 
    }
    // --- STEP 1: Check if the join happened in the public channel ---

    bool isTargetChannel = update.Message?.Chat.Username?
    .Equals(config.PublicChannelUsername.Replace("@", ""), StringComparison.OrdinalIgnoreCase) == true;

    if (!isTargetChannel)
    {
        return Results.Ok();// Acknowledge other updates quickly
    }

    // --- STEP 2: Check if the status change indicates a new member joining ---
     // we only care about MyChatMember updates ( when a user join a channel/group)
    if (update.Type != UpdateType.MyChatMember)
    {
        return Results.Ok(); // Acknowledge other updates quickly
    }
    var oldStatus = myChatMember?.OldChatMember.Status;
    var newStatus = myChatMember?.NewChatMember.Status;

     // We check for Left -> Member or Kicked -> Member
    if(newStatus == ChatMemberStatus.Member && (oldStatus == ChatMemberStatus.Left || oldStatus == ChatMemberStatus.Kicked))
    if (update.Message is { } message)
    {
        // 1. Handle New Member Join 
        if (message.NewChatMembers is not null && message.NewChatMembers.Length != 0 && message.Type == MessageType.NewChatMembers && message.Chat.Id == config.PrivateGroupId)
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
app.MapGet("bot/setwebhook", async (ITelegramBotClient botClient, IOptions<BotConfiguration> options, IConfiguration configuration) =>
{
    var config = options.Value;
    var externalUrl = string.Empty;
    if (!app.Environment.IsDevelopment())
    {
        externalUrl = configuration["ASPNETCORE_URLS"]?.Split(';').FirstOrDefault(url => url.StartsWith("https"))
                    ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';').FirstOrDefault(url => url.StartsWith("https"));
    }
    else
    {
        externalUrl = config.ExternalUrl;
    }
  

    if (string.IsNullOrEmpty(externalUrl))
    {
        return Results.Problem("Could not determine the public URL for webhook registration.");
    }

    var webhookUrl = $"{externalUrl}/bot/webhook";

    // Listen for Message (for new members) and CallbackQuery (for button clicks)
    await botClient.SetWebhook(
        url: webhookUrl,
        allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery]
    );

    return Results.Ok($"Webhook successfully set to: {webhookUrl}");
});

app.Run();

// --- Helper methods ---
async Task InitiateVerification(ITelegramBotClient botClient, long chatId, User newMember)
{
    logger.LogInformation("New  user {UserId} {Username} joined. Initiating CAPTCHA.", newMember.Id, newMember.Username);

    //Immediately restrict the user: they can see messages, can't talk.
    var restrictedPermissions = new ChatPermissions
    {
        CanSendMessages = false
    };
    await botClient.RestrictChatMember(chatId: chatId, userId: newMember.Id, permissions: restrictedPermissions);
    // Set a time limit for restriction
     
}

async Task HandleCaptchaAnswer(ITelegramBotClient botClient, CallbackQuery callbackQuery, ConcurrentDictionary<long, int> activeChallenges)
{
    throw new NotImplementedException();
}


