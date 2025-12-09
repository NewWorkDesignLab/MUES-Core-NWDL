using UnityEngine;
using System.Diagnostics;
using System.Reflection;

public static class ConsoleMessage
{
    public static void Send(bool debugMode, string message, Color _color)
    {
        if (!debugMode) return;

        StackTrace stackTrace = new StackTrace();
        MethodBase method = stackTrace.GetFrame(1).GetMethod();
        string callerClass = method.DeclaringType.Name;

        string colorCode = ColorUtility.ToHtmlStringRGB(_color);
        UnityEngine.Debug.Log($"<b>[{callerClass}]</b> | <color=#{colorCode}>{message}</color>");
    }
}
