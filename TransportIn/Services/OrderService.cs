
using Microsoft.EntityFrameworkCore;
using TransportIn.Data;
using TransportIn.Models;
using OrderEntity = TransportIn.Data.Entities.Order;
using OrderModel = TransportIn.Models.Order;

namespace TransportIn.Services
{
    public class OrderDraft
    {
        public int Id { get; set; }

        public string CustomerCompany { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string CustomerPhone { get; set; } = "";
        public string CustomerEmail { get; set; } = "";

        public string Status { get; set; } = "Нове";
        public string AssignedVehiclePlate { get; set; } = "";
        public string AssignedDriver { get; set; } = "";
        public List<CargoDraft> Cargos { get; set; } = new();
        public List<PointDraft> Points { get; set; } = new();
    }

    public class CargoDraft
    {
        public string Name { get; set; } = "";
        public int Quantity { get; set; } = 1;
        public double WeightTons { get; set; }
        public double VolumeM3 { get; set; }
        public bool NeedsRefrigerator { get; set; }
        public double? TempMin { get; set; }
        public double? TempMax { get; set; }
        public string SpecialConditions { get; set; } = "";
    }

    public class PointDraft
    {
        public bool IsLoading { get; set; }
        public string Address { get; set; } = "";
        public string ContactName { get; set; } = "";
        public string ContactPhone { get; set; } = "";

        public string Status { get; set; } = "";
    }

    public partial class OrderService
    {
        private readonly IDbContextFactory<AppDbContext> _contextFactory;
        private readonly EmergencyService _emergency;

        public OrderService(IDbContextFactory<AppDbContext> contextFactory, EmergencyService emergency)
        {
            _contextFactory = contextFactory;
            _emergency = emergency;
        }

        public event Action? OnChange;

        public List<OrderModel> GetAllOrders()
        {
            using var db = _contextFactory.CreateDbContext();

            var query = db.Orders
                .AsNoTracking()
                .AsSplitQuery()
                .Include(order => order.OrderPoints)
                .Include(order => order.OrderCargos)
                    .ThenInclude(orderCargo => orderCargo.Cargo)
                .Include(order => order.OrderAssignments)
                    .ThenInclude(assignment => assignment.Vehicle)
                .OrderBy(order => order.Id);

            Console.WriteLine("ПОЛНЫЙ SQL ЗАПРОС:");
            Console.WriteLine(query.ToQueryString());

            var orders = query.ToList();

            return orders.Select(order =>
            {
                var points = order.OrderPoints
                    .OrderBy(point => point.SequenceNumber)
                    .ToList();

                var cargoLinks = order.OrderCargos.ToList();

                var loadingPoints = points
                    .Where(point => IsLoadingPoint(point.PointType))
                    .Select(point => point.Address)
                    .Where(address => !string.IsNullOrWhiteSpace(address))
                    .ToList();

                var deliveryPoints = points
                    .Where(point => !IsLoadingPoint(point.PointType))
                    .Select(point => point.Address)
                    .Where(address => !string.IsNullOrWhiteSpace(address))
                    .ToList();

                var cargoName = cargoLinks.Count == 0
                    ? order.CargoName
                    : string.Join(", ", cargoLinks.Select(link => link.Cargo.Title));

                var cargoWeightTons = cargoLinks.Sum(
                    link => link.Cargo.WeightKg * link.Quantity) / 1000d;

                var cargoRequiresRefrigerator = cargoLinks.Any(link =>
                    link.Cargo.TransportConditions.Contains("реф", StringComparison.OrdinalIgnoreCase) ||
                    link.Cargo.TransportConditions.Contains("холод", StringComparison.OrdinalIgnoreCase) ||
                    link.Cargo.TransportConditions.Contains("refriger", StringComparison.OrdinalIgnoreCase));

                var latestAssignment = order.OrderAssignments
                    .OrderByDescending(assignment => assignment.AssignedAt)
                    .FirstOrDefault();

                return new OrderModel
                {
                    Id = order.Id,
                    CustomerCompany = order.CustomerCompany,
                    CustomerName = order.CustomerName,
                    CustomerPhone = order.CustomerPhone,
                    CustomerEmail = order.CustomerEmail,
                    CargoName = cargoName,
                    WeightTons = cargoWeightTons > 0
                        ? cargoWeightTons
                        : order.WeightTons,
                    VolumeM3 = order.VolumeM3,
                    RequiresRefrigerator = cargoLinks.Count > 0
                        ? cargoRequiresRefrigerator
                        : order.RequiresRefrigerator,
                    LoadingPoints = loadingPoints.Count > 0
                        ? loadingPoints
                        : SplitPoints(order.LoadingPoints),
                    DeliveryPoints = deliveryPoints.Count > 0
                        ? deliveryPoints
                        : SplitPoints(order.DeliveryPoints),
                    AssignedVehiclePlate = string.IsNullOrWhiteSpace(order.AssignedVehiclePlate)
    ? (latestAssignment?.Vehicle.LicensePlate ?? "")
    : order.AssignedVehiclePlate,
                    Status = order.Status
                };
            }).ToList();
        }

