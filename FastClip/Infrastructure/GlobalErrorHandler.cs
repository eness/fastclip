namespace FastClip.Infrastructure;

internal static class GlobalErrorHandler
{
    private static readonly IErrorLogger Logger = new ErrorLogger();

    public static void Handle(string source, Exception exception)
    {
        Logger.Log(source, exception);
    }
}
