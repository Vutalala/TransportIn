using Microsoft.EntityFrameworkCore;
using TransportIn.Data;

namespace TransportIn.Services
{
    // Один призначений автомобіль замовлення та його водій (водія може ще не бути)
    public class VehicleSlot
    {
        public string Plate { get; set; } = "";
        public string Driver { get; set; } = "";
    }

    // Замовлення разом зі списком призначених автомобілів і водіїв
    public class ManagerOrder
    {
        public OrderDraft Draft { get; set; } = new();
        public List<VehicleSlot> Vehicles { get; set; } = new();
    }

    // Розділ вантажу між кількома автомобілями (крок 5 алгоритму ТЗ).
    // Номери призначених авто зберігаються в Orders.AssignedVehiclePlate через "; ",
    // а водій кожного авто — окремим рядком у OrderAssignments.
    public partial class OrderService
    {
        private const string PlateSeparator = "; ";

        private static List<string> SplitPlates(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        // Замовлення для робочого місця менеджера: дані замовлення + авто з водіями
        public List<ManagerOrder> GetManagerOrders()
        {
            var drafts = GetOrderDrafts();

            using var db = _contextFactory.CreateDbContext();

            var plates = db.Orders
                .AsNoTracking()
                .Select(o => new { o.Id, o.AssignedVehiclePlate })
                .ToList()
                .ToDictionary(x => x.Id, x => x.AssignedVehiclePlate ?? "");

            var assignments = db.OrderAssignments
                .AsNoTracking()
                .Select(a => new { a.OrderId, Plate = a.Vehicle.LicensePlate, Driver = a.Driver.FullName })
                .ToList();

            return drafts.Select(d =>
            {
                var slots = SplitPlates(plates.GetValueOrDefault(d.Id)).Select(plate => new VehicleSlot
                {
                    Plate = plate,
                    Driver = assignments
                        .FirstOrDefault(a => a.OrderId == d.Id && a.Plate == plate)?.Driver ?? ""
                }).ToList();

                return new ManagerOrder { Draft = d, Vehicles = slots };
            }).ToList();
        }

        // Технічний стан: автомобіль придатний до рейсу, лише якщо його статус «Справний».
        public static bool IsOperational(string? status) =>
            string.IsNullOrWhiteSpace(status) ||
            status.Trim().Equals("Справний", StringComparison.OrdinalIgnoreCase);

        // Автомобілі, які не можна призначити на замовлення orderId: номер -> причина.
        // Причини: несправність або зайнятість іншим активним замовленням («В роботі», «Призупинено»).
        // Замовлення orderId не рахується, тому його власні авто не блокуються.
        private static Dictionary<string, string> FindUnavailable(AppDbContext db, int orderId)
        {
            var result = new Dictionary<string, string>();

            var vehicles = db.Vehicles.AsNoTracking()
                .Select(v => new { v.LicensePlate, v.Status })
                .ToList();
            foreach (var v in vehicles)
            {
                if (!IsOperational(v.Status))
                    result[v.LicensePlate] = $"Несправний ({v.Status})";
            }

            var active = db.Orders.AsNoTracking()
                .Where(o => o.Id != orderId && (o.Status == "В роботі" || o.Status == "Призупинено"))
                .Select(o => new { o.Id, o.AssignedVehiclePlate })
                .ToList();
            foreach (var o in active)
            {
                foreach (var plate in SplitPlates(o.AssignedVehiclePlate))
                {
                    if (!result.ContainsKey(plate))
                        result[plate] = $"Зайнятий: замовлення №{o.Id}";
                }
            }

            return result;
        }

        // Для інтерфейсу менеджера: які авто недоступні для цього замовлення і чому
        public Dictionary<string, string> GetVehicleUnavailability(int orderId)
        {
            using var db = _contextFactory.CreateDbContext();
            return FindUnavailable(db, orderId);
        }

        // Перевіряє, чи обраних автомобілів разом вистачає для замовлення.
        // Повертає текст помилки або null.
        private static string? CheckCoverage(
            TransportIn.Data.Entities.Order order,
            List<TransportIn.Data.Entities.Vehicle> vehicles)
        {
            var weight = order.WeightTons;
            var volume = order.VolumeM3;
            double refWeight = 0, refVolume = 0;
            var needsRef = order.RequiresRefrigerator;

            if (order.OrderCargos.Count > 0)
            {
                var refCargos = order.OrderCargos.Where(c => c.Cargo.NeedsRefrigerator).ToList();
                refWeight = refCargos.Sum(c => c.Cargo.WeightKg * c.Quantity) / 1000d;
                refVolume = refCargos.Sum(c => (c.Cargo.VolumeM3 ?? 0) * c.Quantity);
                needsRef = refCargos.Count > 0;
            }
            else if (needsRef)
            {
                refWeight = weight;
                refVolume = volume;
            }

            var capacity = vehicles.Sum(v => v.CapacityTons);
            if (capacity < weight)
                return $"Недостатня вантажопідйомність: обрано {capacity:0.##} т, потрібно {weight:0.##} т.";

            var space = vehicles.Sum(v => v.VolumeM3);
            if (space < volume)
                return $"Недостатній об'єм кузова: обрано {space:0.##} м³, потрібно {volume:0.##} м³.";

            if (needsRef)
            {
                var fridge = vehicles.Where(v => v.HasRefrigerator).ToList();
                if (fridge.Count == 0)
                    return "Для цього замовлення потрібен автомобіль із рефрижератором.";
                if (fridge.Sum(v => v.CapacityTons) < refWeight || fridge.Sum(v => v.VolumeM3) < refVolume)
                    return "Рефрижераторів недостатньо для вантажів, що потребують температурного режиму.";
            }

            return null;
        }

        // Активне замовлення конкретного водія (за EmployeeId) і лише його автомобіль.
        // Повертає null, якщо водія не розпізнано або замовлення йому не призначено.
        public TransportIn.Models.Order? GetActiveOrderForDriver(int? employeeId)
        {
            if (employeeId == null)
                return null;

            using var db = _contextFactory.CreateDbContext();

            var assignment = db.OrderAssignments
                .AsNoTracking()
                .Where(a => a.DriverId == employeeId &&
                            (a.Order.Status == "В роботі" || a.Order.Status == "Призупинено"))
                .OrderByDescending(a => a.AssignedAt)
                .Select(a => new { a.OrderId, Plate = a.Vehicle.LicensePlate })
                .FirstOrDefault();

            if (assignment == null)
                return null;

            var order = GetAllOrders().FirstOrDefault(o => o.Id == assignment.OrderId);
            if (order != null)
                order.AssignedVehiclePlate = assignment.Plate;

            return order;
        }

        // Призначає замовленню один або кілька автомобілів. Повертає текст помилки або null.
        // Водії автомобілів, що залишилися в списку, зберігаються; решта призначень скасовується.
        public string? AssignVehiclesToOrder(int orderId, List<string> licensePlates)
        {
            var plates = licensePlates
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct()
                .ToList();

            if (plates.Count == 0)
                return "Оберіть хоча б один транспортний засіб.";

            using var db = _contextFactory.CreateDbContext();
            using var transaction = db.Database.BeginTransaction();

            var order = db.Orders
                .Include(o => o.OrderAssignments)
                .Include(o => o.OrderCargos).ThenInclude(oc => oc.Cargo)
                .FirstOrDefault(o => o.Id == orderId);

            if (order == null)
                return "Замовлення не знайдено.";

            if (order.Status is "Скасовано" or "Завершено")
                return "Замовлення вже завершене або скасоване.";

            var vehicles = db.Vehicles.Where(v => plates.Contains(v.LicensePlate)).ToList();
            if (vehicles.Count != plates.Count)
                return "Частину обраних автомобілів не знайдено в автопарку.";

            // Технічна справність і зайнятість: перевіряємо і на сервері, щоб не обійти через інтерфейс
            var unavailable = FindUnavailable(db, orderId);
            var blocked = plates.Where(unavailable.ContainsKey).ToList();
            if (blocked.Count > 0)
                return "Не можна призначити: " +
                       string.Join("; ", blocked.Select(p => $"{p} — {unavailable[p]}")) + ".";

            var error = CheckCoverage(order, vehicles);
            if (error != null)
                return error;

            // Знімаємо водіїв з автомобілів, яких більше немає в списку
            var removed = order.OrderAssignments
                .Where(a => !vehicles.Any(v => v.Id == a.VehicleId))
                .ToList();
            db.OrderAssignments.RemoveRange(removed);

            order.AssignedVehiclePlate = string.Join(PlateSeparator, plates);

            AddHistory(db, order.Id, order.Status, "В роботі",
                $"Призначено транспорт: {string.Join(", ", plates)}");
            order.Status = "В роботі";

            db.SaveChanges();
            transaction.Commit();

            NotifyStateChanged();
            return null;
        }

        // Призначає водія конкретному автомобілю замовлення. Повертає текст помилки або null.
        public string? AssignDriverToVehicle(int orderId, string licensePlate, string driverName)
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = db.Database.BeginTransaction();

            var order = db.Orders
                .Include(o => o.OrderAssignments)
                .FirstOrDefault(o => o.Id == orderId);

            if (order == null)
                return "Замовлення не знайдено.";

            if (!SplitPlates(order.AssignedVehiclePlate).Contains(licensePlate))
                return "Цей автомобіль не призначено на замовлення.";

            var vehicle = db.Vehicles.FirstOrDefault(v => v.LicensePlate == licensePlate);
            if (vehicle == null)
                return "Транспорт не знайдено в автопарку.";

            var driver = db.Employees.FirstOrDefault(e => e.FullName == driverName && e.IsActive);
            if (driver == null)
                return "Водія не знайдено або його деактивовано.";

            // Водій не може вести два авто одного замовлення чи брати інше активне замовлення
            var busy = db.OrderAssignments.Any(a =>
                a.DriverId == driver.Id &&
                ((a.OrderId != orderId &&
                  a.Order.Status != "Завершено" && a.Order.Status != "Скасовано") ||
                 (a.OrderId == orderId && a.VehicleId != vehicle.Id)));

            if (busy)
                return $"Водій {driverName} вже призначений на інше активне замовлення або авто.";

            db.OrderAssignments.RemoveRange(
                order.OrderAssignments.Where(a => a.VehicleId == vehicle.Id));

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

            AddHistory(db, order.Id, order.Status, order.Status,
                $"Призначено водія {driver.FullName} на авто {licensePlate}");

            db.SaveChanges();
            transaction.Commit();

            NotifyStateChanged();
            return null;
        }

        // Знімає водія з конкретного автомобіля замовлення
        public void UnassignDriverFromVehicle(int orderId, string licensePlate)
        {
            using var db = _contextFactory.CreateDbContext();

            var items = db.OrderAssignments
                .Where(a => a.OrderId == orderId && a.Vehicle.LicensePlate == licensePlate)
                .ToList();
            if (items.Count == 0)
                return;

            var status = db.Orders.Where(o => o.Id == orderId).Select(o => o.Status).FirstOrDefault() ?? "";

            db.OrderAssignments.RemoveRange(items);
            AddHistory(db, orderId, status, status, $"Скасовано призначення водія на авто {licensePlate}");

            db.SaveChanges();
            NotifyStateChanged();
        }
    }
}