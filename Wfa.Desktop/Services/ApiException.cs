using System;

namespace Wfa.Desktop.Services;

/// <summary>
/// Raised when the FastAPI backend returns a non-success status; carries the raw response body.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string responseBody)
        : base($"HTTP {statusCode}: {responseBody}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public int StatusCode { get; }

    /// <summary>Raw JSON/text from FastAPI (validation errors, detail message, etc.).</summary>
    public string ResponseBody { get; }
}