        public void AddOrder(OrderModel order)
        {
            using var db = _contextFactory.CreateDbContext();

            var entity = new OrderEntity
            {
                CustomerCompany = order.CustomerCompany,
                CustomerName = order.CustomerName,
                CustomerPhone = order.CustomerPhone,
                CustomerEmail = order.CustomerEmail,
                CargoName = order.CargoName,
                WeightTons = order.WeightTons,
                VolumeM3 = order.VolumeM3,
                RequiresRefrigerator = order.RequiresRefrigerator,
                LoadingPoints = JoinPoints(order.LoadingPoints),
                DeliveryPoints = JoinPoints(order.DeliveryPoints),
                AssignedVehiclePlate = order.AssignedVehiclePlate,
                Status = order.Status
            };

            db.Orders.Add(entity);
            db.SaveChanges();

            order.Id = entity.Id;
            NotifyStateChanged();
        }

        public List<Vehicle> GetSuitableVehicles(
            OrderModel order,
            List<Vehicle> allVehicles)
        {
            return allVehicles.Where(vehicle =>
                vehicle.CapacityTons >= order.WeightTons &&
                vehicle.VolumeM3 >= order.VolumeM3 &&
                (!order.RequiresRefrigerator || vehicle.HasRefrigerator)
            ).ToList();
        }

        public void AssignVehicle(int orderId, string licensePlate)
        {
            using var db = _contextFactory.CreateDbContext();

            var order = db.Orders.FirstOrDefault(item => item.Id == orderId);
            if (order == null)
                return;

            order.AssignedVehiclePlate = licensePlate;
            order.Status = "Призначено транспорт";

            db.SaveChanges();
            NotifyStateChanged();
        }

        public void UpdateStatus(int orderId, string status)
        {
            using var db = _contextFactory.CreateDbContext();

            var order = db.Orders.FirstOrDefault(item => item.Id == orderId);
            if (order == null)
                return;

            order.Status = status;

            db.SaveChanges();
            NotifyStateChanged();
        }

        private static bool IsLoadingPoint(string pointType) =>
            pointType.Contains("load", StringComparison.OrdinalIgnoreCase) ||
            pointType.Contains("pickup", StringComparison.OrdinalIgnoreCase) ||
            pointType.Contains("завантаж", StringComparison.OrdinalIgnoreCase) ||
            pointType.Contains("відправ", StringComparison.OrdinalIgnoreCase);

