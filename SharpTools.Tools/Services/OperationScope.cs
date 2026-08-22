namespace SharpTools.Tools.Services;

// Tools derive their modified solution from the CurrentSolution they read first; remembering that read per invocation
// (AsyncLocal flows into everything the tool awaits) lets ApplyChangesAsync diff against the exact base instead of guessing.
public static class OperationScope {
    private static readonly AsyncLocal<StrongBox<Solution?>?> Current = new();

    public static IDisposable Begin() {
        Current.Value = new StrongBox<Solution?>();
        return new Ender();
    }

    internal static void Observe(Solution? solution) {
        if (solution != null && Current.Value is { Value: null } box) {
            box.Value = solution;
        }
    }

    internal static Solution? ObservedBase => Current.Value?.Value;

    internal static void Forget() {
        if (Current.Value is { } box) {
            box.Value = null;
        }
    }

    private sealed class Ender : IDisposable {
        public void Dispose() => Current.Value = null;
    }
}
