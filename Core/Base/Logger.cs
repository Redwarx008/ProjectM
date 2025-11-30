namespace Core;
public static class Logger
{
    public delegate void MessageCallback(string message, params object[] args);
    public static MessageCallback? LogInfoFunc { get; set; }

    public static MessageCallback? LogWarnFunc { get; set; }

    public static MessageCallback? LogErrorFunc { get; set; }

    public static MessageCallback? LogDebugFunc { get; set; }

    public static void Info(string message, params object[] args)
    {
        LogInfoFunc?.Invoke(message, args);
    }

    public static void Warn(string message, params object[] args)
    {
        LogWarnFunc?.Invoke(message, args);
    }

    public static void Error(string message, params object[] args)
    {
        LogErrorFunc?.Invoke(message, args);
    }

    public static void Debug(string message, params object[] args)
    {
#if DEBUG
        LogDebugFunc?.Invoke(message, args);
#endif
    }
}