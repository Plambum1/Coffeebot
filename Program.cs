using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Npgsql;
using System.Collections.Concurrent;

class Program
{
    static string AdminPassword = "Igor123";
    static string? BotToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
    static string? DatabaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    static TelegramBotClient? BotClient;
    static ConcurrentDictionary<long, UserSession> Sessions = new();

    static async Task Main()
    {
        if (string.IsNullOrWhiteSpace(BotToken))
        {
            Console.WriteLine("⛔ BOT_TOKEN не найден!");
            return;
        }

        if (string.IsNullOrWhiteSpace(DatabaseUrl))
        {
            Console.WriteLine("⛔ DATABASE_URL не найден!");
            return;
        }

        BotClient = new TelegramBotClient(BotToken);

        var me = await BotClient.GetMeAsync();
        Console.WriteLine($"✅ Бот @{me.Username} запущен.");

        InitDb();

        var cts = new CancellationTokenSource();

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        Console.WriteLine("⏳ Запуск StartReceiving...");

        BotClient.StartReceiving(
            HandleUpdateAsync,
            HandlePollingErrorAsync,
            receiverOptions,
            cancellationToken: cts.Token
        );

        Console.WriteLine("✅ StartReceiving запущен.");

        await Task.Delay(-1);
    }

    static void InitDb()
    {
        using var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS menu (
                key TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                price INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS stats (
                date TEXT NOT NULL,
                coffee_key TEXT NOT NULL,
                payment TEXT NOT NULL,
                count INTEGER DEFAULT 0,
                revenue INTEGER DEFAULT 0,
                PRIMARY KEY (date, coffee_key, payment)
            );
        ";
        cmd.ExecuteNonQuery();
    }

    static string ConvertDatabaseUrlToConnectionString(string databaseUrl)
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':');
        return $"Host={uri.Host};Port={uri.Port};Username={userInfo[0]};Password={userInfo[1]};Database={uri.AbsolutePath.TrimStart('/')};SSL Mode=Require;Trust Server Certificate=true;";
    }

    static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is { } message)
        {
            Console.WriteLine($"📩 Получено сообщение: {message.Text} от {message.Chat.Id}");

            long userId = message.Chat.Id;
            if (!Sessions.ContainsKey(userId))
                Sessions[userId] = new UserSession();

            var session = Sessions[userId];
            var text = message.Text!.Trim();

            if (text == "/start")
            {
                await BotClient!.SendTextMessageAsync(userId, "Привет! Выбери действие:", replyMarkup: GetMainMenu(), cancellationToken: cancellationToken);
                return;
            }

            if (session.AwaitingPassword)
            {
                session.AwaitingPassword = false;
                if (text == AdminPassword)
                {
                    await BotClient!.SendTextMessageAsync(userId, "✅ Пароль верный!", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
                }
                else
                {
                    await BotClient!.SendTextMessageAsync(userId, "❌ Неверный пароль!", cancellationToken: cancellationToken);
                }
                return;
            }

            if (session.AwaitingNewCoffee)
            {
                session.AwaitingNewCoffee = false;
                if (!text.Contains("-"))
                {
                    await BotClient!.SendTextMessageAsync(userId, "❌ Неверный формат. Используйте: Название - Цена", cancellationToken: cancellationToken);
                    return;
                }

                var parts = text.Split("-", 2);
                var name = parts[0].Trim();
                if (!int.TryParse(parts[1].Trim(), out var price))
                {
                    await BotClient!.SendTextMessageAsync(userId, "❌ Цена должна быть числом!", cancellationToken: cancellationToken);
                    return;
                }

                var key = name.ToLower().Replace(" ", "_");

                using var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!));
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO menu (key, name, price) VALUES (@k, @n, @p) ON CONFLICT (key) DO UPDATE SET name=EXCLUDED.name, price=EXCLUDED.price;";
                cmd.Parameters.AddWithValue("k", key);
                cmd.Parameters.AddWithValue("n", name);
                cmd.Parameters.AddWithValue("p", price);
                cmd.ExecuteNonQuery();

                session.LastMenuItem = key;
                await BotClient!.SendTextMessageAsync(userId, $"✅ Напиток добавлен: {name} — {price} грн", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
                return;
            }
        }
        else if (update.CallbackQuery is { } query)
        {
            await HandleCallbackQueryAsync(query, cancellationToken);
        }
    }

    static async Task HandleCallbackQueryAsync(CallbackQuery query, CancellationToken cancellationToken)
    {
        long userId = query.Message!.Chat.Id;
        if (!Sessions.ContainsKey(userId))
            Sessions[userId] = new UserSession();

        var session = Sessions[userId];

        await BotClient!.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);
        await BotClient!.SendTextMessageAsync(userId, "✅ Callback обработан", cancellationToken: cancellationToken);
    }

    static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        Console.WriteLine($"❌ Ошибка Polling: {exception.Message}");
        return Task.CompletedTask;
    }

    static InlineKeyboardMarkup GetMainMenu() =>
        new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("☕ Выбрать кофе", "choose_coffee") },
            new[] { InlineKeyboardButton.WithCallbackData("📊 Статистика (сегодня)", "stats") },
            new[] { InlineKeyboardButton.WithCallbackData("🔧 Ввести пароль (админ)", "enter_password") },
        });

    static InlineKeyboardMarkup GetAdminMenu() =>
        new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("➕ Добавить напиток", "add_coffee") },
            new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "back_main") },
        });
}

class UserSession
{
    public bool AwaitingPassword { get; set; } = false;
    public bool AwaitingNewCoffee { get; set; } = false;
    public string? SelectedCoffee { get; set; }
    public (string date, string coffeeKey, string payment, int price)? LastOrder { get; set; }
    public string? LastMenuItem { get; set; }
}
