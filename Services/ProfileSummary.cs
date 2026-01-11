namespace CeraRegularize.Services
{
    public sealed class ProfileSummary
    {
        public string EmployeeName { get; set; } = string.Empty;
        public string EmployeeId { get; set; } = string.Empty;
        public string Designation { get; set; } = string.Empty;
        public string ReportingManager { get; set; } = string.Empty;

        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(EmployeeName)
            && string.IsNullOrWhiteSpace(EmployeeId)
            && string.IsNullOrWhiteSpace(Designation)
            && string.IsNullOrWhiteSpace(ReportingManager);
    }
}
