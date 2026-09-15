using System.IO;
using Game.BackendServer;

internal static class Program
{
    private static void Main()
    {
        Run("empty legacy state keeps defaults", EmptyLegacyStateKeepsDefaults);
        Run("malformed resources fail closed", () => RequireCorrupt(() => BackendDatabase.DeserializeResourceState(42, "{")));
        Run("null resources fail closed", () => RequireCorrupt(() => BackendDatabase.DeserializeResourceState(42, "null")));
        Run("malformed appearance fails closed", () => RequireCorrupt(() => BackendDatabase.DeserializeAppearanceState(42, "{")));
        Run("null appearance fails closed", () => RequireCorrupt(() => BackendDatabase.DeserializeAppearanceState(42, "null")));
        Run("malformed presentation fails closed", () => RequireCorrupt(() => BackendDatabase.DeserializePresentationState(42, "{")));
        Run("null presentation fails closed", () => RequireCorrupt(() => BackendDatabase.DeserializePresentationState(42, "null")));
        Run("malformed progression fails closed", () => RequireCorrupt(() => BackendDatabase.DeserializeProgressionState(42, "{")));
        Run("null progression fails closed", () => RequireCorrupt(() => BackendDatabase.DeserializeProgressionState(42, "null")));
        Run("invalid progression fails closed", InvalidProgressionFailsClosed);
        Console.WriteLine("All GatewayServer persistence-safety regression tests passed.");
    }

    private static void EmptyLegacyStateKeepsDefaults()
    {
        Require(BackendDatabase.DeserializeResourceState(42, string.Empty).Length == 0,
            "legacy empty resource state should remain supported");
        Require(BackendDatabase.DeserializeAppearanceState(42, string.Empty) != null,
            "legacy empty appearance should create the canonical default");
        Require(BackendDatabase.DeserializePresentationState(42, string.Empty) != null,
            "legacy empty presentation should create the canonical default");
        Require(BackendDatabase.DeserializeProgressionState(42, string.Empty) != null,
            "legacy empty progression should create the canonical default");
    }

    private static void InvalidProgressionFailsClosed()
    {
        const string invalid = "{\"revision\":0,\"level\":0,\"experience\":0,\"tracks\":[]}";
        RequireCorrupt(() => BackendDatabase.DeserializeProgressionState(42, invalid));
    }

    private static void RequireCorrupt(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException ex)
        {
            Require(ex.Message.Contains("Character 42"), "corruption error should identify the character");
            Require(ex.Message.Contains("Refusing to substitute defaults"),
                "corruption error should make the fail-closed policy explicit");
            return;
        }

        throw new InvalidOperationException("corrupt non-empty persisted state was silently accepted");
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {name}: {ex.Message}");
            Environment.ExitCode = 1;
            throw;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
