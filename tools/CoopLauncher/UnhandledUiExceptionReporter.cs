namespace CoopLauncher;

internal sealed class UnhandledUiExceptionReporter
{
    private readonly Action<string> writeLog;
    private readonly Action<string, string> showError;
    private readonly string logPath;

    public UnhandledUiExceptionReporter(
        Action<string> writeLog,
        Action<string, string> showError,
        string logPath)
    {
        this.writeLog = writeLog;
        this.showError = showError;
        this.logPath = logPath;
    }

    public bool Report(Exception exception)
    {
        writeLog($"UNHANDLED UI EXCEPTION: {exception}");
        showError(
            "Calradia Co-op — launcher error",
            $"The launcher hit an error:\n\n{exception.Message}\n\nDetails were written to:\n{logPath}");

        // Dispatcher faults can leave WPF in an invalid render/layout state. Report the cause, then
        // let the normal unhandled-exception path terminate instead of continuing a ghost window.
        return false;
    }
}
