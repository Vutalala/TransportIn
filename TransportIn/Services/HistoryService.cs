using Microsoft.EntityFrameworkCore;
using TransportIn.Data;

namespace TransportIn.Services
{
    public class HistoryPoint
    {
        public bool IsLoading { get; set; }
        public string Address { get; set; } = "";
        public string Status { get; set; } = "Нова";
    }

    public class HistoryStep
    {
        public DateTime At { get; set; }
        public string Text { get; set; } = "";
    }

    public class HistoryOrder
    {
        public int Id { get; set; }
        public DateTime? At { get; set; }
        public string CustomerCompany { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string CargoName { get; set; } = "";
        public double WeightTons { get; set; }
        public double VolumeM3 { get; set; }
        public List<string> Drivers { get; set; } = new();
        public List<string> Plates { get; set; } = new();
        public string Status { get; set; } = "";
        public List<HistoryPoint> Points { get; set; } = new();
        public List<HistoryStep> Steps { get; set; } = new();
    }

    public class HistoryEvent
    {
        public DateTime At { get; set; }
        public string Type { get; set; } = "status";   // status | msg
        public string TypeLabel { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
    }

    public class HistoryRule
    {
        public DateTime At { get; set; }
        public string Threat { get; set; } = "";
        public string RuleName { get; set; } = "";
        public string Recommendation { get; set; } = "";
        public string Target { get; set; } = "";
        public string Result { get; set; } = "";
    }
    public class LivePoint
    {
        public int Id { get; set; }
        public int Seq { get; set; }
        public bool IsLoading { get; set; }
        public string Address { get; set; } = "";
        public string Status { get; set; } = "Нова";
        public double? Lat { get; set; }
        public double? Lng { get; set; }
    }

    public class VehicleLive
    {
        public int OrderId { get; set; }
        public string CargoName { get; set; } = "";
        public string Customer { get; set; } = "";
        public string DriverName { get; set; } = "";
        public string Label { get; set; } = "";
        public bool Stopped { get; set; }
        public bool Suspended { get; set; }
        public int CurrentIndex { get; set; }
        public List<LivePoint> Points { get; set; } = new();
    }
    public class HistoryService
    {
        private readonly IDbContextFactory<AppDbContext> _contextFactory;

