using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Npgsql;

class Program
{
    static string AdminPassword = "Igor123";
    static string? BotToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
    static string? DbConnectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
    static TelegramBotClient? BotClient;
    static Dictionary<long, UserSession> Sessions = new();

    static async Task Main()
    {
        if (string.IsNullOrEmpty(BotToken) || string.IsNullOrEmpty(DbConnectionString))
        {
            Console.WriteLine("Переменные окружения BOT_TOKEN и DATABASE_URL не установлены.");
            return;
        }

        BotClient = new TelegramBotClient(BotToken);

        var me = await BotClient.GetMeAsync();
        Console.WriteLine($"Бот @{me.Username} запущен.");

        InitDb();

        using var cts = new CancellationTokenSource();
        BotClient.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            cancellationToken: cts.Token
        );

        Console.ReadLine();
    }

    static void InitDb()
    {
        using var conn = new NpgsqlConnection(DbConnectionString);
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

    static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is { } message && message.Text is not null)
            await HandleMessageAsync(message);
        else if (update.CallbackQuery is { } query)
            await HandleCallbackQueryAsync(query);
    }

    static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        Console.WriteLine(exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        });
        return Task.CompletedTask;
    }

    static async Task HandleMessageAsync(Message message)
    {
        long userId = message.Chat.Id;
        if (!Sessions.ContainsKey(userId))
            Sessions[userId] = new UserSession();

        var session = Sessions[userId];
        var text = message.Text!.Trim();

        if (text == "/start")
        {
            await BotClient!.SendTextMessageAsync(userId, "Привет! Выбери действие:", replyMarkup: GetMainMenu());
            return;
        }

        if (session.AwaitingPassword)
        {
            session.AwaitingPassword = false;
            if (text == AdminPassword)
            {
                await BotClient!.SendTextMessageAsync(userId, "✅ Пароль верный!", replyMarkup: GetAdminMenu());
            }
            else
            {
                await BotClient!.SendTextMessageAsync(userId, "❌ Неверный пароль!");
            }
            return;
        }

        if (session.AwaitingNewCoffee)
        {
            session.AwaitingNewCoffee = false;
            if (!text.Contains("-"))
            {
                await BotClient!.SendTextMessageAsync(userId, "❌ Неверный формат. Используйте: Название - Цена");
                return;
            }

            var parts = text.Split("-", 2);
            var name = parts[0].Trim();
            if (!int.TryParse(parts[1].Trim(), out var price))
            {
                await BotClient!.SendTextMessageAsync(userId, "❌ Цена должна быть числом!");
                return;
            }

            var key = name.ToLower().Replace(" ", "_");

            using var conn = new NpgsqlConnection(DbConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO menu (key, name, price) VALUES (@k, @n, @p) ON CONFLICT (key) DO UPDATE SET name=EXCLUDED.name, price=EXCLUDED.price;";
            cmd.Parameters.AddWithValue("k", key);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("p", price);
            cmd.ExecuteNonQuery();

            session.LastMenuItem = key;
            await BotClient!.SendTextMessageAsync(userId, $"✅ Напиток добавлен: {name} — {price} грн", replyMarkup: GetAdminMenu());
            return;
        }
    }

    static async Task HandleCallbackQueryAsync(CallbackQuery query)
    {
        long userId = query.Message!.Chat.Id;
        if (!Sessions.ContainsKey(userId))
            Sessions[userId] = new UserSession();

        var session = Sessions[userId];
        var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        var menu = GetMenu();

        switch (query.Data)
        {
            case "choose_coffee":
                await BotClient!.EditMessageTextAsync(userId, query.Message.MessageId, "Выберите напиток:", replyMarkup: GetCoffeeMenu());
                break;

            case var data when data.StartsWith("order_"):
                var coffeeKey = data.Split("_", 2)[1];
                if (menu.ContainsKey(coffeeKey))
                {
                    session.SelectedCoffee = coffeeKey;
                    await BotClient!.EditMessageTextAsync(userId, query.Message.MessageId, $"Вы выбрали: {menu[coffeeKey].Name}\nТеперь выберите способ оплаты:", replyMarkup: GetPaymentMenu());
                }
                break;

            case "pay_cash":
            case "pay_card":
                if (session.SelectedCoffee is null)
                {
                    await BotClient!.SendTextMessageAsync(userId, "⛔ Сначала выберите кофе!");
                    return;
                }
                var payment = query.Data == "pay_cash" ? "cash" : "card";
                var price = menu[session.SelectedCoffee].Price;
                using (var conn = new NpgsqlConnection(DbConnectionString))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO stats (date, coffee_key, payment, count, revenue)
                        VALUES (@d, @c, @p, 1, @r)
                        ON CONFLICT (date, coffee_key, payment) DO UPDATE
                        SET count = stats.count + 1,
                            revenue = stats.revenue + EXCLUDED.revenue;
                    ";
                    cmd.Parameters.AddWithValue("d", today);
                    cmd.Parameters.AddWithValue("c", session.SelectedCoffee);
                    cmd.Parameters.AddWithValue("p", payment);
                    cmd.Parameters.AddWithValue("r", price);
                    cmd.ExecuteNonQuery();
                }
                session.LastOrder = (today, session.SelectedCoffee, payment, price);
                await BotClient!.EditMessageTextAsync(userId, query.Message.MessageId, "✅ Заказ принят!", replyMarkup: GetMainMenu());
                session.SelectedCoffee = null;
                break;

            case "enter_password":
                session.AwaitingPassword = true;
                await BotClient!.SendTextMessageAsync(userId, "Введите пароль:");
                break;

            case "stats":
                string statText = "📊 Статистика за сегодня:\n";
                using (var conn = new NpgsqlConnection(DbConnectionString))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT coffee_key, payment, count, revenue FROM stats WHERE date=@d;";
                    cmd.Parameters.AddWithValue("d", today);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var coffee = menu.ContainsKey(reader.GetString(0)) ? menu[reader.GetString(0)].Name : reader.GetString(0);
                        var pay = reader.GetString(1) == "cash" ? "Наличные" : "Карта";
                        statText += $"☕ {coffee} ({pay}) — {reader.GetInt32(2)} шт. ({reader.GetInt32(3)} грн)\n";
                    }
                }
                await BotClient!.EditMessageTextAsync(userId, query.Message.MessageId, statText, replyMarkup: GetMainMenu());
                break;

            case "back_main":
                await BotClient!.EditMessageTextAsync(userId, query.Message.MessageId, "Выбери действие:", replyMarkup: GetMainMenu());
                break;
        }

        await BotClient!.AnswerCallbackQueryAsync(query.Id);
    }

    static InlineKeyboardMarkup GetMainMenu() =>
        new InlineKeyboardMarkup(new[]
        {
            new [] { InlineKeyboardButton.WithCallbackData("☕ Выбрать кофе", "choose_coffee") },
            new [] { InlineKeyboardButton.WithCallbackData("📊 Статистика (сегодня)", "stats") },
            new [] { InlineKeyboardButton.WithCallbackData("🔧 Ввести пароль (админ)", "enter_password") },
        });

    static InlineKeyboardMarkup GetAdminMenu() =>
        new InlineKeyboardMarkup(new[]
        {
            new [] { InlineKeyboardButton.WithCallbackData("➕ Добавить напиток", "add_coffee") },
            new [] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "back_main") },
        });

    static InlineKeyboardMarkup GetCoffeeMenu()
    {
        var menu = GetMenu();
        var buttons = new List<InlineKeyboardButton[]>();
        foreach (var (key, item) in menu)
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData($"☕ {item.Name} — {item.Price} грн", $"order_{key}") });

        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "back_main") });
        return new InlineKeyboardMarkup(buttons);
    }

    static InlineKeyboardMarkup GetPaymentMenu() =>
        new InlineKeyboardMarkup(new[]
        {
            new [] { InlineKeyboardButton.WithCallbackData("💵 Наличные", "pay_cash") },
            new [] { InlineKeyboardButton.WithCallbackData("💳 Карта", "pay_card") },
            new [] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "choose_coffee") },
        });

    static Dictionary<string, (string Name, int Price)> GetMenu()
    {
        var result = new Dictionary<string, (string, int)>();
        using var conn = new NpgsqlConnection(DbConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, name, price FROM menu;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = (reader.GetString(1), reader.GetInt32(2));
        return result;
    }
}

class UserSession
{
    public bool AwaitingPassword { get; set; } = false;
    public bool AwaitingNewCoffee { get; set; } = false;
    public string? SelectedCoffee { get; set; }
    public (string date, string coffeeKey, string payment, int price)? LastOrder { get; set; }
    public string? LastMenuItem { get; set; }
}
