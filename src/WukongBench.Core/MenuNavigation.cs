namespace WukongBench.Core;

public static class MenuNavigation
{
    private static readonly string[] Labels =
    [
        "super resolution", "super resolution sampling", "frame generation", "full ray tracing",
        "full ray tracing level", "graphics preset", "view distance", "anti-aliasing", "post-effects",
        "shadow quality", "texture quality", "visual effect", "hair quality", "global illumination",
        "reflection quality", "vegetation quality"
    ];

    private static Dictionary<string, OcrLine> VisibleRows(OcrPage page, int width) => Labels
        .Select(label => (Label: label, Row: MenuParser.FindRow(page, label, width)))
        .Where(p => p.Row is not null).ToDictionary(p => p.Label, p => p.Row!);

    public static OcrWord ScrollAnchor(OcrPage page, int width, int height) => VisibleRows(page, width).Values
        .Select(row => row.Words[0])
        .Where(word => word.CenterX > width * .15 && word.CenterX < width * .60
            && word.CenterY > height * .1 && word.CenterY < height * .85)
        .OrderBy(word => Math.Abs(word.CenterY - height * .5)).FirstOrDefault()
        ?? throw new InvalidDataException("Cannot locate the graphics list for scrolling; no input was sent to the help panel.");

    public static bool Moved(OcrPage before, OcrPage after, int width)
    {
        var previous = VisibleRows(before, width);
        var current = VisibleRows(after, width);
        if (!previous.Keys.ToHashSet().SetEquals(current.Keys)) return true;
        return previous.Any(p => Math.Abs(p.Value.Words[0].CenterY - current[p.Key].Words[0].CenterY) > 4);
    }

    public static async Task<(OcrPage Page, OcrLine Line)> FindRow(string label, int width, int height,
        OcrPage initial, Func<int, OcrWord, Task<OcrPage>> scroll)
    {
        var found = MenuParser.FindRow(initial, label, width);
        if (found is not null) return (initial, found);
        var page = await scroll(2400, ScrollAnchor(initial, width, height));
        for (int i = 0; i < 12; i++)
        {
            found = MenuParser.FindRow(page, label, width);
            if (found is not null) return (page, found);
            var next = await scroll(-240, ScrollAnchor(page, width, height));
            found = MenuParser.FindRow(next, label, width);
            if (found is not null) return (next, found);
            if (!Moved(page, next, width))
                throw new InvalidDataException($"Cannot locate setting: {label}. Graphics list did not move or reached its end.");
            page = next;
        }
        throw new InvalidDataException("Cannot locate setting after 12 graphics list scrolls: " + label);
    }
}
