using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TransportIn.Data;
using TransportIn.Models;

namespace TransportIn.Services
{
    // Авторизація: перевірка логіна й пароля за таблицею Users, визначення ролі та працівника
    public class AuthService
    {
        private readonly IDbContextFactory<AppDbContext> _contextFactory;

        public AuthService(IDbContextFactory<AppDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        public User? CurrentUser { get; private set; }

        // Повертає true при успішному вході; інакше в error — текст помилки для користувача
        public bool TryLogin(string? username, string? password, out string error)
        {
            error = "";

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                error = "Будь ласка, заповніть всі поля для входу!";
                return false;
            }

            DbSeeder.EnsureSeeded(_contextFactory);

            using var db = _contextFactory.CreateDbContext();

            var login = username.Trim();
            var entity = db.Users
                .AsNoTracking()
                .Include(u => u.RoleNavigation)
                .Include(u => u.EmployeeNavigation)
                .FirstOrDefault(u => u.Username == login);

            if (entity == null)
            {
                error = "Користувача з таким логіном не знайдено.";
                return false;
            }

            if (!PasswordHasher.Verify(password, entity.PasswordHash))
            {
                error = "Неправильний пароль.";
                return false;
            }

            if (entity.EmployeeNavigation is { IsActive: false })
            {
                error = "Обліковий запис заблоковано або деактивовано.";
                return false;
            }

            var role = MapRole(entity.RoleNavigation.Name);
            if (role == null)
            {
                error = "Для облікового запису не визначено роль.";
                return false;
            }

            CurrentUser = new User
            {
                Id = entity.Id,
                Username = entity.Username,
                Role = role.Value,
                FullName = entity.FullName,
                Phone = entity.Phone,
                EmployeeId = entity.EmployeeId
            };
            return true;
        }

        public User? Login(string username, string password) =>
            TryLogin(username, password, out _) ? CurrentUser : null;

        public void Logout()
        {
            CurrentUser = null;
        }

        private static UserRole? MapRole(string roleName) => roleName switch
        {
            "Адміністратор" => UserRole.Admin,
            "Менеджер" => UserRole.Manager,
            "Водій" => UserRole.Driver,
            _ => null
        };
    }
}