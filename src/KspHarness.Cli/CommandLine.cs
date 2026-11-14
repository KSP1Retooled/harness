namespace KspHarness.Cli;

/// <summary>A command line that does not fit the command; exits with code 2 and the usage text.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// The arguments of one command: options that take a value (<c>--name N</c> or <c>--name=N</c>), flags, and
/// positional arguments, in order. Options may appear anywhere; <c>--</c> ends them.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string> values;
    private readonly HashSet<string> flags;

    private CommandLine(Dictionary<string, string> values, HashSet<string> flags, List<string> positionals)
    {
        this.values = values;
        this.flags = flags;
        Positionals = positionals;
    }

    public IReadOnlyList<string> Positionals { get; }

    /// <summary>
    /// Parses <paramref name="args"/> against the options the command knows. Names include their dashes
    /// (<c>"--name"</c>, <c>"-e"</c>); an unknown option, a repeated one or a missing value is a
    /// <see cref="UsageException"/>.
    /// </summary>
    public static CommandLine Parse(
        IEnumerable<string> args,
        IReadOnlyCollection<string>? valueOptions = null,
        IReadOnlyCollection<string>? flagOptions = null
    )
    {
        valueOptions ??= [];
        flagOptions ??= [];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var positionals = new List<string>();

        using var enumerator = args.GetEnumerator();
        var optionsEnded = false;
        while (enumerator.MoveNext())
        {
            var arg = enumerator.Current;
            if (optionsEnded || !IsOption(arg))
            {
                positionals.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            var name = arg;
            string? inlineValue = null;
            var equals = arg.IndexOf('=');
            if (equals > 0 && arg.StartsWith("--", StringComparison.Ordinal))
            {
                name = arg[..equals];
                inlineValue = arg[(equals + 1)..];
            }

            if (flagOptions.Contains(name))
            {
                if (inlineValue is not null)
                {
                    throw new UsageException($"{name} takes no value");
                }

                flags.Add(name);
                continue;
            }

            if (!valueOptions.Contains(name))
            {
                throw new UsageException($"unknown option {name}");
            }

            if (values.ContainsKey(name))
            {
                throw new UsageException($"{name} is given twice");
            }

            if (inlineValue is null)
            {
                if (!enumerator.MoveNext())
                {
                    throw new UsageException($"{name} needs a value");
                }

                inlineValue = enumerator.Current;
            }

            values[name] = inlineValue;
        }

        return new CommandLine(values, flags, positionals);
    }

    public bool Has(string flag) => flags.Contains(flag);

    public string? Value(string option) => values.GetValueOrDefault(option);

    public string Required(string option)
    {
        return Value(option) ?? throw new UsageException($"{option} is required");
    }

    public int? Int(string option)
    {
        var text = Value(option);
        if (text is null)
        {
            return null;
        }

        return int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new UsageException($"{option} needs a positive whole number, not '{text}'");
    }

    /// <summary>A comma-separated list option.</summary>
    public IReadOnlyList<string> List(string option)
    {
        var text = Value(option);
        return text is null
            ? []
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Fails when more positional arguments were given than the command takes.</summary>
    public CommandLine AtMost(int count)
    {
        if (Positionals.Count > count)
        {
            throw new UsageException($"unexpected argument '{Positionals[count]}'");
        }

        return this;
    }

    // A lone "-" names standard input in many tools, and negative numbers are values, so neither is an option.
    private static bool IsOption(string arg)
    {
        return arg.Length > 1 && arg[0] == '-' && !char.IsDigit(arg[1]);
    }
}
