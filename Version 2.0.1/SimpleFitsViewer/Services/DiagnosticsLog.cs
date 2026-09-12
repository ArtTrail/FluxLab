using System;
using System.Collections.ObjectModel;

namespace SimpleFitsViewer.Services;

/// <summary>
/// App-wide error/event log for the Tools > Diagnostics view, matching the pattern already
/// used in TransitLab/TransitFinder/VariLab. A plain static store (not part of any ViewModel)
/// since it needs to capture things -- unhandled exceptions, background-thread failures -- that
/// happen outside any single ViewModel's lifetime or DataContext.
/// </summary>
public static class DiagnosticsLog
{
    public static ObservableCollection<string> Entries { get; } = [];

    public static void Log(string message)
        => Entries.Add($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");

    public static void LogException(string context, Exception ex)
        => Log($"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
}
