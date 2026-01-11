using CeraRegularize.Services;
using System;
using Media = System.Windows.Media;

namespace CeraRegularize.Pages
{
    public partial class ProfilePage : System.Windows.Controls.UserControl
    {
        public event EventHandler? LogoutRequested;

        public ProfilePage()
        {
            InitializeComponent();
            LogoutButton.Click += (_, _) => LogoutRequested?.Invoke(this, EventArgs.Empty);
        }

        public void SetProfileSummary(ProfileSummary? summary)
        {
            if (summary == null)
            {
                ClearProfile();
                SetStatus("Profile not available.", true);
                return;
            }

            EmployeeNameValue.Text = string.IsNullOrWhiteSpace(summary.EmployeeName) ? "--" : summary.EmployeeName;
            EmployeeIdValue.Text = string.IsNullOrWhiteSpace(summary.EmployeeId) ? "--" : summary.EmployeeId;
            DesignationValue.Text = string.IsNullOrWhiteSpace(summary.Designation) ? "--" : summary.Designation;
            ReportingManagerValue.Text = string.IsNullOrWhiteSpace(summary.ReportingManager) ? "--" : summary.ReportingManager;
            SetStatus("Profile loaded.", false);
        }

        public void SetStatus(string message, bool isError)
        {
            ProfileStatusText.Text = message;
            ProfileStatusText.Foreground = isError
                ? ResolveBrush(null, "#EF4444")
                : ResolveBrush("MutedTextBrush", "#6B7280");
        }

        public void ClearProfile()
        {
            EmployeeNameValue.Text = "--";
            EmployeeIdValue.Text = "--";
            DesignationValue.Text = "--";
            ReportingManagerValue.Text = "--";
        }

        private static Media.Brush ResolveBrush(string? resourceKey, string fallbackHex)
        {
            if (!string.IsNullOrWhiteSpace(resourceKey))
            {
                try
                {
                    if (System.Windows.Application.Current?.Resources[resourceKey] is Media.Brush resourceBrush)
                    {
                        return resourceBrush;
                    }
                }
                catch
                {
                }
            }

            return (Media.Brush)new Media.BrushConverter().ConvertFromString(fallbackHex)!;
        }
    }
}
