using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Timers; // <--- для KeepAlive
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Npgsql;

class Program
{
    // ==============================
    //  ПАРАМЕТРЫ
    // ==============================

    // Пароль для входа в админ-меню
    static string AdminPassword = "Igor123";

    // Читаем переменные окружения:
    static string? BotToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
    static string? DatabaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

    // ==============================
    //   ГЛОБАЛЬНЫЕ ОБЪЕКТЫ
    // ==============================

    // Telegram-клиент
    static TelegramBotClient? BotClient;

    // Храним «сессии» пользователей (что от них ждём)
    static ConcurrentDictionary<long, UserSession> Sessions = new();

    // Таймер KeepAlive
    static System.Timers.Timer? keepAliveTimer;

    // ==============================
    //   Main
    // ==============================
    static async Task Main()
    {
        // Проверяем наличие переменных окружения
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

        // Создаём Telegram-клиент
        BotClient = new TelegramBotClient(BotToken);

        // Пытаемся получить инфо о боте
        var me = await BotClient.GetMeAsync();
        Console.WriteLine($"✅ Бот @{me.Username} запущен.");

        // Инициализация базы
        InitDb();

        // Запускаем KeepAlive таймер
        StartKeepAlive();

        // Запуск Polling
        var cts = new CancellationTokenSource();
        var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };
        BotClient.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            receiverOptions,
            cancellationToken: cts.Token
        );

        Console.WriteLine("✅ StartReceiving запущен (polling). Ожидаю сообщения...");

        // Держим процесс вечно
        await Task.Delay(-1);
    }

    // ==============================
    //   KEEP ALIVE
    // ==============================
    static void StartKeepAlive()
    {
        // Раз в 5 минут
        keepAliveTimer = new System.Timers.Timer(5 * 60 * 1000);
        keepAliveTimer.Elapsed += async (sender, e) =>
        {
            if (BotClient != null)
            {
                try
                {
                    await BotClient.GetMeAsync(); // Лёгкий запрос
                    Console.WriteLine($"[KeepAlive] Bot is alive at {DateTime.Now}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[KeepAlive ERROR] {ex.Message}");
                }
            }
        };
        keepAliveTimer.AutoReset = true;
        keepAliveTimer.Enabled = true;
    }

    // ==============================
    //   ИНИЦИАЛИЗАЦИЯ БД
    // ==============================
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

    // ==============================
    //   ПАРСЕР ДЛЯ DATABASE_URL
    // ==============================
    static string ConvertDatabaseUrlToConnectionString(string databaseUrl)
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':');
        return $"Host={uri.Host};Port={uri.Port};Username={userInfo[0]};Password={userInfo[1]};Database={uri.AbsolutePath.TrimStart('/')};SSL Mode=Require;Trust Server Certificate=true;";
    }

    // ==============================
    //   ОБРАБОТЧИК ОБНОВЛЕНИЙ
    // ==============================
    static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is { } message)
        {
            Console.WriteLine($"[Message] from {message.Chat.Id}: {message.Text}");
            long userId = message.Chat.Id;

            // Ищем/создаём сессию
            if (!Sessions.ContainsKey(userId))
                Sessions[userId] = new UserSession();
            var session = Sessions[userId];

            // Текст команды
            var text = message.Text!.Trim();

            // /start
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
                cmd.CommandText = @"
                    INSERT INTO menu (key, name, price)
                    VALUES (@k, @n, @p)
                    ON CONFLICT (key) DO UPDATE
                    SET name=EXCLUDED.name, price=EXCLUDED.price;
                ";
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
            await HandleCallbackQueryAsync(botClient, query, cancellationToken);
        }
    }

    // ==============================
    //   ОБРАБОТКА КНОПОК
    // ==============================
    static async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        long userId = query.Message!.Chat.Id;
        if (!Sessions.ContainsKey(userId))
            Sessions[userId] = new UserSession();
        var session = Sessions[userId];

        // Убираем "часики" на кнопке
        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        switch (query.Data)
        {
            // Пользователь хочет выбрать кофе
            case "choose_coffee":
                await botClient.SendTextMessageAsync(
                    userId,
                    "☕ Выберите напиток:",
                    replyMarkup: GetCoffeeMenu(),
                    cancellationToken: cancellationToken
                );
                break;

            // Нажал на конкретный кофе
            case var data when data.StartsWith("order_"):
            {
                var coffeeKey = data.Split("_", 2)[1];
                session.SelectedCoffee = coffeeKey;
                var coffee = GetMenu()[coffeeKey];
                await botClient.SendTextMessageAsync(
                    userId,
                    $"Вы выбрали: {coffee.Name} ({coffee.Price} грн)\nВыберите способ оплаты:",
                    replyMarkup: GetPaymentMenu(),
                    cancellationToken: cancellationToken
                );
                break;
            }

            // Статистика за сегодня
            case "stats":
                await ShowStats(userId, cancellationToken);
                break;

            // Ввести пароль для админ-меню
            case "enter_password":
                session.AwaitingPassword = true;
                await botClient.SendTextMessageAsync(
                    userId,
                    "🔑 Введите пароль:",
                    cancellationToken: cancellationToken
                );
                break;

            // Добавить кофе
            case "add_coffee":
                session.AwaitingNewCoffee = true;
                await botClient.SendTextMessageAsync(
                    userId,
                    "Введите название и цену (Напиток - Цена)",
                    cancellationToken: cancellationToken
                );
                break;

            // Возврат в главное меню
            case "back_main":
                await botClient.SendTextMessageAsync(
                    userId,
                    "🔙 Возвращение в главное меню.",
                    replyMarkup: GetMainMenu(),
                    cancellationToken: cancellationToken
                );
                break;

            // Удалить напиток (админ)
            case "remove_coffee":
                await HandleRemoveCoffee(userId, cancellationToken);
                break;

            // Удаляем конкретный напиток
            case var delData when delData.StartsWith("delete_coffee_"):
            {
                var delKey = delData.Split('_', 2)[1];
                using var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!));
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM menu WHERE key=@k";
                cmd.Parameters.AddWithValue("k", delKey);
                cmd.ExecuteNonQuery();

                await botClient.SendTextMessageAsync(
                    userId,
                    "✅ Напиток удалён!",
                    replyMarkup: GetAdminMenu(),
                    cancellationToken: cancellationToken
                );
                break;
            }

            // Отменить последний заказ
            case "undo_order":
                await HandleUndoOrder(userId, cancellationToken);
                break;

            // Оплата наличкой / картой
            case "pay_cash":
            case "pay_card":
            {
                if (session.SelectedCoffee == null)
                {
                    await botClient.SendTextMessageAsync(
                        userId,
                        "⛔ Выберите кофе сначала!",
                        cancellationToken: cancellationToken
                    );
                    return;
                }
                var selected = GetMenu()[session.SelectedCoffee];
                var payment = (query.Data == "pay_cash") ? "cash" : "card";
                var price = selected.Price;
                var dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                // Запись в stats
                using var conn2 = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!));
                conn2.Open();
                using var cmd2 = conn2.CreateCommand();
                cmd2.CommandText = @"
                    INSERT INTO stats (date, coffee_key, payment, count, revenue)
                    VALUES (@d, @k, @pm, 1, @r)
                    ON CONFLICT (date, coffee_key, payment) DO UPDATE
                    SET count = stats.count + 1,
                        revenue = stats.revenue + EXCLUDED.revenue;
                ";
                cmd2.Parameters.AddWithValue("d", dateStr);
                cmd2.Parameters.AddWithValue("k", session.SelectedCoffee);
                cmd2.Parameters.AddWithValue("pm", payment);
                cmd2.Parameters.AddWithValue("r", price);
                cmd2.ExecuteNonQuery();

                // Сохраняем для undo
                session.LastOrder = (dateStr, session.SelectedCoffee, payment, price);

                await botClient.SendTextMessageAsync(
                    userId,
                    $"☕ Заказ добавлен: {selected.Name}, оплата: {(payment == "cash" ? "Наличные" : "Карта")}",
                    replyMarkup: GetMainMenu(),
                    cancellationToken: cancellationToken
                );
                session.SelectedCoffee = null;
                break;
            }

            default:
                await botClient.SendTextMessageAsync(
                    userId,
                    "⚠️ Неизвестная команда.",
                    cancellationToken: cancellationToken
                );
                break;
        }
    }

    // ==============================
    //   УДАЛЕНИЕ НАПИТКА (список)
    // ==============================
    static async Task HandleRemoveCoffee(long userId, CancellationToken cancellationToken)
    {
        var menu = GetMenu();
        if (menu.Count == 0)
        {
            await BotClient!.SendTextMessageAsync(
                userId,
                "⛔ Нет напитков в меню!",
                replyMarkup: GetAdminMenu(),
                cancellationToken: cancellationToken
            );
            return;
        }

        var rows = new List<InlineKeyboardButton[]>();
        foreach (var (key, (Name, Price)) in menu)
        {
            string btnText = $"❌ {Name}";
            string callback = $"delete_coffee_{key}";
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(btnText, callback) });
        }
        // Кнопка назад
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "back_admin") });

        var markup = new InlineKeyboardMarkup(rows);
        await BotClient!.SendTextMessageAsync(
            userId,
            "Выберите напиток для удаления:",
            replyMarkup: markup,
            cancellationToken: cancellationToken
        );
    }

    // ==============================
    //   ОТМЕНА ПОСЛЕДНЕГО ЗАКАЗА
    // ==============================
    static async Task HandleUndoOrder(long userId, CancellationToken cancellationToken)
    {
        var session = Sessions[userId];
        if (session.LastOrder == null)
        {
            await BotClient!.SendTextMessageAsync(
                userId,
                "⛔ Последний заказ не найден.",
                replyMarkup: GetAdminMenu(),
                cancellationToken: cancellationToken
            );
            return;
        }

        var (date, coffeeKey, payment, price) = session.LastOrder.Value;

        using var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT count FROM stats
            WHERE date=@d AND coffee_key=@k AND payment=@p
        ";
        cmd.Parameters.AddWithValue("d", date);
        cmd.Parameters.AddWithValue("k", coffeeKey);
        cmd.Parameters.AddWithValue("p", payment);
        var row = cmd.ExecuteScalar();

        if (row == null)
        {
            // Уже нет записи?
            await BotClient!.SendTextMessageAsync(
                userId,
                "⛔ Не удалось найти заказ в таблице stats.",
                replyMarkup: GetAdminMenu(),
                cancellationToken: cancellationToken
            );
            session.LastOrder = null;
            return;
        }

        int currentCount = Convert.ToInt32(row);
        if (currentCount <= 1)
        {
            // Удаляем всю запись
            cmd.CommandText = @"
                DELETE FROM stats
                WHERE date=@d AND coffee_key=@k AND payment=@p
            ";
            cmd.ExecuteNonQuery();
        }
        else
        {
            // вычитаем 1 из count и из revenue
            cmd.CommandText = @"
                UPDATE stats
                SET count = count - 1,
                    revenue = revenue - @price
                WHERE date=@d AND coffee_key=@k AND payment=@p
            ";
            cmd.Parameters.AddWithValue("price", price);
            cmd.ExecuteNonQuery();
        }

        session.LastOrder = null;

        await BotClient!.SendTextMessageAsync(
            userId,
            "✅ Последний заказ отменён.",
            replyMarkup: GetAdminMenu(),
            cancellationToken: cancellationToken
        );
    }

    // ==============================
    //   ПОКАЗАТЬ СТАТИСТИКУ ЗА СЕГОДНЯ
    // ==============================
    static async Task ShowStats(long userId, CancellationToken cancellationToken)
    {
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        int total = 0;
        string statText = $"📊 Статистика за {today}:\n";

        using var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!));
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

        statText += $"\n\n💰 Общая выручка: {total} грн";

        await BotClient!.SendTextMessageAsync(
            userId,
            statText,
            replyMarkup: GetMainMenu(),
            cancellationToken: cancellationToken
        );
    }

    // ==============================
    //   ОБРАБОТКА ОШИБОК ПОЛЛИНГА
    // ==============================
    static Task HandleErrorAsync(ITelegramBotClient botClient, Exception ex, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Error] {ex.Message}");
        return Task.CompletedTask;
    }

    // ==============================
    //   ГЛАВНОЕ МЕНЮ
    // ==============================
    static InlineKeyboardMarkup GetMainMenu() =>
        new InlineKeyboardMarkup(
            new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("☕ Выбрать кофе", "choose_coffee") },
                new[] { InlineKeyboardButton.WithCallbackData("📊 Статистика (сегодня)", "stats") },
                new[] { InlineKeyboardButton.WithCallbackData("🔧 Ввести пароль (админ)", "enter_password") }
            }
        );

    // ==============================
    //   АДМИН-МЕНЮ
    // ==============================
    static InlineKeyboardMarkup GetAdminMenu() =>
        new InlineKeyboardMarkup(
            new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("➕ Добавить напиток", "add_coffee") },
                new[] { InlineKeyboardButton.WithCallbackData("🗑 Удалить напиток", "remove_coffee") },
                new[] { InlineKeyboardButton.WithCallbackData("⏪ Отменить заказ", "undo_order") },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "back_main") }
            }
        );

    // ==============================
    //   ВЫБОР КОФЕ
    // ==============================
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
        // Кнопка назад
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "back_main") });

        return new InlineKeyboardMarkup(rows);
    }

    // ==============================
    //   СПОСОБ ОПЛАТЫ
    // ==============================
    static InlineKeyboardMarkup GetPaymentMenu() =>
        new InlineKeyboardMarkup(
            new[]
            {
                new[] {
                    InlineKeyboardButton.WithCallbackData("💵 Наличные", "pay_cash"),
                    InlineKeyboardButton.WithCallbackData("💳 Карта",    "pay_card")
                },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "choose_coffee") }
            }
        );

    // ==============================
    //   Читаем позиции меню из БД
    // ==============================
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

// ==============================
//   СЕССИЯ ПОЛЬЗОВАТЕЛЯ
// ==============================
class UserSession
{
    public bool AwaitingPassword { get; set; } = false;
    public bool AwaitingNewCoffee { get; set; } = false;
    public string? SelectedCoffee { get; set; }

    // Сохраняем данные последнего заказа для undo
    public (string date, string coffeeKey, string payment, int price)? LastOrder { get; set; }

    public string? LastMenuItem { get; set; }
}
