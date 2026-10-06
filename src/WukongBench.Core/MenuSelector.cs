namespace WukongBench.Core;

public enum SelectionDirection { Left, Right }

public static class MenuSelector
{
    // Clicking focuses the row. This build changes values with Left/Right, not a dropdown.
    public static async Task Select(string label, string target, int width, OcrPage initial,
        Func<OcrWord, Task<OcrPage>> activate, Func<SelectionDirection, Task<OcrPage>> step)
    {
        string ReadValue(OcrPage page)
        {
            var row = MenuParser.FindRow(page, label, width)
                ?? throw new InvalidDataException("Setting disappeared while selecting " + label);
            string value = MenuParser.Value(page, row, width).Trim();
            if (value.Length == 0) throw new InvalidDataException("Cannot read " + label + "; selection stopped.");
            return value;
        }
        bool Matches(string value) => value.Equals(target, StringComparison.OrdinalIgnoreCase);
        string current = ReadValue(initial);
        if (Matches(current)) return;
        var initialRow = MenuParser.FindRow(initial, label, width)!;
        current = ReadValue(await activate(MenuParser.Control(initial, initialRow, width)));
        if (Matches(current)) return;
        var observed = new List<string> { current };
        foreach (var direction in new[] { SelectionDirection.Right, SelectionDirection.Left })
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current };
            for (int i = 0; i < 12; i++)
            {
                current = ReadValue(await step(direction));
                observed.Add(current);
                if (Matches(current)) return;
                // An unchanged value marks an endpoint; a repeated value marks a full cycle.
                if (!seen.Add(current)) break;
            }
        }
        throw new InvalidDataException($"Cannot select {target} for {label} with Left/Right. Observed: {string.Join(" -> ", observed)}");
    }
}
