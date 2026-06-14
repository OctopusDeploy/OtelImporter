namespace OtelImporter.Export;

internal sealed class TraceExportException : Exception
{
    public TraceExportException(string message) : base(message)
    {
    }

    public TraceExportException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
