namespace FastClip.Infrastructure;

internal interface IErrorLogger
{
    void Log(string operationId, Exception ex);
}