        private static List<string> SplitPoints(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : value.Split(
                    new[] { '|', ';', '\r', '\n' },
                    StringSplitOptions.TrimEntries |
                    StringSplitOptions.RemoveEmptyEntries
                ).ToList();

        private static string JoinPoints(IEnumerable<string> points) =>
            string.Join(
                " | ",
                points.Where(point => !string.IsNullOrWhiteSpace(point))
                    .Select(point => point.Trim())
            );

        public void AddOrder(OrderDraft draft)
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = db.Database.BeginTransaction();

            try
            {
                var order = new OrderEntity
                {
                    CustomerCompany = draft.CustomerCompany,
                    CustomerName = draft.CustomerName,
                    CustomerPhone = draft.CustomerPhone,
                    CustomerEmail = draft.CustomerEmail,

                    CargoName = string.Join(", ",
                        draft.Cargos.Select(c => c.Name)),

                    WeightTons = draft.Cargos.Sum(
                        c => c.WeightTons * c.Quantity),

                    VolumeM3 = draft.Cargos.Sum(
                        c => c.VolumeM3 * c.Quantity),

                    RequiresRefrigerator =
                        draft.Cargos.Any(c => c.NeedsRefrigerator),

                    LoadingPoints = JoinPoints(
                        draft.Points.Where(p => p.IsLoading)
                            .Select(p => p.Address)),

                    DeliveryPoints = JoinPoints(
                        draft.Points.Where(p => !p.IsLoading)
                            .Select(p => p.Address)),

                    AssignedVehiclePlate = draft.AssignedVehiclePlate,
                    Status = draft.Status
                };

                db.Orders.Add(order);
                db.SaveChanges();

                SaveCargos(db, order.Id, draft.Cargos);
                SavePoints(db, order.Id, draft.Points);

                AddHistory(db, order.Id, "", order.Status, "Замовлення створено");
                db.SaveChanges();

                transaction.Commit();

                draft.Id = order.Id;
                NotifyStateChanged();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public void UpdateOrder(OrderDraft draft)
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = db.Database.BeginTransaction();

            try
            {
                var order = db.Orders
                    .Include(o => o.OrderCargos)
                        .ThenInclude(oc => oc.Cargo)
                    .Include(o => o.OrderPoints)
                    .FirstOrDefault(o => o.Id == draft.Id);

                if (order == null)
                    throw new InvalidOperationException("Замовлення не знайдено.");

                order.CustomerCompany = draft.CustomerCompany;
                order.CustomerName = draft.CustomerName;
                order.CustomerPhone = draft.CustomerPhone;
                order.CustomerEmail = draft.CustomerEmail;

                order.CargoName = string.Join(", ",
                    draft.Cargos.Select(c => c.Name));

                order.WeightTons = draft.Cargos.Sum(
                    c => c.WeightTons * c.Quantity);

                order.VolumeM3 = draft.Cargos.Sum(
                    c => c.VolumeM3 * c.Quantity);

                order.RequiresRefrigerator =
                    draft.Cargos.Any(c => c.NeedsRefrigerator);

                order.LoadingPoints = JoinPoints(
                    draft.Points.Where(p => p.IsLoading)
                        .Select(p => p.Address));

                order.DeliveryPoints = JoinPoints(
                    draft.Points.Where(p => !p.IsLoading)
                        .Select(p => p.Address));

                order.AssignedVehiclePlate = draft.AssignedVehiclePlate;
                order.Status = draft.Status;

                var oldCargos = order.OrderCargos
                    .Select(oc => oc.Cargo)
                    .ToList();

                db.OrderCargos.RemoveRange(order.OrderCargos);
                db.OrderPoints.RemoveRange(order.OrderPoints);

                db.SaveChanges();

                foreach (var cargo in oldCargos)
                {
                    if (!db.OrderCargos.Any(oc => oc.CargoId == cargo.Id))
                        db.Cargos.Remove(cargo);
                }

                db.SaveChanges();

                SaveCargos(db, order.Id, draft.Cargos);
                SavePoints(db, order.Id, draft.Points);

                transaction.Commit();
                NotifyStateChanged();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private void SaveCargos(
            AppDbContext db,
            int orderId,
            List<CargoDraft> cargos)
        {
            foreach (var item in cargos)
            {
                var cargo = new TransportIn.Data.Entities.Cargo
                {
                    Title = item.Name,
                    WeightKg = item.WeightTons * 1000,
                    VolumeM3 = item.VolumeM3,

                    NeedsRefrigerator = item.NeedsRefrigerator,
                    MinTemperatureC = item.TempMin,
                    MaxTemperatureC = item.TempMax,

                    SpecialConditions = item.SpecialConditions,
                    TransportConditions = item.NeedsRefrigerator
                        ? "Рефрижератор"
                        : item.SpecialConditions,

                    SenderName = "",
                    SenderPhone = "",
                    RecipientName = "",
                    RecipientPhone = ""
                };

                db.Cargos.Add(cargo);
                db.SaveChanges();

                db.OrderCargos.Add(
                    new TransportIn.Data.Entities.OrderCargo
                    {
                        OrderId = orderId,
                        CargoId = cargo.Id,
                        Quantity = item.Quantity,
                        Notes = item.SpecialConditions
                    });
            }

            db.SaveChanges();
        }

        private void SavePoints(
            AppDbContext db,
            int orderId,
            List<PointDraft> points)
        {
            for (int i = 0; i < points.Count; i++)
            {
                var item = points[i];

                db.OrderPoints.Add(
                    new TransportIn.Data.Entities.OrderPoint
                    {
                        OrderId = orderId,
                        ContactPersonId = null,

                        PointType = item.IsLoading
                            ? "Завантаження"
                            : "Доставка",

                        Address = item.Address,
                        ContactName = item.ContactName,
                        ContactPhone = item.ContactPhone,

                        SequenceNumber = i + 1,

                        Latitude = null,
                        Longitude = null,
                        PlannedArrival = null,
                        ActualArrival = null,

                        Status = "Нова",
                        Notes = ""
                    });
            }

            db.SaveChanges();
        }
        // ===== Виконання пунктів маршруту (завантаження / розвантаження) =====

        // Повертає пункти замовлення за порядком відвідування.
        // Для старих замовлень, у яких пунктів у БД ще немає, створює їх із рядків LoadingPoints/DeliveryPoints.
        public List<PointProgress> GetPointProgress(int orderId)
        {
            using var db = _contextFactory.CreateDbContext();

            if (!db.OrderPoints.Any(p => p.OrderId == orderId))
                CreatePointsFromOrderStrings(db, orderId);

            return db.OrderPoints
                .AsNoTracking()
                .Where(p => p.OrderId == orderId)
                .OrderBy(p => p.SequenceNumber)
                .AsEnumerable()
                .Select(p => new PointProgress
                {
                    Id = p.Id,
                    OrderId = p.OrderId,
                    SequenceNumber = p.SequenceNumber,
                    IsLoading = IsLoadingPoint(p.PointType),
                    Address = p.Address,
                    ContactName = p.ContactName,
                    ContactPhone = p.ContactPhone,
                    Status = p.Status,
                    ActualArrival = p.ActualArrival
                })
                .ToList();
        }

        // Підтверджує наступний крок пункту. Повертає false, якщо крок недопустимий:
        // пропущено етап, попередній пункт не завершено, замовлення скасоване/призупинене/завершене
        // або цей крок уже підтверджено (подвійний клік).
        public bool SetPointStatus(int pointId, string newStatus)
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = db.Database.BeginTransaction();

            var point = db.OrderPoints.FirstOrDefault(p => p.Id == pointId);
            if (point == null)
                return false;

            var order = db.Orders.FirstOrDefault(o => o.Id == point.OrderId);
            if (order == null || order.Status is "Скасовано" or "Призупинено" or "Завершено")
                return false;

            var points = db.OrderPoints
                .Where(p => p.OrderId == point.OrderId)
                .OrderBy(p => p.SequenceNumber)
                .ToList();

            var index = points.FindIndex(p => p.Id == point.Id);

            // Усі попередні пункти мають бути повністю завершені (водій виїхав з них)
            if (points.Take(index).Any(p => p.Status != "Виїхав"))
                return false;

            var stages = PointWorkflow.GetStages(
                IsLoadingPoint(point.PointType),
                hasNextPoint: index < points.Count - 1);

            var next = PointWorkflow.GetNextStageIndex(stages, point.Status);
            if (next >= stages.Count || stages[next].Status != newStatus)
                return false;

            // Підтвердження виїзду блокується на сервері під час тривоги, перекриття мостів, урагану, снігопаду
            if (stages[next].IsDeparture && PointWorkflow.IsDepartureBlocked(_emergency.CurrentEmergency))
                return false;

            var previous = point.Status;
            point.Status = newStatus;

            if (newStatus == "Прибув")
                point.ActualArrival = DateTime.UtcNow;

            // Перший крок водія переводить замовлення в статус «В роботі»
            if (order.Status != "В роботі")
            {
                AddHistory(db, order.Id, order.Status, "В роботі", "Водій розпочав виконання замовлення");
                order.Status = "В роботі";
            }

            AddHistory(db, order.Id, previous, newStatus,
                $"Пункт №{point.SequenceNumber} ({point.PointType}): {point.Address}");

            db.SaveChanges();
            transaction.Commit();

            NotifyStateChanged();
            return true;
        }

        // Завершує замовлення, коли всі пункти повністю виконані
        public bool CompleteOrder(int orderId)
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = db.Database.BeginTransaction();

            var order = db.Orders.FirstOrDefault(o => o.Id == orderId);
            if (order == null || order.Status is "Скасовано" or "Завершено")
                return false;

            var points = db.OrderPoints
                .Where(p => p.OrderId == orderId)
                .OrderBy(p => p.SequenceNumber)
                .ToList();

            for (int i = 0; i < points.Count; i++)
            {
                var stages = PointWorkflow.GetStages(
                    IsLoadingPoint(points[i].PointType),
                    hasNextPoint: i < points.Count - 1);

                if (PointWorkflow.GetNextStageIndex(stages, points[i].Status) < stages.Count)
                    return false;
            }

            AddHistory(db, order.Id, order.Status, "Завершено", "Водій підтвердив завершення замовлення");
            order.Status = "Завершено";

            db.SaveChanges();
            transaction.Commit();

            NotifyStateChanged();
            return true;
        }

        private static void AddHistory(
            AppDbContext db, int orderId, string previous, string current, string comment)
        {
            db.OrderStatusHistories.Add(new TransportIn.Data.Entities.OrderStatusHistory
            {
                OrderId = orderId,
                PreviousStatus = previous ?? "",
                NewStatus = current,
                ChangedAt = DateTime.UtcNow,
                ChangedByEmployeeId = null,
                Comment = comment.Length > 500 ? comment[..500] : comment
            });
        }

        private static void CreatePointsFromOrderStrings(AppDbContext db, int orderId)
        {
            var order = db.Orders.FirstOrDefault(o => o.Id == orderId);
            if (order == null)
                return;

            var sequence = 1;
            foreach (var address in SplitPoints(order.LoadingPoints))
                db.OrderPoints.Add(CreateLegacyPoint(order, address, "Завантаження", sequence++));
            foreach (var address in SplitPoints(order.DeliveryPoints))
                db.OrderPoints.Add(CreateLegacyPoint(order, address, "Доставка", sequence++));

            try
            {
                db.SaveChanges();
            }
            catch (DbUpdateException)
            {
                // Пункти вже створив інший запит (унікальний індекс OrderId + SequenceNumber)
                db.ChangeTracker.Clear();
            }
        }

        private static TransportIn.Data.Entities.OrderPoint CreateLegacyPoint(
            OrderEntity order, string address, string pointType, int sequence) =>
            new()
            {
                OrderId = order.Id,
                PointType = pointType,
                Address = address,
                ContactName = order.CustomerName,
                ContactPhone = order.CustomerPhone,
                SequenceNumber = sequence,
                Status = PointWorkflow.StatusNew,
                Notes = ""
            };

        // ===== Призначення транспорту й водія, статуси замовлення (менеджер) =====

        // Усі замовлення в повному вигляді: вантажі, пункти з контактами й статусами, авто, водій
        public List<OrderDraft> GetOrderDrafts()
        {
            using var db = _contextFactory.CreateDbContext();

            var orders = db.Orders
                .AsNoTracking()
                .AsSplitQuery()
                .Include(o => o.OrderPoints)
                .Include(o => o.OrderCargos).ThenInclude(oc => oc.Cargo)
                .Include(o => o.OrderAssignments).ThenInclude(a => a.Vehicle)
                .Include(o => o.OrderAssignments).ThenInclude(a => a.Driver)
                .OrderBy(o => o.Id)
                .ToList();

            return orders.Select(order =>
            {
                var latest = order.OrderAssignments
                    .OrderByDescending(a => a.AssignedAt)
                    .FirstOrDefault();

                var draft = new OrderDraft
                {
                    Id = order.Id,
                    CustomerCompany = order.CustomerCompany,
                    CustomerName = order.CustomerName,
                    CustomerPhone = order.CustomerPhone,
                    CustomerEmail = order.CustomerEmail,
                    Status = order.Status,
                    AssignedVehiclePlate = latest?.Vehicle.LicensePlate ?? order.AssignedVehiclePlate ?? "",
                    AssignedDriver = latest?.Driver.FullName ?? ""
                };

                if (order.OrderCargos.Count > 0)
                {
                    foreach (var link in order.OrderCargos)
                    {
                        draft.Cargos.Add(new CargoDraft
                        {
                            Name = link.Cargo.Title,
                            Quantity = link.Quantity,
                            WeightTons = link.Cargo.WeightKg / 1000d,
                            VolumeM3 = link.Cargo.VolumeM3 ?? 0,
                            NeedsRefrigerator = link.Cargo.NeedsRefrigerator,
                            TempMin = link.Cargo.MinTemperatureC,
                            TempMax = link.Cargo.MaxTemperatureC,
                            SpecialConditions = link.Cargo.SpecialConditions ?? ""
                        });
                    }
                }
                else
                {
                    // Старі замовлення без записів у таблицях вантажів
                    draft.Cargos.Add(new CargoDraft
                    {
                        Name = order.CargoName,
                        Quantity = 1,
                        WeightTons = order.WeightTons,
                        VolumeM3 = order.VolumeM3,
                        NeedsRefrigerator = order.RequiresRefrigerator
                    });
                }

                if (order.OrderPoints.Count > 0)
                {
                    foreach (var point in order.OrderPoints.OrderBy(p => p.SequenceNumber))
                    {
                        draft.Points.Add(new PointDraft
                        {
                            IsLoading = IsLoadingPoint(point.PointType),
                            Address = point.Address,
                            ContactName = point.ContactName,
                            ContactPhone = point.ContactPhone,
                            Status = point.Status
                        });
                    }
                }
                else
                {
                    foreach (var address in SplitPoints(order.LoadingPoints))
                        draft.Points.Add(new PointDraft { IsLoading = true, Address = address });
                    foreach (var address in SplitPoints(order.DeliveryPoints))
                        draft.Points.Add(new PointDraft { IsLoading = false, Address = address });
                }

                return draft;
            }).ToList();
        }

        // Призначає автомобіль замовленню. Якщо авто змінюється, призначення водія скидається.
        public bool AssignVehicleToOrder(int orderId, string licensePlate)
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = db.Database.BeginTransaction();

            var order = db.Orders
                .Include(o => o.OrderAssignments)
                .FirstOrDefault(o => o.Id == orderId);

            if (order == null || order.Status is "Скасовано" or "Завершено")
                return false;

            if (!db.Vehicles.Any(v => v.LicensePlate == licensePlate))
                return false;

            if (order.AssignedVehiclePlate != licensePlate)
                db.OrderAssignments.RemoveRange(order.OrderAssignments);

            order.AssignedVehiclePlate = licensePlate;

            AddHistory(db, order.Id, order.Status, "В роботі", $"Призначено транспорт {licensePlate}");
            order.Status = "В роботі";

            db.SaveChanges();
            transaction.Commit();

            NotifyStateChanged();
            return true;
        }

