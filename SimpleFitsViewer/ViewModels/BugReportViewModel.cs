using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleFitsViewer.Services;

namespace SimpleFitsViewer.ViewModels;

/// <summary>Backs the Submit Feedback form. Ported from StarFix's BugReportViewModel -- posts via
/// BugReportService to the shared Cloudflare Worker, which files the GitHub issue on FluxLab's
/// behalf.</summary>
public partial class BugReportViewModel : ViewModelBase
{
    [ObservableProperty] private string _reportType   = "Bug Report";
    [ObservableProperty] private string _summary      = "";
    [ObservableProperty] private string _description  = "";
    [ObservableProperty] private string _email        = "";
    [ObservableProperty] private string _statusText   = "";
    [ObservableProperty] private bool   _isSubmitting = false;
    [ObservableProperty] private bool   _isSubmitted  = false;

    public string   Version     => AppVersion.Version;
    public string   OsName      => DetectOs();
    public string[] ReportTypes { get; } = ["Bug Report", "Feature Request"];

    public Action? CloseCallback { get; set; }

    [RelayCommand]
    private void Cancel() => CloseCallback?.Invoke();

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task Submit()
    {
        IsSubmitting = true;
        StatusText   = "Submitting…";
        try
        {
            await BugReportService.SubmitAsync(ReportType, Summary, Description, Email, Version, OsName);
            IsSubmitted = true;
            StatusText  = "Thank you — your report has been submitted.";
        }
        catch (Exception ex)
        {
            DiagnosticsLog.LogException("Submitting feedback", ex);
            StatusText = $"Submission failed: {ex.Message}";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private bool CanSubmit() =>
        !string.IsNullOrWhiteSpace(Description) &&
        !string.IsNullOrWhiteSpace(Email) &&
        !IsSubmitting && !IsSubmitted;

    partial void OnDescriptionChanged(string value) => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnEmailChanged(string value)        => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnIsSubmittingChanged(bool value)   => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnIsSubmittedChanged(bool value)    => SubmitCommand.NotifyCanExecuteChanged();

    private static string DetectOs()
    {
        if (OperatingSystem.IsWindows())
            return Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";
        if (OperatingSystem.IsMacOS()) return "macOS";
        if (OperatingSystem.IsLinux()) return "Linux";
        return RuntimeInformation.OSDescription;
    }
}
