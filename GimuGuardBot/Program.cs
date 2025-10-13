// See https://aka.ms/new-console-template for more information
using Microsoft.Extensions.Configuration;
using System.Configuration;
using Telegram.Bot;
ConfigurationBuilder configurationBuilder = new ConfigurationBuilder();
IConfiguration configuration = configurationBuilder.AddUserSecrets<Program>().Build();
string botToken = configuration.GetSection("GuardBot")["Token"]; 
Console.WriteLine("Hello, World!");


var botClient = new TelegramBotClient(botToken);

var me = await botClient.GetMe();
Console.WriteLine($"Hello, World! I am bot {me.Id} and my name is {me.FirstName}.");