        public HistoryService(IDbContextFactory<AppDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        private static bool IsLoadingPoint(string pointType) =>
            pointType.Contains("load", StringComparison.OrdinalIgnoreCase) ||
            pointType.Contains("pickup", StringComparison.OrdinalIgnoreCase) ||
            pointType.Contains("завантаж", StringComparison.OrdinalIgnoreCase) ||
            pointType.Contains("відправ", StringComparison.OrdinalIgnoreCase);

        private static List<string> Split(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : value.Split(new[] { '|', ';', '\r', '\n' },
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        // Зберігає координати пункту після геокодування, щоб не робити запит повторно
        public void SavePointCoordinates(int pointId, double lat, double lng)
        {
            using var db = _contextFactory.CreateDbContext();
            var point = db.OrderPoints.FirstOrDefault(p => p.Id == pointId);
            if (point == null) return;

            point.Latitude = lat;
            point.Longitude = lng;
            db.SaveChanges();
        }

        // Реальний стан машин за активними замовленнями (для карти моніторингу)
        public Dictionary<string, VehicleLive> GetVehicleStates()
        {
            using var db = _contextFactory.CreateDbContext();

            var orders = db.Orders
                .AsNoTracking()
                .AsSplitQuery()
                .Include(o => o.OrderPoints)
                .Include(o => o.OrderAssignments).ThenInclude(a => a.Vehicle)
                .Include(o => o.OrderAssignments).ThenInclude(a => a.Driver)
                .Where(o => o.Status == "В роботі" || o.Status == "Призупинено")
                .ToList();

            var result = new Dictionary<string, VehicleLive>();

            foreach (var o in orders)
            {
                var points = o.OrderPoints.OrderBy(p => p.SequenceNumber).ToList();
                if (points.Count == 0) continue;

                // Поточний пункт: перший, з якого водій ще не виїхав
                var current = points.FirstOrDefault(p => p.Status != "Виїхав") ?? points[^1];
                var status = string.IsNullOrWhiteSpace(current.Status) ? "Нова" : current.Status;
                var n = current.SequenceNumber;
                var currentIndex = points.IndexOf(current);

                var live = new VehicleLive
                {
                    OrderId = o.Id,
                    CargoName = o.CargoName,
                    Customer = o.CustomerCompany,
                    CurrentIndex = currentIndex,
                    Points = points.Select(p => new LivePoint
                    {
                        Id = p.Id,
                        Seq = p.SequenceNumber,
                        IsLoading = IsLoadingPoint(p.PointType),
                        Address = p.Address,
                        Status = string.IsNullOrWhiteSpace(p.Status) ? "Нова" : p.Status,
                        Lat = p.Latitude,
                        Lng = p.Longitude
                    }).ToList(),
                    Stopped = o.Status == "Призупинено" || status != "Нова",
                    Label = o.Status == "Призупинено" ? "призупинено" : status switch
                    {
                        "Нова" => $"в дорозі до пункту {n}",
                        "Прибув" => $"прибув на пункт {n}",
                        "Завантажується" => $"завантаження (пункт {n})",
                        "Завантажено" => $"завантажено, очікує виїзду (пункт {n})",
                        "Розвантажується" => $"розвантаження (пункт {n})",
                        "Розвантажено" => $"розвантажено (пункт {n})",
                        _ => status
                    }
                };

                var plates = o.OrderAssignments.Select(a => a.Vehicle.LicensePlate).Distinct().ToList();
                if (plates.Count == 0 && !string.IsNullOrWhiteSpace(o.AssignedVehiclePlate))
                    plates.AddRange(o.AssignedVehiclePlate.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

                foreach (var plate in plates)
                {
                    // Водій саме цієї машини (в замовленні їх може бути декілька)
                    var drivers = o.OrderAssignments
                        .Where(a => a.Vehicle.LicensePlate == plate)
                        .Select(a => a.Driver.FullName)
                        .Distinct()
                        .ToList();
                    if (drivers.Count == 0)
                        drivers = o.OrderAssignments.Select(a => a.Driver.FullName).Distinct().ToList();

                    result[plate] = new VehicleLive
                    {
                        OrderId = live.OrderId,
                        CargoName = live.CargoName,
                        Customer = live.Customer,
                        DriverName = string.Join(", ", drivers),
                        Label = live.Label,
                        Stopped = live.Stopped,
                        Suspended = live.Suspended,
                        CurrentIndex = live.CurrentIndex,
                        Points = live.Points
                    };
                }
            }

            return result;
        }
        public List<HistoryOrder> GetOrders()
        {
            using var db = _contextFactory.CreateDbContext();

            var orders = db.Orders
                .AsNoTracking()
                .AsSplitQuery()
                .Include(o => o.OrderPoints)
                .Include(o => o.OrderAssignments).ThenInclude(a => a.Driver)
                .Include(o => o.OrderAssignments).ThenInclude(a => a.Vehicle)
                .Include(o => o.OrderStatusHistories)
                .OrderByDescending(o => o.Id)
                .ToList();

            return orders.Select(o =>
            {
                var steps = o.OrderStatusHistories.OrderBy(h => h.ChangedAt).ToList();

                var points = o.OrderPoints
                    .OrderBy(p => p.SequenceNumber)
                    .Select(p => new HistoryPoint
                    {
                        IsLoading = IsLoadingPoint(p.PointType),
                        Address = p.Address,
                        Status = string.IsNullOrWhiteSpace(p.Status) ? "Нова" : p.Status
                    })
                    .ToList();

                // Старі замовлення без пунктів у БД: беремо адреси з рядків
                if (points.Count == 0)
                {
                    points.AddRange(Split(o.LoadingPoints).Select(a => new HistoryPoint { IsLoading = true, Address = a }));
                    points.AddRange(Split(o.DeliveryPoints).Select(a => new HistoryPoint { IsLoading = false, Address = a }));
                }

                var plates = o.OrderAssignments.Select(a => a.Vehicle.LicensePlate).Distinct().ToList();
                if (plates.Count == 0 && !string.IsNullOrWhiteSpace(o.AssignedVehiclePlate))
                    plates.Add(o.AssignedVehiclePlate);

                return new HistoryOrder
                {
                    Id = o.Id,
                    At = steps.Count > 0 ? steps[0].ChangedAt : null,
                    CustomerCompany = o.CustomerCompany,
                    CustomerName = o.CustomerName,
                    CargoName = o.CargoName,
                    WeightTons = o.WeightTons,
                    VolumeM3 = o.VolumeM3,
                    Drivers = o.OrderAssignments.Select(a => a.Driver.FullName).Distinct().ToList(),
                    Plates = plates,
                    Status = o.Status,
                    Points = points,
                    Steps = steps.Select(h => new HistoryStep
                    {
                        At = h.ChangedAt,
                        Text = h.PreviousStatus == h.NewStatus || string.IsNullOrEmpty(h.PreviousStatus)
                            ? h.Comment
                            : $"{h.PreviousStatus} → {h.NewStatus}: {h.Comment}"
                    }).ToList()
                };
            }).ToList();
        }

        // Журнал подій: зміни статусів замовлень і повідомлення водіїв
        public List<HistoryEvent> GetEvents()
        {
            using var db = _contextFactory.CreateDbContext();

            var statuses = db.OrderStatusHistories
                .AsNoTracking()
                .OrderByDescending(h => h.ChangedAt)
                .Take(200)
                .Select(h => new { h.OrderId, h.PreviousStatus, h.NewStatus, h.ChangedAt, h.Comment })
                .ToList();

            var reports = db.DriverReports
                .AsNoTracking()
                .OrderByDescending(r => r.CreatedAt)
                .Take(200)
                .Select(r => new { r.Driver.FullName, r.OrderId, r.ReportType, r.Description, r.CreatedAt })
                .ToList();

            var events = new List<HistoryEvent>();

            events.AddRange(statuses.Select(h => new HistoryEvent
            {
                At = h.ChangedAt,
                Type = "status",
                TypeLabel = "СТАТУС",
                Title = h.PreviousStatus == h.NewStatus || string.IsNullOrEmpty(h.PreviousStatus)
                    ? $"Замовлення №{h.OrderId}: {h.NewStatus}"
                    : $"Замовлення №{h.OrderId}: {h.PreviousStatus} → {h.NewStatus}",
                Description = h.Comment
            }));

            events.AddRange(reports.Select(r => new HistoryEvent
            {
                At = r.CreatedAt,
                Type = "msg",
                TypeLabel = "ПОВІДОМЛЕННЯ",
                Title = $"Повідомлення від {r.FullName} ({r.ReportType})" + (r.OrderId.HasValue ? $" · замовлення №{r.OrderId}" : ""),
                Description = r.Description.Replace("\n", " — ")
            }));

            return events.OrderByDescending(e => e.At).Take(300).ToList();
        }

        // Історія застосування експертних правил
        public List<HistoryRule> GetRules()
        {
            using var db = _contextFactory.CreateDbContext();

            return db.RuleExecutionHistories
                .AsNoTracking()
                .OrderByDescending(r => r.ExecutedAt)
                .Take(200)
                .Select(r => new HistoryRule
                {
                    At = r.ExecutedAt,
                    Threat = r.ExpertRule.ThreatType,
                    RuleName = r.TriggerDescription,
                    Recommendation = r.ExpertRule.ActionRecommendation,
                    Target = r.Details,
                    Result = r.WasSuccessful ? "Виконано" : "Не виконано"
                })
                .ToList();
        }
    }
}