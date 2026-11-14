using System;
using System.Collections;
using System.Diagnostics;
using KspHarness.Mod.Events;
using KspHarness.Mod.Protocol;
using KspHarness.Mod.Runtime;
using KspHarness.Mod.Values;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KspHarness.Mod;

/// <summary>
/// The mod's one persistent object: owns the socket server and the event sources, and forwards Update,
/// FixedUpdate, LateUpdate and the end of the frame to the scheduler that snippets run on.
/// </summary>
internal sealed class HarnessHost : MonoBehaviour
{
    private EventHub hub;
    private LogCapture logs;
    private SceneTracker scenes;
    private DialogWatcher dialogs;
    private RunManager runs;
    private RpcServer server;
    private volatile bool quitRequested;

    public static HarnessHost Instance { get; private set; }

    /// <summary>The handles of the connected client, for code that runs outside any snippet.</summary>
    public HandleRegistry ActiveHandles => server?.Active?.Handles;

    public static void Create(int port, string token)
    {
        var host = new GameObject("KspHarness").AddComponent<HarnessHost>();
        DontDestroyOnLoad(host.gameObject);
        host.Begin(port, token);
    }

    private void Begin(int port, string token)
    {
        Instance = this;
        Scheduler.Initialize();
        GameClock.Advance();

        hub = new EventHub();
        logs = new LogCapture(hub);
        logs.Start();
        scenes = new SceneTracker(hub);
        SceneTracker.Instance = scenes;
        dialogs = new DialogWatcher(hub);
        DialogWatcher.Current = dialogs;
        runs = new RunManager();
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        server = new RpcServer(port, token, OnHello, OnRequest, OnClosed);
        try
        {
            server.Start();
        }
        catch (Exception e)
        {
            Debug.LogError($"[KspHarness] Cannot listen on 127.0.0.1:{port}: {e.Message}");
            return;
        }

        StartCoroutine(EndOfFrameLoop());
        Debug.Log($"[KspHarness] Listening on 127.0.0.1:{port}");
    }

    private void Update()
    {
        GameClock.Advance();
        ModAttribution.Refresh();
        runs.CheckDeadlines();
        Scheduler.Tick(TickPoint.Update);
        dialogs.Update();
    }

    private void FixedUpdate() => Scheduler.Tick(TickPoint.FixedUpdate);

    private void LateUpdate() => Scheduler.Tick(TickPoint.LateUpdate);

    private IEnumerator EndOfFrameLoop()
    {
        var endOfFrame = new WaitForEndOfFrame();
        while (true)
        {
            yield return endOfFrame;
            Scheduler.Tick(TickPoint.EndOfFrame);
            if (quitRequested)
            {
                quitRequested = false;
                Application.Quit();
            }
        }
    }

    private void OnDestroy()
    {
        server?.Stop();
        logs?.Stop();
        scenes?.Stop();
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        if (Instance == this)
        {
            Instance = null;
            DialogWatcher.Current = null;
        }
    }

    // Reader thread: the hello facts are read on the main thread, and only then does the client go live
    private void OnHello(Request request)
    {
        Scheduler.Post(() =>
        {
            Connection connection = request.Connection;
            if (!connection.IsOpen)
                return;

            server.Activate(connection);
            hub.GoLive(connection, cursor => connection.SendResult(request.Id, HelloResult(cursor)));
        });
    }

    // Reader thread
    private void OnRequest(Request request)
    {
        switch (request.Method)
        {
            case "load":
                Guard(request, () => runs.Load(request));
                break;
            case "run":
                Scheduler.Post(() => Guard(request, () => runs.Start(request)));
                break;
            case "cancel":
                Scheduler.Post(() => Guard(request, () => runs.Cancel(request)));
                break;
            case "release":
                Scheduler.Post(() => Guard(request, () => runs.Release(request)));
                break;
            case "quit":
                Scheduler.Post(() =>
                {
                    request.Connection.SendResult(request.Id, new JsonObject());
                    quitRequested = true;
                });
                break;
            case "hello":
                request.Connection.SendError(request.Id, ErrorCodes.InvalidRequest, "Already authenticated");
                break;
            default:
                request.Connection.SendError(request.Id, ErrorCodes.MethodNotFound, $"No method \"{request.Method}\"");
                break;
        }
    }

    // Any thread
    private void OnClosed(Connection connection)
    {
        hub.Drop(connection);
        Scheduler.Post(() =>
        {
            runs.Abandon(connection);
            connection.Handles.Clear();
        });
    }

    private static void Guard(Request request, Action action)
    {
        try
        {
            action();
        }
        catch (RpcException e)
        {
            request.Connection.SendError(request.Id, e.Code, e.Message, e.Data);
        }
        catch (Exception e)
        {
            request.Connection.SendError(request.Id, ErrorCodes.InternalError, e.Message, RunManager.ExceptionData(e));
        }
    }

    private JsonObject HelloResult(long logCursor)
    {
        GameScenes scene = HighLogic.LoadedScene;
        return new JsonObject
        {
            { "protocol", RpcServer.Protocol },
            { "pid", Process.GetCurrentProcess().Id },
            { "ksp", $"{Versioning.version_major}.{Versioning.version_minor}.{Versioning.Revision}" },
            { "retooled", IsRetooled() },
            { "unity", Application.unityVersion },
            { "scene", scene.ToString() },
            { "loaded", scene != GameScenes.LOADING && scene != GameScenes.LOADINGBUFFER && !scenes.Loading },
            { "logCursor", logCursor },
        };
    }

    private static bool IsRetooled()
    {
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name.StartsWith("KSP.Retooled", StringComparison.Ordinal))
                return true;
        }

        return typeof(HighLogic).Assembly.GetType("KSP.Retooled.Modding.ModRelinker") != null;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Only an exception thrown on a thread other than the main one gets here. It arrives with IsTerminating set, yet
        // the player keeps running (measured on stock 1.12.5), so the event says only that it happened; the flush is
        // for the process that does go down
        try
        {
            hub.Publish("crashing", new JsonObject { { "message", e.ExceptionObject?.ToString() } });
            hub.Live?.WaitForFlush(500);
        }
        catch (Exception)
        {
            // Throwing from this handler would only replace the exception being reported
        }
    }
}
