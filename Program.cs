using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Npgsql;
using System.Collections.Concurrent;
using System.Globalization;

class Program
{
    // Конфигурация
    static string AdminPassword = "Igor123";
    static string? BotToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
    static string? DatabaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

    // Основные объекты
    static TelegramBotClient? BotClient;

    // Храним сессии пользователей (статусы, что ждём)
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

        // Создаём клиента и проверяем
        BotClient = new TelegramBotClient(BotToken);
        var me = await BotClient.GetMeAsync();
        Console.WriteLine($"✅ Бот @{me.Username} запущен.");

        // Инициализируем БД
        InitDb();

        // Запускаем Polling
        var cts = new CancellationTokenSource();
        var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };
        BotClient.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            receiverOptions,
            cancellationToken: cts.Token
        );

        Console.WriteLine("✅ StartReceiving запущен (polling). Ожидаю сообщения...");
        await Task.Delay(-1);
    }

    // Инициализация БД
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

    // Парсер строки подключения
    static string ConvertDatabaseUrlToConnectionString(string databaseUrl)
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':');
        return $"Host={uri.Host};Port={uri.Port};Username={userInfo[0]};Password={userInfo[1]};Database={uri.AbsolutePath.TrimStart('/')};SSL Mode=Require;Trust Server Certificate=true;";
    }

    // Главный обработчик апдейтов
    static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is { } message)
        {
            // Лог
            Console.WriteLine($"[Message] from {message.Chat.Id}: {message.Text}");

            long userId = message.Chat.Id;
            if (!Sessions.ContainsKey(userId))
                Sessions[userId] = new UserSession();
            var session = Sessions[userId];

            // Текст команды
            var text = message.Text!.Trim();

            // Проверяем команды
            if (text == "/start")
            {
                await botClient.SendTextMessageAsync(
                    chatId: userId,
                    text: "Привет! Выбери действие:",
                    replyMarkup: GetMainMenu(),
                    cancellationToken: cancellationToken
                );
                return;
            }

            // Если ждём пароль
            if (session.AwaitingPassword)
            {
                session.AwaitingPassword = false;
                if (text == AdminPassword)
                {
                    await botClient.SendTextMessageAsync(
                        userId,
                        "✅ Пароль верный!",
                        replyMarkup: GetAdminMenu(),
                        cancellationToken: cancellationToken
                    );
                }
                else
                {
                    await botClient.SendTextMessageAsync(
                        userId,
                        "❌ Неверный пароль!",
                        cancellationToken: cancellationToken
                    );
                }
                return;
            }

            // Если ждём ввод нового кофе
            if (session.AwaitingNewCoffee)
            {
                session.AwaitingNewCoffee = false;
                if (!text.Contains("-"))
                {
                    await botClient.SendTextMessageAsync(
                        userId,
                        "❌ Неверный формат. Используйте: Название - Цена",
                        cancellationToken: cancellationToken
                    );
                    return;
                }

                var parts = text.Split('-', 2);
                var name = parts[0].Trim();
                if (!int.TryParse(parts[1].Trim(), out var price))
                {
                    await botClient.SendTextMessageAsync(
                        userId,
                        "❌ Цена должна быть числом!",
                        cancellationToken: cancellationToken
                    );
                    return;
                }

                var key = name.ToLower().Replace(" ", "_");

                using var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!));
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO menu (key, name, price)
                                    VALUES (@k, @n, @p)
                                    ON CONFLICT (key) DO UPDATE
                                    SET name=EXCLUDED.name, price=EXCLUDED.price;";
                cmd.Parameters.AddWithValue("k", key);
                cmd.Parameters.AddWithValue("n", name);
                cmd.Parameters.AddWithValue("p", price);
                cmd.ExecuteNonQuery();

                session.LastMenuItem = key;

                await botClient.SendTextMessageAsync(
                    userId,
                    $"✅ Напиток добавлен: {name} — {price} грн",
                    replyMarkup: GetAdminMenu(),
                    cancellationToken: cancellationToken
                );
                return;
            }
        }
        else if (update.CallbackQuery is { } query)
        {
            // Обработка кнопок
            await HandleCallbackQueryAsync(botClient, query, cancellationToken);
        }
    }

    // Обработка нажатий на кнопки
    static async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        long userId = query.Message!.Chat.Id;
        if (!Sessions.ContainsKey(userId))
            Sessions[userId] = new UserSession();
        var session = Sessions[userId];

        // Убираем "часики" с кнопки
        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        // Смотрим, какая кнопка
        switch (query.Data)
        {
            case "choose_coffee":
                // Покажем список кофе
                await botClient.SendTextMessageAsync(
                    userId,
                    "☕ Выберите напиток:",
                    replyMarkup: GetCoffeeMenu(),
                    cancellationToken: cancellationToken
                );
                break;

            case var data when data.StartsWith("order_"):
                // Пытаемся выделить ключ кофе
                var coffeeKey = data.Split("_", 2)[1];
                // Спросим способ оплаты
                session.SelectedCoffee = coffeeKey;
                await botClient.SendTextMessageAsync(
                    userId,
                    $"Вы выбрали: {GetMenu()[coffeeKey].Name} ( {GetMenu()[coffeeKey].Price} грн )\nВыберите способ оплаты:",
                    replyMarkup: GetPaymentMenu(),
                    cancellationToken: cancellationToken
                );
                break;

            case "stats":
                // Показываем статистику за сегодня
                string today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                int total = 0;
                string statText = $"📊 Статистика за {today}:\n";

                using (var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!)))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT coffee_key, payment, count, revenue FROM stats WHERE date=@d";
                    cmd.Parameters.AddWithValue("d", today);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var ck = reader.GetString(0);
                        var pm = reader.GetString(1);
                        var ct = reader.GetInt32(2);
                        var rev = reader.GetInt32(3);

                        var menu = GetMenu();
                        string coffeeName = menu.ContainsKey(ck) ? menu[ck].Name : ck;
                        string payName = pm == "cash" ? "Наличные" : "Карта";

                        statText += $"\n☕ {coffeeName} ({payName}) — {ct} шт. (Выручка: {rev} грн)";
                        total += rev;
                    }
                }

                statText += $"\n\n💰 Общая выручка: {total} грн";

                await botClient.SendTextMessageAsync(
                    userId,
                    statText,
                    replyMarkup: GetMainMenu(),
                    cancellationToken: cancellationToken
                );
                break;

            case "enter_password":
                session.AwaitingPassword = true;
                await botClient.SendTextMessageAsync(
                    userId,
                    "🔑 Введите пароль:",
                    cancellationToken: cancellationToken
                );
                break;

            case "add_coffee":
                session.AwaitingNewCoffee = true;
                await botClient.SendTextMessageAsync(
                    userId,
                    "Введите название и цену (Напиток - Цена)",
                    cancellationToken: cancellationToken
                );
                break;

            case "back_main":
                await botClient.SendTextMessageAsync(
                    userId,
                    "🔙 Возвращение в главное меню.",
                    replyMarkup: GetMainMenu(),
                    cancellationToken: cancellationToken
                );
                break;

            // Оплата
            case "pay_cash":
            case "pay_card":
                if (session.SelectedCoffee == null)
                {
                    await botClient.SendTextMessageAsync(
                        userId,
                        "⛔ Выберите кофе сначала!",
                        cancellationToken: cancellationToken
                    );
                    return;
                }
                var coffee = GetMenu()[session.SelectedCoffee];
                var payment = query.Data == "pay_cash" ? "cash" : "card";
                var price = coffee.Price;
                var dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                // Записываем в stats
                using (var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!)))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO stats (date, coffee_key, payment, count, revenue)
                        VALUES (@d, @k, @pm, 1, @r)
                        ON CONFLICT (date, coffee_key, payment) DO UPDATE
                        SET count = stats.count + 1,
                            revenue = stats.revenue + EXCLUDED.revenue;
                    ";
                    cmd.Parameters.AddWithValue("d", dateStr);
                    cmd.Parameters.AddWithValue("k", session.SelectedCoffee);
                    cmd.Parameters.AddWithValue("pm", payment);
                    cmd.Parameters.AddWithValue("r", price);
                    cmd.ExecuteNonQuery();
                }

                await botClient.SendTextMessageAsync(
                    userId,
                    $"☕ Заказ добавлен: {coffee.Name}, оплата: {(payment == "cash" ? "Наличные" : "Карта")}",
                    replyMarkup: GetMainMenu(),
                    cancellationToken: cancellationToken
                );
                session.SelectedCoffee = null;
                break;

            default:
                await botClient.SendTextMessageAsync(
                    userId,
                    "⚠️ Неизвестная команда.",
                    cancellationToken: cancellationToken
                );
                break;
        }
    }

    static Task HandleErrorAsync(ITelegramBotClient botClient, Exception ex, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Error] {ex.Message}");
        return Task.CompletedTask;
    }

    // Главное меню
    static InlineKeyboardMarkup GetMainMenu() =>
        new InlineKeyboardMarkup(
            new[] {
                new[] { InlineKeyboardButton.WithCallbackData("☕ Выбрать кофе", "choose_coffee") },
                new[] { InlineKeyboardButton.WithCallbackData("📊 Статистика (сегодня)", "stats") },
                new[] { InlineKeyboardButton.WithCallbackData("🔧 Ввести пароль (админ)", "enter_password") }
            }
        );

    // Админ-меню
    static InlineKeyboardMarkup GetAdminMenu() =>
        new InlineKeyboardMarkup(
            new[] {
                new[] { InlineKeyboardButton.WithCallbackData("➕ Добавить напиток", "add_coffee") },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "back_main") }
            }
        );

    // Меню выбора кофе (из БД)
    static InlineKeyboardMarkup GetCoffeeMenu()
    {
        var menu = GetMenu();
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var (key, (Name, Price)) in menu)
        {
            string btnText = $"{Name} ({Price} грн)";
            rows.Add(new[] {
                InlineKeyboardButton.WithCallbackData(btnText, $"order_{key}")
            });
        }
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "back_main") });
        return new InlineKeyboardMarkup(rows);
    }

    // Меню оплаты
    static InlineKeyboardMarkup GetPaymentMenu() =>
        new InlineKeyboardMarkup(
            new[] {
                new[] { InlineKeyboardButton.WithCallbackData("💵 Наличные", "pay_cash"), InlineKeyboardButton.WithCallbackData("💳 Карта", "pay_card") },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "choose_coffee") }
            }
        );

    // Читаем все позиции меню из БД
    static Dictionary<string, (string Name, int Price)> GetMenu()
    {
        var result = new Dictionary<string, (string, int)>();
        using var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, name, price FROM menu";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string k = reader.GetString(0);
            string n = reader.GetString(1);
            int p = reader.GetInt32(2);
            result[k] = (n, p);
        }
        return result;
    }
}

// Сессия пользователя: что мы от него ждём?
class UserSession
{
    public bool AwaitingPassword { get; set; } = false;
    public bool AwaitingNewCoffee { get; set; } = false;
    public string? SelectedCoffee { get; set; }
    public (string date, string coffeeKey, string payment, int price)? LastOrder { get; set; }
    public string? LastMenuItem { get; set; }
}
