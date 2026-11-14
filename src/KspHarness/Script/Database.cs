#nullable disable

using System.Collections.Generic;

namespace KspHarness.Script;

/// <summary>Queries the live game database. <c>Nodes</c> returns KSP's <c>ConfigNode</c>s in the game.</summary>
public static class Database
{
    public static List<string> Query(string query) => throw OutsideGame.Error();

    public static List<string> Values(string query) => throw OutsideGame.Error();

    public static List<dynamic> Nodes(string query) => throw OutsideGame.Error();
}

/// <summary>Lets the config query language run over KSP's <c>ConfigNode</c> in the game.</summary>
public sealed class ConfigNodeAdapter
{
    public static readonly ConfigNodeAdapter Instance = null;

    private ConfigNodeAdapter() { }

    public string GetName(object node) => throw OutsideGame.Error();

    public int GetValueCount(object node) => throw OutsideGame.Error();

    public string GetValueKey(object node, int index) => throw OutsideGame.Error();

    public string GetValueText(object node, int index) => throw OutsideGame.Error();

    public int GetNodeCount(object node) => throw OutsideGame.Error();

    public dynamic GetNode(object node, int index) => throw OutsideGame.Error();
}
