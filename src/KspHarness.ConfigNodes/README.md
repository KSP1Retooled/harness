# KspHarness.ConfigNodes

Reads, writes and queries KSP config files (`.cfg`, `.craft`, `.sfs`, `settings.cfg`, `ModuleManager.ConfigCache`).
It targets netstandard2.0 and has no dependencies, and the driver mod compiles the same sources in.

```csharp
var pod = ConfigFile.Load(@"GameData\Squad\Parts\Command\mk1pod_v2\mk1Pod_v2.cfg");
int crew = pod.SelectValues("PART[mk1pod*]/MODULE[ModuleCommand]/minimumCrew")[0].As<int>();

var db = ConfigDatabase.LoadModuleManagerCache(@"GameData\ModuleManager.ConfigCache");
List<CfgNode> engines = db.Select("PART:HAS[@MODULE[ModuleEngines*]:HAS[#maxThrust[>100]]]");
UrlConfig config = db.SelectConfigs("PART[mk1pod_v2]")[0];   // config.File, config.Url

var save = ConfigFile.Load(@"saves\default\persistent.sfs");
var landed = save.Select("GAME/FLIGHTSTATE/VESSEL:HAS[#sit[LANDED]]");
```

## Parsing and writing

`ConfigParser.Parse` follows KSP 1.12.5's `ConfigNode.Parse` exactly: comments start at the first `//` on a line
(inside values too), every `{` and `}` is its own token wherever it is, a value splits at the first `=`, a word not
followed by `{` is dropped, and a stray top-level `}` ends the file. A file is a root node named `root`.

`ConfigWriter.WriteRoot` / `ConfigFile.Save` write KSP's layout (tabs, braces on their own lines). Braces in values
become `[ ]`, as KSP's writer does, so the text always reads back to the same tree.

## Typed values

`node.Value<T>(key)`, `node.Values<T>(key)`, `node.TryGetValue<T>(key, out v)`, `node.ValueOr(key, fallback)` and
`value.As<T>()` read KSP's text forms: integers and floats in the invariant culture, `True`/`False` in any case, enums
by name as `Enum.Parse` does, `double[]`/`float[]`/`int[]` from `x,y,z` (commas, spaces or tabs), `string[]` from
comma lists, `Guid` and `CfgColor` (`r,g,b` or `r,g,b,a`).

## Queries

ModuleManager's node syntax. Segments are separated by `/` (children) or `//` (any depth).

| Form | Meaning |
|---|---|
| `PART` | nodes named `PART` (`*` and `?` wildcards, case-sensitive) |
| `PART[mk1pod*]` | ... whose `name` value matches; `a\|b` or `a,b` for alternatives |
| `PART,1` / `PART,-1` / `PART,*` | the 2nd / last / every match among siblings; no index means every match |
| `:HAS[@MODULE[x]]` / `:HAS[!MODULE[x]]` | has / lacks a matching child node |
| `:HAS[#key]`, `:HAS[#key[pattern]]` | has the key / a value matching the pattern |
| `:HAS[#key[>100]]`, `:HAS[#key[<1]]` | has a value numerically above / below |
| `:HAS[~key]`, `:HAS[~key[]]`, `:HAS[~key[pattern]]` | lacks the key / lacks a matching value |
| `:HAS[a,b]`, `:HAS[@X:HAS[...]]` | AND; nested conditions apply to the matched child |
| last segment `key` or `#key` (`SelectValues`) | the values, each with its key, text and owning node |

Inside `:HAS[...]` all whitespace is removed, as in ModuleManager: match spaces with `?`.

## Live KSP nodes

`ConfigQuery` runs over any node type through `INodeAdapter<TNode>`, so the mod queries KSP's `ConfigNode`s in
place, without copying:

```csharp
sealed class KspNodes : INodeAdapter<ConfigNode>
{
    public string GetName(ConfigNode n) => n.name;
    public int GetValueCount(ConfigNode n) => n.values.Count;
    public string GetValueKey(ConfigNode n, int i) => n.values[i].name;
    public string GetValueText(ConfigNode n, int i) => n.values[i].value;
    public int GetNodeCount(ConfigNode n) => n.nodes.Count;
    public ConfigNode GetNode(ConfigNode n, int i) => n.nodes[i];
}

var query = ConfigQuery.Parse("PART:HAS[@MODULE[ModuleEngines*]]");
var parts = query.SelectNodes(GameDatabase.Instance.root.AllConfigs.Select(c => c.config), new KspNodes());
```

## Tests

`dotnet test tests/KspHarness.ConfigNodes.Tests`. The tests on a stock 1.12.5 install's files run when `KSP_DIR`
names one, and the tests on a `ModuleManager.ConfigCache` run when `KSPHARNESS_TEST_MM_CACHE` names one. They only
read those files.
