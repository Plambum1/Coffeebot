using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
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

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>() // получать все апдейты
        };

        botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: cts.Token
        );

        Console.ReadLine(); // чтобы приложение не завершилось
    }

    static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is { } message)
        {
            Console.WriteLine($"Получено сообщение: {message.Text}");
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Я жив!",
                cancellationToken: cancellationToken
            );
        }
    }

    static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Ошибка получения сообщений: {exception.Message}");
        return Task.CompletedTask;
    }
}
