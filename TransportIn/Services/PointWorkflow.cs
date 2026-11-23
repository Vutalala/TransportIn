namespace TransportIn.Services
{
    // Один крок виконання пункту маршруту
    public record PointStage(string Status, string Label, string ButtonLabel, bool IsDeparture = false);

    // Пункт маршруту разом зі станом його виконання (для робочого місця водія)
    public class PointProgress
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public int SequenceNumber { get; set; }
        public bool IsLoading { get; set; }
        public string Address { get; set; } = "";
        public string ContactName { get; set; } = "";
        public string ContactPhone { get; set; } = "";
        public string Status { get; set; } = PointWorkflow.StatusNew;
        public DateTime? ActualArrival { get; set; }
    }

    public static class PointWorkflow
    {
        public const string StatusNew = "Нова";

        // Послідовність кроків для одного пункту.
        // Крок «Виїхав» є лише тоді, коли після цього пункту є наступний.
        public static List<PointStage> GetStages(bool isLoading, bool hasNextPoint)
        {
            var stages = new List<PointStage>
            {
                new PointStage(
                    "Прибув",
                    isLoading ? "Прибув на пункт завантаження" : "Прибув на пункт доставки",
                    isLoading ? "Підтвердити прибуття" : "Прибув на розвантаження"),

                isLoading
                    ? new PointStage("Завантажується", "Початок завантаження", "Почати завантаження")
                    : new PointStage("Розвантажується", "Початок розвантаження", "Почати розвантаження"),

                isLoading
                    ? new PointStage("Завантажено", "Завершення завантаження", "Завершити завантаження")
                    : new PointStage("Розвантажено", "Завершення розвантаження", "Завершити розвантаження")
            };

            if (hasNextPoint)
                stages.Add(new PointStage("Виїхав", "Виїзд до наступного пункту", "Підтвердити виїзд", true));

            return stages;
        }

        // Індекс наступного кроку за поточним статусом пункту.
        // Якщо результат дорівнює stages.Count, пункт повністю завершено.
        public static int GetNextStageIndex(List<PointStage> stages, string? currentStatus)
        {
            if (string.IsNullOrWhiteSpace(currentStatus) || currentStatus == StatusNew)
                return 0;

            var index = stages.FindIndex(s => s.Status == currentStatus);
            return index < 0 ? 0 : index + 1;
        }
    }
}