        // Призначає водія. Повертає текст помилки або null, якщо все гаразд.
        public string? AssignDriverToOrder(int orderId, string driverName)
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = db.Database.BeginTransaction();

            var order = db.Orders
                .Include(o => o.OrderAssignments)
                .FirstOrDefault(o => o.Id == orderId);

            if (order == null)
                return "Замовлення не знайдено.";

            if (string.IsNullOrWhiteSpace(order.AssignedVehiclePlate))
                return "Спочатку призначте транспорт.";

            var vehicle = db.Vehicles.FirstOrDefault(v => v.LicensePlate == order.AssignedVehiclePlate);
            if (vehicle == null)
                return "Транспорт не знайдено в автопарку.";

            var driver = db.Employees.FirstOrDefault(e => e.FullName == driverName && e.IsActive);
            if (driver == null)
                return "Водія не знайдено або його деактивовано.";

            var busy = db.OrderAssignments.Any(a =>
                a.DriverId == driver.Id &&
                a.OrderId != orderId &&
                a.Order.Status != "Завершено" &&
                a.Order.Status != "Скасовано");

            if (busy)
                return $"Водій {driverName} вже призначений на інше активне замовлення.";

            db.OrderAssignments.RemoveRange(order.OrderAssignments);

            var now = DateTime.UtcNow;
            db.OrderAssignments.Add(new TransportIn.Data.Entities.OrderAssignment
            {
                OrderId = order.Id,
                VehicleId = vehicle.Id,
                DriverId = driver.Id,
                AssignedAt = now,
                PlannedStart = now,
                PlannedEnd = now.AddHours(8), // орієнтовна тривалість зміни
                Status = "Призначено",
                Notes = ""
            });

