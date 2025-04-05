using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Timers;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Npgsql;

class Program
{
    // --- Конфигурация ---
    static string AdminPassword = "Igor123";
    static string? BotToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
    static string? DatabaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

    static TelegramBotClient? BotClient;
    static ConcurrentDictionary<long, UserSession> Sessions = new();
    static System.Timers.Timer? keepAliveTimer;

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
        StartKeepAlive();

        var cts = new CancellationTokenSource();
        var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };
        BotClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, cancellationToken: cts.Token);

        Console.WriteLine("✅ StartReceiving запущен. Ожидание сообщений...");
        await Task.Delay(-1);
    }

    static void StartKeepAlive()
    {
        keepAliveTimer = new System.Timers.Timer(5 * 60 * 1000);
        keepAliveTimer.Elapsed += async (sender, e) =>
        {
            if (BotClient != null)
            {
                try
                {
                    await BotClient.GetMeAsync();
                    Console.WriteLine($"[KeepAlive] Bot alive at {DateTime.Now}");
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
            Console.WriteLine($"[Message] from {message.Chat.Id}: {message.Text}");
            long userId = message.Chat.Id;

            if (!Sessions.ContainsKey(userId))
                Sessions[userId] = new UserSession();
            var session = Sessions[userId];
            var text = message.Text!.Trim();

            if (text == "/start")
            {
                await botClient.SendTextMessageAsync(userId, "Привет! Выбери действие:", replyMarkup: GetMainMenu(), cancellationToken: cancellationToken);
                return;
            }

            // Если ждём пароль
            if (session.AwaitingPassword)
            {
                session.AwaitingPassword = false;
                if (text == AdminPassword)
                    await botClient.SendTextMessageAsync(userId, "✅ Пароль верный!", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
                else
                    await botClient.SendTextMessageAsync(userId, "❌ Неверный пароль!", cancellationToken: cancellationToken);
                return;
            }

            // Если ждём новый кофе
            if (session.AwaitingNewCoffee)
            {
                session.AwaitingNewCoffee = false;
                if (!text.Contains('-'))
                {
                    await botClient.SendTextMessageAsync(userId, "❌ Неверный формат. Используйте: Название - Цена", cancellationToken: cancellationToken);
                    return;
                }
                var parts = text.Split('-', 2);
                var name = parts[0].Trim();
                if (!int.TryParse(parts[1].Trim(), out var price))
                {
                    await botClient.SendTextMessageAsync(userId, "❌ Цена должна быть числом!", cancellationToken: cancellationToken);
                    return;
                }
                var key = name.ToLower().Replace(" ", "_");

                using var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!));
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO menu (key, name, price)
                    VALUES (@k, @n, @p)
                    ON CONFLICT (key) DO UPDATE SET name = EXCLUDED.name, price = EXCLUDED.price;";
                cmd.Parameters.AddWithValue("k", key);
                cmd.Parameters.AddWithValue("n", name);
                cmd.Parameters.AddWithValue("p", price);
                cmd.ExecuteNonQuery();

                session.LastMenuItem = key;

                await botClient.SendTextMessageAsync(userId, $"✅ Напиток добавлен: {name} — {price} грн", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
                return;
            }

            // Если ждём название для изменения статистики
            if (session.AwaitingEditStatsName)
            {
                session.AwaitingEditStatsName = false;
                var name = message.Text!.Trim();
                var menu = GetMenu();
                var found = menu.FirstOrDefault(x => string.Equals(x.Value.Name, name, StringComparison.OrdinalIgnoreCase));
                if (found.Key == null)
                {
                    await botClient.SendTextMessageAsync(userId, "⛔ Напиток не найден в меню.", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
                    return;
                }
                session.EditStatsCoffeeKey = found.Key;
                session.AwaitingEditStatsCount = true;
                await botClient.SendTextMessageAsync(userId, $"☕ Найден напиток: {found.Value.Name}. Введите количество заказов для уменьшения:", cancellationToken: cancellationToken);
                return;
            }

            // Если ждём количество для изменения статистики
            if (session.AwaitingEditStatsCount)
            {
                session.AwaitingEditStatsCount = false;
                if (!int.TryParse(message.Text!.Trim(), out int countToRemove) || countToRemove <= 0)
                {
                    await botClient.SendTextMessageAsync(userId, "⛔ Введите положительное число!", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
                    return;
                }
                if (session.EditStatsCoffeeKey == null)
                {
                    await botClient.SendTextMessageAsync(userId, "⛔ Ошибка. Не выбран напиток.", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
                    return;
                }

                string coffeeKey = session.EditStatsCoffeeKey;
                string today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                using var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!));
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count, revenue FROM stats WHERE date=@d AND coffee_key=@k";
                cmd.Parameters.AddWithValue("d", today);
                cmd.Parameters.AddWithValue("k", coffeeKey);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    await botClient.SendTextMessageAsync(userId, "⛔ Статистика за сегодня по напитку не найдена.", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
                    return;
                }
                int currentCount = reader.GetInt32(0);
                reader.Close();

                if (countToRemove >= currentCount)
                {
                    cmd.CommandText = "DELETE FROM stats WHERE date=@d AND coffee_key=@k";
                    cmd.ExecuteNonQuery();
                    await botClient.SendTextMessageAsync(userId, "✅ Статистика обнулена и запись удалена.", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
                }
                else
                {
                    var menu = GetMenu();
                    int pricePerUnit = menu[coffeeKey].Price;
                    cmd.CommandText = @"
                        UPDATE stats
                        SET count = count - @cnt, revenue = revenue - @sum
                        WHERE date=@d AND coffee_key=@k";
                    cmd.Parameters.AddWithValue("cnt", countToRemove);
                    cmd.Parameters.AddWithValue("sum", countToRemove * pricePerUnit);
                    cmd.ExecuteNonQuery();
                    await botClient.SendTextMessageAsync(userId, $"✅ Статистика уменьшена на {countToRemove} заказов.", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
                }

                session.EditStatsCoffeeKey = null;
                return;
            }
        }
        else if (update.CallbackQuery is { } query)
        {
            await HandleCallbackQueryAsync(botClient, query, cancellationToken);
        }
    }
    static async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        long userId = query.Message!.Chat.Id;

        if (!Sessions.ContainsKey(userId))
            Sessions[userId] = new UserSession();
        var session = Sessions[userId];

        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        switch (query.Data)
        {
            case "choose_coffee":
                await botClient.SendTextMessageAsync(userId, "☕ Выберите напиток:", replyMarkup: GetCoffeeMenu(), cancellationToken: cancellationToken);
                break;

            case var data when data.StartsWith("order_"):
                var coffeeKey = data.Split("_", 2)[1];
                session.SelectedCoffee = coffeeKey;
                var coffee = GetMenu()[coffeeKey];
                await botClient.SendTextMessageAsync(userId, $"Вы выбрали: {coffee.Name} — {coffee.Price} грн.\nВыберите способ оплаты:", replyMarkup: GetPaymentMenu(), cancellationToken: cancellationToken);
                break;

            case "pay_cash":
            case "pay_card":
                if (session.SelectedCoffee == null)
                {
                    await botClient.SendTextMessageAsync(userId, "⛔ Сначала выберите кофе!", cancellationToken: cancellationToken);
                    return;
                }
                var selected = GetMenu()[session.SelectedCoffee];
                var payment = query.Data == "pay_cash" ? "cash" : "card";
                var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                using (var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!)))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO stats (date, coffee_key, payment, count, revenue)
                        VALUES (@d, @k, @p, 1, @r)
                        ON CONFLICT (date, coffee_key, payment) DO UPDATE 
                        SET count = stats.count + 1, revenue = stats.revenue + EXCLUDED.revenue";
                    cmd.Parameters.AddWithValue("d", date);
                    cmd.Parameters.AddWithValue("k", session.SelectedCoffee);
                    cmd.Parameters.AddWithValue("p", payment);
                    cmd.Parameters.AddWithValue("r", selected.Price);
                    cmd.ExecuteNonQuery();
                }

                session.LastOrder = (date, session.SelectedCoffee, payment, selected.Price);

                await botClient.SendTextMessageAsync(userId, $"☕ Заказ добавлен: {selected.Name}, оплата: {(payment == "cash" ? "наличные" : "карта")}.", replyMarkup: GetMainMenu(), cancellationToken: cancellationToken);
                session.SelectedCoffee = null;
                break;

            case "stats":
                await ShowStats(userId, cancellationToken);
                break;

            case "enter_password":
                session.AwaitingPassword = true;
                await botClient.SendTextMessageAsync(userId, "🔑 Введите пароль:", cancellationToken: cancellationToken);
                break;

            case "add_coffee":
                session.AwaitingNewCoffee = true;
                await botClient.SendTextMessageAsync(userId, "Введите название и цену кофе (пример: Латте - 45):", cancellationToken: cancellationToken);
                break;

            case "remove_coffee":
                await HandleRemoveCoffee(userId, cancellationToken);
                break;

            case var del when del.StartsWith("delete_coffee_"):
                var delKey = del.Split("_", 2)[1];
                using (var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!)))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM menu WHERE key=@k";
                    cmd.Parameters.AddWithValue("k", delKey);
                    cmd.ExecuteNonQuery();
                }
                await botClient.SendTextMessageAsync(userId, "✅ Напиток удалён!", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
                break;

            case "undo_order":
                await HandleUndoOrder(userId, cancellationToken);
                break;

            case "edit_stats":
                session.AwaitingEditStatsName = true;
                await botClient.SendTextMessageAsync(userId, "✏️ Введите название напитка для изменения статистики:", cancellationToken: cancellationToken);
                break;

            case "back_main":
                await botClient.SendTextMessageAsync(userId, "🔙 Возврат в главное меню.", replyMarkup: GetMainMenu(), cancellationToken: cancellationToken);
                break;
        }
    }
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
        var menu = GetMenu();
        while (reader.Read())
        {
            string coffeeKey = reader.GetString(0);
            string payment = reader.GetString(1);
            int count = reader.GetInt32(2);
            int revenue = reader.GetInt32(3);

            string coffeeName = menu.ContainsKey(coffeeKey) ? menu[coffeeKey].Name : coffeeKey;
            string payName = payment == "cash" ? "Наличные" : "Карта";

            statText += $"☕ {coffeeName} ({payName}) — {count} шт. ({revenue} грн)\n";
            total += revenue;
        }

        statText += $"\n💰 Общая выручка: {total} грн";

        await BotClient!.SendTextMessageAsync(userId, statText, replyMarkup: GetMainMenu(), cancellationToken: cancellationToken);
    }

    static async Task HandleRemoveCoffee(long userId, CancellationToken cancellationToken)
    {
        var menu = GetMenu();
        if (menu.Count == 0)
        {
            await BotClient!.SendTextMessageAsync(userId, "⛔ Нет напитков в меню.", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
            return;
        }

        var buttons = new List<InlineKeyboardButton[]>();
        foreach (var (key, value) in menu)
        {
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData($"❌ {value.Name}", $"delete_coffee_{key}") });
        }
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "back_main") });

        await BotClient!.SendTextMessageAsync(userId, "Выберите напиток для удаления:", replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: cancellationToken);
    }

    static async Task HandleUndoOrder(long userId, CancellationToken cancellationToken)
    {
        var session = Sessions[userId];
        if (session.LastOrder == null)
        {
            await BotClient!.SendTextMessageAsync(userId, "⛔ Последний заказ не найден.", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
            return;
        }

        var (date, coffeeKey, payment, price) = session.LastOrder.Value;
        using var conn = new NpgsqlConnection(ConvertDatabaseUrlToConnectionString(DatabaseUrl!));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count FROM stats WHERE date=@d AND coffee_key=@k AND payment=@p";
        cmd.Parameters.AddWithValue("d", date);
        cmd.Parameters.AddWithValue("k", coffeeKey);
        cmd.Parameters.AddWithValue("p", payment);

        var result = cmd.ExecuteScalar();
        if (result == null)
        {
            await BotClient!.SendTextMessageAsync(userId, "⛔ Строка статистики не найдена.", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
            session.LastOrder = null;
            return;
        }

        int currentCount = Convert.ToInt32(result);
        if (currentCount <= 1)
        {
            cmd.CommandText = "DELETE FROM stats WHERE date=@d AND coffee_key=@k AND payment=@p";
            cmd.ExecuteNonQuery();
        }
        else
        {
            cmd.CommandText = @"
                UPDATE stats
                SET count = count - 1, revenue = revenue - @price
                WHERE date=@d AND coffee_key=@k AND payment=@p";
            cmd.Parameters.AddWithValue("price", price);
            cmd.ExecuteNonQuery();
        }

        session.LastOrder = null;

        await BotClient!.SendTextMessageAsync(userId, "✅ Последний заказ отменён.", replyMarkup: GetAdminMenu(), cancellationToken: cancellationToken);
    }
    static InlineKeyboardMarkup GetMainMenu() =>
        new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("☕ Выбрать кофе", "choose_coffee") },
            new[] { InlineKeyboardButton.WithCallbackData("📊 Статистика (сегодня)", "stats") },
            new[] { InlineKeyboardButton.WithCallbackData("🔧 Ввести пароль (админ)", "enter_password") }
        });

    static InlineKeyboardMarkup GetAdminMenu() =>
        new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("➕ Добавить напиток", "add_coffee") },
            new[] { InlineKeyboardButton.WithCallbackData("🗑 Удалить напиток", "remove_coffee") },
            new[] { InlineKeyboardButton.WithCallbackData("✏️ Изменить статистику", "edit_stats") },
            new[] { InlineKeyboardButton.WithCallbackData("⏪ Отменить заказ", "undo_order") },
            new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "back_main") }
        });

    static InlineKeyboardMarkup GetCoffeeMenu()
    {
        var menu = GetMenu();
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var (key, value) in menu)
        {
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData($"☕ {value.Name} — {value.Price} грн", $"order_{key}") });
        }
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "back_main") });
        return new InlineKeyboardMarkup(rows);
    }

    static InlineKeyboardMarkup GetPaymentMenu() =>
        new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("💵 Наличные", "pay_cash"),
                InlineKeyboardButton.WithCallbackData("💳 Карта", "pay_card")
            },
            new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "choose_coffee") }
        });

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
            result[reader.GetString(0)] = (reader.GetString(1), reader.GetInt32(2));
        }
        return result;
    }

    static Task HandleErrorAsync(ITelegramBotClient botClient, Exception ex, CancellationToken token)
    {
        Console.WriteLine($"[ERROR] {ex.Message}");
        return Task.CompletedTask;
    }
}

// =========================
//     КЛАСС СЕССИИ
// =========================
class UserSession
{
    public bool AwaitingPassword { get; set; } = false;
    public bool AwaitingNewCoffee { get; set; } = false;
    public string? SelectedCoffee { get; set; }
    public (string date, string coffeeKey, string payment, int price)? LastOrder { get; set; }
    public string? LastMenuItem { get; set; }

    public bool AwaitingEditStatsName { get; set; } = false;
    public bool AwaitingEditStatsCount { get; set; } = false;
    public string? EditStatsCoffeeKey { get; set; }
}
