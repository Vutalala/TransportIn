using Microsoft.EntityFrameworkCore;
using TransportIn.Data;

namespace TransportIn.Services
{
    // Початкові дані для входу: ролі та облікові записи адміністратора й менеджера.
    // Виконується один раз за запуск, нічого не перезаписує, якщо дані вже є.
    public static class DbSeeder
    {
        private static readonly object Sync = new();
        private static bool _done;

        public static void EnsureSeeded(IDbContextFactory<AppDbContext> factory)
        {
            if (_done)
                return;

            lock (Sync)
            {
                if (_done)
                    return;

                using var db = factory.CreateDbContext();

                EnsureRole(db, "Адміністратор", "Керує обліковими записами, транспортом і подіями");
                EnsureRole(db, "Менеджер", "Оформлює замовлення та призначає транспорт");
                EnsureRole(db, "Водій", "Виконує перевезення");
                db.SaveChanges();

                var roles = db.Roles
                .AsEnumerable()
                .GroupBy(r => r.Name)
                .ToDictionary(g => g.Key, g => g.Min(r => r.Id));

                // Паролі змініть за потреби (мінімум 6 символів за ТЗ)
                EnsureUser(db, "admin", "admin123", roles["Адміністратор"], "Головний Адмін", "0500000001");
                EnsureUser(db, "manager", "manager123", roles["Менеджер"], "Менеджер Складу", "0500000002");
                db.SaveChanges();

                _done = true;
            }
        }

        private static void EnsureRole(AppDbContext db, string name, string description)
        {
            if (!db.Roles.Any(r => r.Name == name))
                db.Roles.Add(new TransportIn.Data.Entities.Role { Name = name, Description = description });
        }

        private static void EnsureUser(
            AppDbContext db, string username, string password, int roleId, string fullName, string phone)
        {
            if (db.Users.Any(u => u.Username == username))
                return;

            db.Users.Add(new TransportIn.Data.Entities.User
            {
                Username = username,
                PasswordHash = PasswordHasher.Hash(password),
                Role = roleId,
                EmployeeId = null,
                FullName = fullName,
                Phone = phone
            });
        }
    }
}
