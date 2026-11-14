using System;
using KspHarness.Mod.Protocol;
using UnityEngine;

namespace KspHarness.Mod.Events;

/// <summary>Publishes every Unity log message, from any thread, as a <c>log</c> event.</summary>
internal sealed class LogCapture
{
    [ThreadStatic]
    private static bool publishing;

    private readonly EventHub hub;

    public LogCapture(EventHub hub)
    {
        this.hub = hub;
    }

    public void Start() => Application.logMessageReceivedThreaded += OnLog;

    public void Stop() => Application.logMessageReceivedThreaded -= OnLog;

    private void OnLog(string message, string stackTrace, LogType type)
    {
        // Something logged while publishing (a failing send) must not feed back into the log
        if (publishing)
            return;

        publishing = true;
        try
        {
            hub.Publish(
                "log",
                new JsonObject
                {
                    { "level", type.ToString() },
                    { "message", message },
                    { "stack", stackTrace ?? "" },
                    { "mods", ModAttribution.Find(stackTrace).ConvertAll(mod => (object)mod) },
                }
            );
        }
        catch (Exception)
        {
            // Throwing from a log callback would log again; dropping the event is the lesser harm
        }
        finally
        {
            publishing = false;
        }
    }
}
