using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;

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

    /// <summary>
    /// Appends a timestamped line. Safe to call from any thread: Entries is bound as the
    /// Diagnostics window's ItemsSource, and mutating a bound ObservableCollection off the UI
    /// thread throws in Avalonia -- so off-thread calls are marshalled. Callers that log from
    /// background work (the unobserved-task handler in Program.cs, TargetResolverService's HTTP
    /// continuations) would otherwise crash, but only while that window happened to be open.
    /// </summary>
    public static void Log(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        if (Dispatcher.UIThread.CheckAccess()) Entries.Add(line);
        else Dispatcher.UIThread.Post(() => Entries.Add(line));
    }

    public static void LogException(string context, Exception ex)
        => Log($"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
}
