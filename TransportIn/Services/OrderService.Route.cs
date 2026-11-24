using Microsoft.EntityFrameworkCore;

namespace TransportIn.Services
{
    // Пункт маршруту з координатами (для карти водія)
    public class RoutePointInfo
    {
        public int Id { get; set; }
        public int Seq { get; set; }
        public bool IsLoading { get; set; }
        public string Address { get; set; } = "";
        public string Status { get; set; } = "Нова";
        public double? Lat { get; set; }
        public double? Lng { get; set; }
    }

    // Дані для карти маршруту водія
    public partial class OrderService
    {
        // Пункти замовлення за порядком відвідування разом із координатами (якщо вони вже збережені)
        public List<RoutePointInfo> GetRoutePoints(int orderId)
        {
            using var db = _contextFactory.CreateDbContext();

            return db.OrderPoints
                .AsNoTracking()
                .Where(p => p.OrderId == orderId)
                .OrderBy(p => p.SequenceNumber)
                .AsEnumerable()
                .Select(p => new RoutePointInfo
                {
                    Id = p.Id,
                    Seq = p.SequenceNumber,
                    IsLoading = IsLoadingPoint(p.PointType),
                    Address = p.Address,
                    Status = string.IsNullOrWhiteSpace(p.Status) ? "Нова" : p.Status,
                    Lat = p.Latitude,
                    Lng = p.Longitude
                })
                .ToList();
        }

        // Зберігає координати пункту після геокодування, щоб не робити запит повторно.
        // Подію оновлення не викликає, інакше сторінки перемальовувалися б безкінечно.
        public void SavePointCoordinates(int pointId, double lat, double lng)
        {
            using var db = _contextFactory.CreateDbContext();

            var point = db.OrderPoints.FirstOrDefault(p => p.Id == pointId);
            if (point == null)
                return;

            point.Latitude = lat;
            point.Longitude = lng;
            db.SaveChanges();
        }

        // Марка й модель автомобіля за держномером (для блоку «Транспортний засіб» у водія)
        public string GetVehicleModel(string? licensePlate)
        {
            if (string.IsNullOrWhiteSpace(licensePlate))
                return "Не призначено";

            using var db = _contextFactory.CreateDbContext();

            return db.Vehicles
                .AsNoTracking()
                .Where(v => v.LicensePlate == licensePlate)
                .Select(v => v.BrandModel)
                .FirstOrDefault() ?? "Не вказано";
        }
    }
}