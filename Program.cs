using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

class Program
{
    static async Task Main()
    {
        string? botToken = Environment.GetEnvironmentVariable("BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(botToken))
        {
            Console.WriteLine("⛔ BOT_TOKEN не найден!");
            return;
        }

        var botClient = new TelegramBotClient(botToken);
        var me = await botClient.GetMeAsync();
        Console.WriteLine($"✅ Бот @{me.Username} запущен.");

        using var cts = new CancellationTokenSource();

        botClient.StartReceiving(
            updateHandler: async (client, update, cancellationToken) =>
            {
                if (update.Message is { } message)
                {
                    Console.WriteLine($"Получено сообщение: {message.Text}");
                    await client.SendTextMessageAsync(message.Chat.Id, "Я жив!");
                }
            },
            pollingErrorHandler: (client, exception, cancellationToken) =>
            {
                Console.WriteLine($"Ошибка получения сообщений: {exception}");
                return Task.CompletedTask;
            },
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            },
            cancellationToken: cts.Token
        );

        Console.ReadLine();
    }
}
