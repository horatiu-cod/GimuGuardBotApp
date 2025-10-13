// See https://aka.ms/new-console-template for more information
using Telegram.Bot;

Console.WriteLine("Hello, World!");


var botClient = new TelegramBotClient("8475907726:AAGi7wK81pzumjE2osYPcCsVMbKlHN4qrn4");

var me = await botClient.GetMe();
Console.WriteLine($"Hello, World! I am bot {me.Id} and my name is {me.FirstName}.");