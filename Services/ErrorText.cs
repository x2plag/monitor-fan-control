namespace MonitorFanControl;

internal static class ErrorText
{
    // Only exceptions raised with a user-facing message are shown verbatim.
    public static string Describe(Exception exception) => exception switch
    {
        FanToolException or TimeoutException or ArgumentOutOfRangeException => exception.Message,
        _ => $"An unexpected error occurred ({exception.GetType().Name})."
    };
}
