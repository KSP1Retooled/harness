namespace KspHarness.Installs;

/// <summary>
/// One definition's own steps. Each layer gets its own instance, built from the starting install with the steps of
/// every layer up to and including this one.
/// </summary>
public sealed class InstallLayer
{
    internal InstallLayer(InstallDefinition definition, IReadOnlyList<InstallStep> steps, PopupChoices popupChoices)
    {
        Definition = definition;
        Steps = steps;
        PopupChoices = popupChoices;
        Popups = PopupSettings.Merge(null, popupChoices);
    }

    public InstallDefinition Definition { get; }

    /// <summary>The key of this layer in lock files.</summary>
    public string DefinitionId => Definition.GetType().FullName ?? Definition.GetType().Name;

    public IReadOnlyList<InstallStep> Steps { get; }

    public IEnumerable<CkanStep> CkanSteps => Steps.OfType<CkanStep>();

    public IEnumerable<ArchiveStep> ArchiveSteps => Steps.OfType<ArchiveStep>();

    /// <summary>Popup choices merged with the included definitions' ones.</summary>
    public PopupSettings Popups { get; internal set; }

    internal PopupChoices PopupChoices { get; }

    public IReadOnlyList<string> Fingerprints => Steps.Select(step => step.Fingerprint).ToList();
}

/// <summary>A definition resolved into layers, the outermost included definition first.</summary>
public sealed class InstallPlan
{
    private InstallPlan(IReadOnlyList<InstallLayer> layers)
    {
        Layers = layers;
    }

    public IReadOnlyList<InstallLayer> Layers { get; }

    /// <summary>The popup settings of the definition itself (the last layer).</summary>
    public PopupSettings Popups => Layers[^1].Popups;

    public static InstallPlan Create(InstallDefinition definition)
    {
        var layers = new List<InstallLayer>();
        var seen = new HashSet<Type>();
        InstallDefinition? current = definition;

        while (current is not null)
        {
            if (!seen.Add(current.GetType()))
            {
                throw new InvalidOperationException($"definition {current.GetType().Name} includes itself");
            }

            var builder = new InstallBuilder();
            current.Configure(builder);
            layers.Add(new InstallLayer(current, builder.Steps.ToList(), builder.Popups));
            current = builder.Included is null ? null : (InstallDefinition)Activator.CreateInstance(builder.Included)!;
        }

        layers.Reverse();
        PopupSettings? included = null;
        foreach (var layer in layers)
        {
            layer.Popups = PopupSettings.Merge(included, layer.PopupChoices);
            included = layer.Popups;
        }

        return new InstallPlan(layers);
    }
}
