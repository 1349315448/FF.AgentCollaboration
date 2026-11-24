using System.Text.RegularExpressions;
using FF.AgentCollaboration.Domain;
using Microsoft.AspNetCore.Http;

namespace FF.AgentCollaboration.Presentation.Http;

internal static class HttpErrors
{
    internal static int Status(WorkbenchFailure failure) => failure switch
    {
        WorkbenchFailure.Forbidden => StatusCodes.Status403Forbidden,
        WorkbenchFailure.NotFound => StatusCodes.Status404NotFound,
        WorkbenchFailure.Conflict => StatusCodes.Status409Conflict,
        WorkbenchFailure.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest
    };

    internal static string Error(Exception error)
    {
        if (error is OperationCanceledException) return "连接已取消或超时";
        var safe = Regex.Replace(error.Message, @"[a-fA-F0-9]{64}|(?:github_pat_|ghp_|Bearer )[A-Za-z0-9_.-]+", "[已隐藏]");
        return safe[..Math.Min(600, safe.Length)];
    }
}
