using Serilog.Core;
using Serilog.Events;

namespace PdfWatcher.Gui;

public class AppStateSink : ILogEventSink
{
    private readonly AppState _appState;

    public AppStateSink(AppState appState) => _appState = appState;

    public void Emit(LogEvent logEvent)
    {
        var level = logEvent.Level switch
        {
            LogEventLevel.Verbose     => "VRB",
            LogEventLevel.Debug       => "DBG",
            LogEventLevel.Information => "INF",
            LogEventLevel.Warning     => "WRN",
            LogEventLevel.Error       => "ERR",
            LogEventLevel.Fatal       => "FTL",
            _                         => "???"
        };
        _appState.AddLogEntry(new LogEntry(logEvent.Timestamp.DateTime, level, logEvent.RenderMessage()));
    }
}
