using System.Dynamic;

namespace KspHarness;

/// <summary>
/// An object that stays in the game. Pass it back into a snippet (a captured variable or an argument) to use it there;
/// the snippet then sees the real object with its in-game type.
/// </summary>
public sealed class Handle : DynamicObject
{
    internal Handle(long id, string type, string? assembly, string? text, GameSession? session)
    {
        Id = id;
        Type = type;
        Assembly = assembly;
        Text = text;
        Session = session;
    }

    public long Id { get; }

    /// <summary>The full name of the object's type in the game.</summary>
    public string Type { get; }

    /// <summary>The simple name of the assembly that declares <see cref="Type"/>.</summary>
    public string? Assembly { get; }

    /// <summary>The object's <c>ToString()</c> in the game when the handle was made, shortened.</summary>
    public string? Text { get; }

    internal GameSession? Session { get; }

    /// <summary>Lets the game drop the object. Using the handle afterwards fails in the game.</summary>
    public Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (Session is null)
        {
            return Task.CompletedTask;
        }

        return Session.ReleaseAsync([this], cancellationToken);
    }

    public override string ToString() => $"handle {Id}: {Type} ({Text})";

    public override bool TryGetMember(GetMemberBinder binder, out object? result) => throw UsedOutside(binder.Name);

    public override bool TrySetMember(SetMemberBinder binder, object? value) => throw UsedOutside(binder.Name);

    public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
    {
        throw UsedOutside(binder.Name);
    }

    public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object? result)
    {
        throw UsedOutside("[]");
    }

    public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object? value)
    {
        throw UsedOutside("[]");
    }

    public override bool TryInvoke(InvokeBinder binder, object?[]? args, out object? result) => throw UsedOutside("()");

    public override bool TryBinaryOperation(BinaryOperationBinder binder, object arg, out object? result)
    {
        throw UsedOutside(binder.Operation.ToString());
    }

    public override bool TryUnaryOperation(UnaryOperationBinder binder, out object? result)
    {
        throw UsedOutside(binder.Operation.ToString());
    }

    public override bool TryConvert(ConvertBinder binder, out object? result)
    {
        throw UsedOutside("a conversion to " + binder.Type.Name);
    }

    private InvalidOperationException UsedOutside(string member)
    {
        return new InvalidOperationException(
            $"'{member}' was used on {this} outside the game. The object lives in the game: move the code that uses "
            + "it into a Game.Run lambda, where the handle becomes the real object.");
    }
}
