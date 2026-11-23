using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TransportIn.Data;
using TransportIn.Models;

namespace TransportIn.Services
{
    public class FleetService
    {
        private readonly IDbContextFactory<AppDbContext> _contextFactory;

        public FleetService(IDbContextFactory<AppDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        public event Action? OnChange;

        // Допустимі технічні стани транспорту. Лише «Справний» дозволяє призначати авто на замовлення.
        public const string OperationalStatus = "Справний";
        public static readonly string[] VehicleStatuses = { OperationalStatus, "У ремонті", "Несправний" };

        public List<Vehicle> GetAllVehicles()
        {
            using var context = _contextFactory.CreateDbContext();

            return context.Vehicles
                .AsNoTracking()
                .Select(v => new Vehicle
                {
                    Id = v.Id,
                    BrandModel = v.BrandModel,
                    LicensePlate = v.LicensePlate,
                    VinCode = v.VinCode,
                    BodyType = v.BodyType,
                    CapacityTons = v.CapacityTons,
                    VolumeM3 = v.VolumeM3,
                    HasRefrigerator = v.HasRefrigerator,
                    AssignedDriver = v.AssignedDriver,
                    Status = v.Status,
                    Lat = v.Lat,
                    Lng = v.Lng
                })
                .ToList();
        }

        public async Task<List<Vehicle>> GetAllVehiclesAsync()
        {
            await using var context =
                await _contextFactory.CreateDbContextAsync();

            return await context.Vehicles
                .AsNoTracking()
                .Select(v => new Vehicle
                {
                    Id = v.Id,
                    BrandModel = v.BrandModel,
                    LicensePlate = v.LicensePlate,
                    VinCode = v.VinCode,
                    BodyType = v.BodyType,
                    CapacityTons = v.CapacityTons,
                    VolumeM3 = v.VolumeM3,
                    HasRefrigerator = v.HasRefrigerator,
                    AssignedDriver = v.AssignedDriver,
                    Status = v.Status,
                    Lat = v.Lat,
                    Lng = v.Lng
                })
                .ToListAsync();
        }

        public void AddVehicle(Vehicle vehicle)
        {
            using var context = _contextFactory.CreateDbContext();

            var dbVehicle = new TransportIn.Data.Entities.Vehicle
            {
                BrandModel = vehicle.BrandModel,
                LicensePlate = vehicle.LicensePlate,
                VinCode = vehicle.VinCode,
                BodyType = vehicle.BodyType,
                CapacityTons = vehicle.CapacityTons,
                VolumeM3 = vehicle.VolumeM3,
                HasRefrigerator = vehicle.HasRefrigerator,
                AssignedDriver = vehicle.AssignedDriver,
                Status = string.IsNullOrWhiteSpace(vehicle.Status) || vehicle.Status == "На складі"
                    ? OperationalStatus
                    : vehicle.Status,
                Lat = vehicle.Lat,
                Lng = vehicle.Lng
            };

            context.Vehicles.Add(dbVehicle);
            context.SaveChanges();

            vehicle.Id = dbVehicle.Id;

            NotifyStateChanged();
        }

        // Змінює технічний стан авто. Повертає номери активних замовлень, на які авто призначене
        // (адміністратор бачить попередження, але призначення автоматично не скасовуються).
        public List<int> SetVehicleStatus(int id, string status)
        {
            if (!VehicleStatuses.Contains(status))
                throw new ArgumentException("Невідомий технічний стан транспорту.");

            using var context = _contextFactory.CreateDbContext();

            var vehicle = context.Vehicles.FirstOrDefault(v => v.Id == id);
            if (vehicle == null)
                throw new InvalidOperationException("ТЗ не знайдено.");

            vehicle.Status = status;
            context.SaveChanges();

            var plate = vehicle.LicensePlate;
            var activeOrders = context.Orders
                .AsNoTracking()
                .Where(o => (o.Status == "В роботі" || o.Status == "Призупинено")
                            && o.AssignedVehiclePlate.Contains(plate))
                .Select(o => o.Id)
                .ToList();

            NotifyStateChanged();
            return activeOrders;
        }

        public void DeleteVehicle(int id)
        {
            using var context = _contextFactory.CreateDbContext();

            var vehicle = context.Vehicles.FirstOrDefault(v => v.Id == id);

            if (vehicle != null)
            {
                context.Vehicles.Remove(vehicle);
                context.SaveChanges();

                NotifyStateChanged();
            }
        }

        private void NotifyStateChanged()
        {
            OnChange?.Invoke();
        }
    }
}