using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransportIn.Migrations
{
    /// <inheritdoc />
    public partial class SyncModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Cargos_TemperatureRange",
                table: "Cargos");

            migrationBuilder.RenameColumn(
                name: "MinTemperatureC",
                table: "Cargos",
                newName: "TempMin");

            migrationBuilder.RenameColumn(
                name: "MaxTemperatureC",
                table: "Cargos",
                newName: "TempMax");

            migrationBuilder.AddColumn<bool>(
                name: "NeedsRefrigerator",
                table: "Cargos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SpecialConditions",
                table: "Cargos",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cargos_TemperatureRange",
                table: "Cargos",
                sql: "\"TempMin\" IS NULL OR \"TempMax\" IS NULL OR \"TempMin\" <= \"TempMax\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Cargos_TemperatureRange",
                table: "Cargos");

            migrationBuilder.DropColumn(
                name: "NeedsRefrigerator",
                table: "Cargos");

            migrationBuilder.DropColumn(
                name: "SpecialConditions",
                table: "Cargos");

            migrationBuilder.RenameColumn(
                name: "TempMin",
                table: "Cargos",
                newName: "MinTemperatureC");

            migrationBuilder.RenameColumn(
                name: "TempMax",
                table: "Cargos",
                newName: "MaxTemperatureC");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cargos_TemperatureRange",
                table: "Cargos",
                sql: "\"MinTemperatureC\" IS NULL OR \"MaxTemperatureC\" IS NULL OR \"MinTemperatureC\" <= \"MaxTemperatureC\"");
        }
    }
}
