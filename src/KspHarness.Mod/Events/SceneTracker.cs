using KspHarness.Mod.Protocol;

namespace KspHarness.Mod.Events;

/// <summary>Follows scene loads, reports them as <c>scene</c> events and says when a scene is ready.</summary>
internal sealed class SceneTracker
{
    private readonly EventHub hub;
    private volatile bool loading;
    private GameScenes previous = GameScenes.LOADING;

    // KSP drops handlers whose delegate has no target, so these must be instance methods
    public SceneTracker(EventHub hub)
    {
        this.hub = hub;
        GameEvents.onGameSceneLoadRequested.Add(OnLoadRequested);
        GameEvents.onLevelWasLoadedGUIReady.Add(OnLoaded);
    }

    public static SceneTracker Instance { get; set; }

    /// <summary>Whether a scene load was requested and has not finished.</summary>
    public bool Loading => loading;

    public void Stop()
    {
        GameEvents.onGameSceneLoadRequested.Remove(OnLoadRequested);
        GameEvents.onLevelWasLoadedGUIReady.Remove(OnLoaded);
    }

    /// <summary>Whether <paramref name="scene"/> is loaded, its load finished and (in flight) a vessel is active.</summary>
    public static bool IsReady(GameScenes scene)
    {
        if ((Instance != null && Instance.Loading) || HighLogic.LoadedScene != scene)
            return false;

        if (scene != GameScenes.FLIGHT)
            return true;

        return FlightGlobals.ready && FlightGlobals.fetch != null && FlightGlobals.ActiveVessel != null;
    }

    private void OnLoadRequested(GameScenes to)
    {
        loading = true;
        Publish(HighLogic.LoadedScene, to, "requested");
    }

    private void OnLoaded(GameScenes scene)
    {
        loading = false;
        Publish(previous, scene, "loaded");
        previous = scene;
    }

    private void Publish(GameScenes from, GameScenes to, string phase)
    {
        hub.Publish(
            "scene",
            new JsonObject
            {
                { "from", from.ToString() },
                { "to", to.ToString() },
                { "phase", phase },
            }
        );
    }
}
