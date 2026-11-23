using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TransportIn.Data;

// Отличаем сущность PostgreSQL от модели для интерфейса
using EmployeeEntity = TransportIn.Data.Entities.Employee;

namespace TransportIn.Services
{
    // Модель для отображения сотрудников на странице
    public class Employee
    {
        public int Id { get; set; }
        public string FullName { get; set; } = "";
        public string Role { get; set; } = "Водій";
        public string Phone { get; set; } = "";
        public string LicenseCategory { get; set; } = "C, CE";
        public string MedicalCertExpiry { get; set; } = "";
        public bool IsActive { get; set; } = true;

        // Дані для входу в систему (необов'язкові: без логіна обліковий запис не створюється)
        public string Login { get; set; } = "";
        public string Password { get; set; } = "";
        public string Email { get; set; } = "";
    }

    public class EmployeeService
    {
        private readonly IDbContextFactory<AppDbContext> _contextFactory;

        public EmployeeService(IDbContextFactory<AppDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        public event Action? OnChange;

        // Получение сотрудников
        public List<Employee> GetAllEmployees()
        {
            using var db = _contextFactory.CreateDbContext();

            return db.Employees
                .AsNoTracking()
                .OrderBy(e => e.Id)
                .Select(e => new Employee
                {
                    Id = e.Id,
                    FullName = e.FullName,
                    Role = e.RoleNavigation.Name,
                    Phone = e.Phone,
                    LicenseCategory = e.LicenseCategory,
                    MedicalCertExpiry = e.MedicalCertExpiry,
                    IsActive = e.IsActive
                })
                .ToList();
        }

        // Добавление сотрудника (разом з обліковим записом для входу, якщо вказано логін)
        public void AddEmployee(Employee employee)
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = db.Database.BeginTransaction();

            string roleName = employee.Role.Trim();

            var role = db.Roles
                .FirstOrDefault(r => r.Name == roleName);

            if (role == null)
            {
                throw new InvalidOperationException(
                    $"Роль '{roleName}' не найдена в таблице Roles."
                );
            }

            var login = (employee.Login ?? "").Trim();
            if (login.Length > 0)
            {
                if ((employee.Password ?? "").Length < 6)
                    throw new InvalidOperationException("Пароль має містити мінімум 6 символів.");

                if (db.Users.Any(u => u.Username == login))
                    throw new InvalidOperationException($"Логін '{login}' вже зайнятий.");
            }

            var newEmployee = new EmployeeEntity
            {
                FullName = employee.FullName,
                Role = roleName,
                Phone = employee.Phone,
                LicenseCategory = employee.LicenseCategory,
                MedicalCertExpiry = employee.MedicalCertExpiry,
                IsActive = true,
                Address = "",
                Email = employee.Email?.Trim() ?? "",
                PhotoPath = "",
                RoleId = role.Id
            };

            db.Employees.Add(newEmployee);
            db.SaveChanges();

            if (login.Length > 0)
            {
                db.Users.Add(new TransportIn.Data.Entities.User
                {
                    Username = login,
                    PasswordHash = PasswordHasher.Hash(employee.Password!),
                    Role = role.Id,
                    EmployeeId = newEmployee.Id,
                    FullName = newEmployee.FullName,
                    Phone = newEmployee.Phone
                });
                db.SaveChanges();
            }

            transaction.Commit();

            employee.Id = newEmployee.Id;

            NotifyStateChanged();
        }

        // Деактивация сотрудника
        public void DeleteEmployee(int id)
        {
            using var db = _contextFactory.CreateDbContext();

            var employee = db.Employees
                .FirstOrDefault(e => e.Id == id);

            if (employee == null)
                return;

            employee.IsActive = false;

            db.SaveChanges();

            NotifyStateChanged();
        }

        // Повторная активация сотрудника
        public void ActivateEmployee(int id)
        {
            using var db = _contextFactory.CreateDbContext();

            var employee = db.Employees
                .FirstOrDefault(e => e.Id == id);

            if (employee == null)
                return;

            employee.IsActive = true;

            db.SaveChanges();

            NotifyStateChanged();
        }

        // Асинхронное получение сотрудников
        public async Task<List<Employee>> GetAllEmployeesAsync()
        {
            await using var db =
                await _contextFactory.CreateDbContextAsync();

            return await db.Employees
                .AsNoTracking()
                .OrderBy(e => e.Id)
                .Select(e => new Employee
                {
                    Id = e.Id,
                    FullName = e.FullName,
                    Role = e.RoleNavigation.Name,
                    Phone = e.Phone,
                    LicenseCategory = e.LicenseCategory,
                    MedicalCertExpiry = e.MedicalCertExpiry,
                    IsActive = e.IsActive
                })
                .ToListAsync();
        }

        private void NotifyStateChanged()
        {
            OnChange?.Invoke();
        }
    }
}