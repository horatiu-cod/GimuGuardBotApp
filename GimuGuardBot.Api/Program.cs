using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using GimuGuardBot.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("Guard_");
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

    // --- STEP 1: Check if the join happened in the public channel ---

    bool isTargetChannel = update.Message?.Chat.Username?
    .Equals(config.PublicChannelUsername.Replace("@", ""), StringComparison.OrdinalIgnoreCase) == true;

    if (!isTargetChannel)
    {
        return Results.Ok();// Acknowledge other updates quickly
    }
    
    if (update.Message is { } message)
    {
        // we only care about MyChatMember updates ( when a user join a channel/group)
        if (update.Type != UpdateType.MyChatMember)
        {
            return Results.Ok(); // Acknowledge other updates quickly
        }
        // --- check if the status change indicates a new member joining ---
        var myChatMember = update.MyChatMember;
        var oldStatus = myChatMember?.OldChatMember.Status;
        var newStatus = myChatMember?.NewChatMember.Status;
        bool isBot = myChatMember?.NewChatMember.User.IsBot == true;
        // We check for Left -> Member or Kicked -> Member
        bool isValidMember = newStatus == ChatMemberStatus.Member && (oldStatus == ChatMemberStatus.Left || oldStatus == ChatMemberStatus.Kicked);
       bool isLeftMember = newStatus == ChatMemberStatus.Left && (oldStatus == ChatMemberStatus.Member);

        // 1. Handle New Member Join 
        if (isValidMember )
        {
            var newMember = myChatMember?.From;
            if (newMember == null) return Results.Ok();
            if (isBot) return Results.Ok(); // Ignore other bots
            if (newMember.IsBot) return Results.Ok(); // Ignore other bots
            await InitiateVerification(botClient, message.Chat.Id, newMember);
            if (isLeftMember)
            {
                await botClient.BanChatMember(message.Chat.Id, newMember.Id);
            }
            // If multiple members joined, handle each (not typical in channels)
            //foreach (var newMember in message.NewChatMembers)
            //{
            //    if (isBot) continue; // Ignore other bots
            //    if (newMember.IsBot) continue; // Ignore other bots
            //    await InitiateVerification(botClient, message.Chat.Id, newMember);
            //}
        }
    }
    else if (update.CallbackQuery is { } callbackQuery)
    {
        // 2. Handle CAPTCHA Button Click
        if (callbackQuery.Data != null && callbackQuery.Data.StartsWith("captcha_"))
        {
            await HandleCaptchaAnswer(botClient, callbackQuery, config.PrivateGroupId, activeChallenges);
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
app.MapGet("/bot/delete", async (ITelegramBotClient botclient) =>{
    await botclient.DeleteWebhook();
    return Results.Ok("Webhook deleted");
});

//app.UseTelegramBotWebhook();
await app.RunAsync();

// --- Helper methods ---

async Task InitiateVerification(ITelegramBotClient botClient, long chatId, User newMember)
{
    logger.LogInformation("New  user {UserId} {Username} joined. Initiating CAPTCHA.", newMember.Id, newMember.Username);

    //Immediately restrict the user: they can see messages, can't talk.
    var restrictedPermissions = new ChatPermissions{ CanSendMessages = false};
    await botClient.RestrictChatMember(
        chatId: chatId, 
        userId: newMember.Id, 
        permissions: restrictedPermissions,
    // Set a time limit for restriction
        untilDate: DateTime.UtcNow.AddMinutes(5)
        );

    // Create a simple math challenge
    var rnd = new Random();
    var num1 = rnd.Next(1, 10);
    var num2 = rnd.Next(1, 10);
    var correctAnswer = num1 + num2;

    // Generate 3 unique answer options, including the correct one
    var options = new List<int> { correctAnswer };
    while (options.Count < 3)
    {
        var randomAnswer = rnd.Next(2, 20);
        if (!options.Contains(randomAnswer))
        {
            options.Add(randomAnswer);
        }
    }
    options.Shuffle(); // Extension method needed for real use, but here's the concept

    // Store the correct answer for later verification
    activeChallenges[newMember.Id] = correctAnswer;

    // Build the inline keyboard
    var keyboardButtons = options.Select(answer =>
        InlineKeyboardButton.WithCallbackData(
            text: answer.ToString(),
            // Store the expected answer and the user ID in the callback data
            callbackData: $"captcha_{newMember.Id}_{answer}")
    ).ToArray();

    var inlineKeyboard = new InlineKeyboardMarkup(keyboardButtons.Chunk(3)); // Arrange into rows

    var welcomeMessage = $"🤖 **Verification Required** 🤖\n" +
                         $"Welcome, {newMember.FirstName}! To prove you're not a bot, please answer the math question below within 5 minutes:\n\n" +
                         $"**What is {num1} + {num2}?**";

    await botClient.SendMessage(
        chatId: chatId,
        text: welcomeMessage,
        replyMarkup: inlineKeyboard,
        parseMode: ParseMode.Markdown
    );
}

async Task HandleCaptchaAnswer(ITelegramBotClient botClient, CallbackQuery callbackQuery, long privateGroupId, ConcurrentDictionary<long, int> challenges)
{
    var dataParts = callbackQuery.Data.Split('_');
    var targetUserId = long.Parse(dataParts[1]); // The user the challenge was intended for
    var selectedAnswer = int.Parse(dataParts[2]); // The answer the user clicked
    var clickerId = callbackQuery.From.Id; // The user who clicked the button
    var chatId = callbackQuery.Message!.Chat.Id;

    // 1. Check if the user clicking the button is the user who needs verification
    if (clickerId != targetUserId)
    {
        await botClient.AnswerCallbackQuery(callbackQuery.Id, "This button is not for you! 🤫");
        return;
    }

    // 2. Retrieve the correct answer from storage
    if (challenges.TryGetValue(targetUserId, out int correctAnswer))
    {
        // 3. Compare the answers
        if (selectedAnswer == correctAnswer)
        {
            // SUCCESS: Grant full access
            try
            {
                // --- STEP 3: Generate the ONE-TIME invite link for the Private Group ---
                // The Bot MUST be an admin in the PrivateGroupId with "Invite Users" permission.
                var inviteLink = await botClient.CreateChatInviteLink(
                    chatId: privateGroupId,
                    memberLimit: 1, // Crucial: Makes the link one-time use
                    expireDate: DateTime.UtcNow.AddMinutes(5),
                    createsJoinRequest: false
                );
                var inlineMessageText = $"✅ **{callbackQuery.From.FirstName}** successfully verified and now has access! An invite link have been send to you";
                await botClient.EditMessageText(
                    chatId: chatId,
                    messageId: callbackQuery.Message.MessageId,
                    text: inlineMessageText,
                    parseMode: ParseMode.Markdown
                );
                logger.LogInformation("Successfully verified the user {UserId}.", clickerId);

                // --- STEP 4: Send the link privately to the user ---
                var privateMessageText = $"Welcome to our community, {callbackQuery.From.FirstName}! Here is your **one-time** invite link to the private discussion group:\n\n{inviteLink.InviteLink}";

                await botClient.SendMessage(
                    chatId: clickerId, // Send to the user's private chat
                    text: privateMessageText,
                    parseMode: ParseMode.Markdown
                );
                // check if user lefts the  channel

                logger.LogInformation("Successfully sent one-time link to user {UserId}.", clickerId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing join event for user {UserId}. The user might have blocked the bot.", clickerId);
            }
            // --- STEP 5: Ban user to avoid spam ---
            await botClient.BanChatMember(chatId, clickerId);
        }
        else
        {
            // FAILURE: Kick the user
            await botClient.BanChatMember(chatId, clickerId);
            challenges.TryRemove(targetUserId, out _);

            await botClient.EditMessageText(
                chatId: chatId,
                messageId: callbackQuery.Message.MessageId,
                text: $"❌ **{callbackQuery.From.FirstName}** failed verification and has been removed.",
                parseMode: ParseMode.Markdown
            );
        }
    }
    // 4. Always acknowledge the callback query
    await botClient.AnswerCallbackQuery(callbackQuery.Id);
}

async Task UnrestrictMember(ITelegramBotClient botClient, long chatId, long userId)
{
    // Grant them default, full permissions (all true)
    var fullPermissions = new ChatPermissions
    {
        CanSendMessages = true,
        CanSendOtherMessages = true,
        CanSendPolls = true,
        CanAddWebPagePreviews = true,
        CanChangeInfo = false, // Usually false
        CanInviteUsers = true,
        CanPinMessages = false // Usually false
    };

    await botClient.RestrictChatMember(
        chatId: chatId,
        userId: userId,
        permissions: fullPermissions
    );
}

