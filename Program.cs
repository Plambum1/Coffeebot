using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Threading;

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

        var cts = new CancellationTokenSource();

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        Console.WriteLine("⏳ Запуск StartReceiving...");
        
        botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: cts.Token
        );

        Console.WriteLine("✅ StartReceiving запущен.");

        await Task.Delay(-1); // Чтобы приложение не завершилось
    }

    static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is { } message)
        {
            Console.WriteLine($"📩 Получено сообщение: {message.Text} от {message.Chat.Id}");

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Я жив!",
                cancellationToken: cancellationToken
            );
        }
    }

    static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        Console.WriteLine($"❌ Ошибка Polling: {exception.Message}");
        return Task.CompletedTask;
    }
}