            AddHistory(db, order.Id, order.Status, order.Status, $"Призначено водія {driver.FullName}");

            db.SaveChanges();
            transaction.Commit();

            NotifyStateChanged();
            return null;
        }

        public void UnassignDriver(int orderId)
        {
            using var db = _contextFactory.CreateDbContext();

            var items = db.OrderAssignments.Where(a => a.OrderId == orderId).ToList();
            if (items.Count == 0)
                return;

            var status = db.Orders.Where(o => o.Id == orderId).Select(o => o.Status).FirstOrDefault() ?? "";

            db.OrderAssignments.RemoveRange(items);
            AddHistory(db, orderId, status, status, "Скасовано призначення водія");

            db.SaveChanges();
            NotifyStateChanged();
        }

        // Зміна статусу замовлення менеджером (призупинення, відновлення, скасування, завершення)
        public bool SetOrderStatus(int orderId, string newStatus, string comment)
        {
            using var db = _contextFactory.CreateDbContext();

            var order = db.Orders.FirstOrDefault(o => o.Id == orderId);
            if (order == null || order.Status is "Скасовано" or "Завершено")
                return false;

            AddHistory(db, order.Id, order.Status, newStatus, comment);
            order.Status = newStatus;

            db.SaveChanges();
            NotifyStateChanged();
            return true;
        }
        private void NotifyStateChanged()
        {
            OnChange?.Invoke();
        }
    }
}
