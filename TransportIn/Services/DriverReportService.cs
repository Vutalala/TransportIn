using Microsoft.EntityFrameworkCore;
using TransportIn.Data;
using TransportIn.Data.Entities;

namespace TransportIn.Services
{
    public class DriverReportItem
    {
        public int Id { get; set; }
        public string DriverName { get; set; } = "";
        public int? OrderId { get; set; }
        public string Category { get; set; } = "manager"; // "admin" (ДТП / дорога) або "manager" (клієнт / вантаж)
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public bool IsRead { get; set; }
    }
    public class DriverReplyItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public bool IsRead { get; set; }
    }
    public class DriverReportService
    {
        public const string TypeAdmin = "ДТП / дорога";
        public const string TypeManager = "Клієнт / вантаж";

        private readonly IDbContextFactory<AppDbContext> _contextFactory;

        public DriverReportService(IDbContextFactory<AppDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        public event Action? OnChange;

        // Водій надсилає повідомлення; воно прив'язується до його активного замовлення (якщо є)
        public bool Send(int driverEmployeeId, int? orderId, string category, string subject, string body)
        {
            using var db = _contextFactory.CreateDbContext();

            if (!db.Employees.Any(e => e.Id == driverEmployeeId))
                return false;

            db.DriverReports.Add(new DriverReport
            {
                DriverId = driverEmployeeId,
                OrderId = orderId,
                ReportType = category == "admin" ? TypeAdmin : TypeManager,
                // Тема й опис зберігаються в одному полі
                Description = subject.Trim() + "\n" + body.Trim(),
                CargoCondition = "",
                VehicleCondition = "",
                CreatedAt = DateTime.UtcNow,
                RequiresAttention = true
            });
            db.SaveChanges();

            OnChange?.Invoke();
            return true;
        }

        public List<DriverReportItem> GetReports(string category)
        {
            using var db = _contextFactory.CreateDbContext();
            var type = category == "admin" ? TypeAdmin : TypeManager;

            var rows = db.DriverReports
                .AsNoTracking()
                .Where(r => r.ReportType == type)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new { r.Id, r.Driver.FullName, r.OrderId, r.Description, r.CreatedAt, r.RequiresAttention })
                .ToList();

            return rows.Select(r =>
            {
                var parts = r.Description.Split('\n', 2);
                return new DriverReportItem
                {
                    Id = r.Id,
                    DriverName = r.FullName,
                    OrderId = r.OrderId,
                    Category = category,
                    Subject = parts[0],
                    Body = parts.Length > 1 ? parts[1] : "",
                    CreatedAt = r.CreatedAt,
                    IsRead = !r.RequiresAttention
                };
            }).ToList();
        }

        public void MarkRead(int reportId)
        {
            using var db = _contextFactory.CreateDbContext();
            var report = db.DriverReports.FirstOrDefault(r => r.Id == reportId);
            if (report == null || !report.RequiresAttention)
                return;

            report.RequiresAttention = false;
            db.SaveChanges();
            OnChange?.Invoke();
        }
        private const string ReplyPrefix = "ReportReply:";

        // Відповідь менеджера на повідомлення водія: зберігається як сповіщення для цього водія
        public bool Reply(int reportId, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            using var db = _contextFactory.CreateDbContext();

            var report = db.DriverReports
                .AsNoTracking()
                .Where(r => r.Id == reportId)
                .Select(r => new { r.DriverId, r.Description })
                .FirstOrDefault();
            if (report == null)
                return false;

            var subject = report.Description.Split('\n', 2)[0];
            if (subject.Length > 150) subject = subject[..150];

            var message = text.Trim();
            if (message.Length > 2000) message = message[..2000];

            db.Notifications.Add(new Notification
            {
                EmployeeId = report.DriverId,
                Title = "Відповідь на: " + subject,
                Message = message,
                NotificationType = ReplyPrefix + reportId,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            });
            db.SaveChanges();

            OnChange?.Invoke();
            return true;
        }

        // Усі відповіді за повідомленнями: ключ — Id повідомлення (для менеджера)
        public Dictionary<int, List<(string Text, DateTime At)>> GetReplies()
        {
            using var db = _contextFactory.CreateDbContext();

            var rows = db.Notifications
                .AsNoTracking()
                .Where(n => n.NotificationType.StartsWith(ReplyPrefix))
                .OrderBy(n => n.CreatedAt)
                .Select(n => new { n.NotificationType, n.Message, n.CreatedAt })
                .ToList();

            var result = new Dictionary<int, List<(string, DateTime)>>();
            foreach (var r in rows)
            {
                if (!int.TryParse(r.NotificationType.Substring(ReplyPrefix.Length), out var reportId))
                    continue;
                if (!result.TryGetValue(reportId, out var list))
                    result[reportId] = list = new();
                list.Add((r.Message, r.CreatedAt));
            }
            return result;
        }

        // Відповіді, адресовані конкретному водієві
        public List<DriverReplyItem> GetRepliesForDriver(int driverEmployeeId)
        {
            using var db = _contextFactory.CreateDbContext();

            return db.Notifications
                .AsNoTracking()
                .Where(n => n.EmployeeId == driverEmployeeId && n.NotificationType.StartsWith(ReplyPrefix))
                .OrderByDescending(n => n.CreatedAt)
                .Take(20)
                .Select(n => new DriverReplyItem
                {
                    Id = n.Id,
                    Title = n.Title,
                    Text = n.Message,
                    CreatedAt = n.CreatedAt,
                    IsRead = n.IsRead
                })
                .ToList();
        }

        public void MarkReplyRead(int notificationId)
        {
            using var db = _contextFactory.CreateDbContext();
            var n = db.Notifications.FirstOrDefault(x => x.Id == notificationId);
            if (n == null || n.IsRead)
                return;

            n.IsRead = true;
            n.ReadAt = DateTime.UtcNow;
            db.SaveChanges();
            OnChange?.Invoke();
        }
    }
}