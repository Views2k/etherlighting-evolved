using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;

namespace EtherlightingEvolved.Native.Pages.Effects;

internal sealed class EffectPreview : Control
{
    readonly EffectClock motionClock = new(), colorClock = new();
    readonly bool optical;
    readonly string id;
    readonly EffectAnimationState[] animation;
    readonly double[,] colors;
    readonly ImmutableSolidColorBrush[] brushes;
    readonly JsonObject?[] opticalSettings;
    readonly double[] brightnessScale;
    readonly int rows, columns;
    readonly double gap, lampWidth, lampHeight, radius;
    JsonObject? config, cachedParameters;
    bool parametersChangeWithTime, uniform, first = true, settled;
    double master, gamma;
    bool native;

    internal EffectPreview(string effect, int count, bool optical)
    {
        this.optical = optical;
        id = effect;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        var initial = Color.FromRgb(50, 54, 65);
        animation = Enumerable.Range(0, count).Select(_ => new EffectAnimationState()).ToArray();
        colors = new double[count, 3];
        brushes = Enumerable.Repeat(new ImmutableSolidColorBrush(initial), count).ToArray();
        opticalSettings = optical ? new JsonObject?[count] : [];
        brightnessScale = new double[count];
        (rows, columns) = count > 10 ? PortTopology.PreviewGrid(count) : (1, count);
        gap = count > 10 ? 4 : optical ? 16 : 7;
        lampWidth = optical && count <= 10 ? 16 : 10;
        lampHeight = count > 10 ? 14 : optical ? 24 : 33;
        radius = optical && count <= 10 ? 5 : 3;
        Width = columns * lampWidth + (columns - 1) * gap;
        Height = rows * lampHeight + (rows - 1) * gap;
    }

    internal double ReservedHeight(bool compact) => brushes.Length > 10 ? Math.Max(67, rows * 18 + 22) : compact ? 36 : 67;

    internal void Configure(JsonObject state, JsonObject draft, int[] ports, IReadOnlyDictionary<int, JsonObject> sfp, JsonObject? next)
    {
        var nextMaster = Math.Clamp(draft.Num("brightness") ?? state.Num("brightness") ?? 100, 0, 100);
        var nextGamma = state.Num("output.gamma") ?? 2.2;
        var nextNative = optical && SfpSyncModel.SfpNativeAvailable(state);
        if (master != nextMaster || gamma != nextGamma || native != nextNative) settled = false;
        (master, gamma, native) = (nextMaster, nextGamma, nextNative);
        if (!optical && !JsonNode.DeepEquals(config, next))
        {
            config = next;
            cachedParameters = EffectRenderer.ResolvedColorParams(config, 0);
            var synced = EffectRenderer.SynchronizedBreathing(config);
            parametersChangeWithTime = !synced && EffectRenderer.EffectById(id).Objects("params")
                .Any(p => p.Str("random_param") is { } key && cachedParameters.Bool(key) == true);
            uniform = id is "static" or "color_cycle" or "strobe" or "daylight"
                || id == "breathing" && (synced || cachedParameters.Bool("ripple") != true);
            settled = false;
        }
        for (var i = 0; i < brushes.Length; i++)
        {
            int port = i < ports.Length ? ports[i] : 0;
            if (optical)
            {
                var settings = SfpSyncModel.SfpCandidateSettings(state, port, id, draft);
                if (!JsonNode.DeepEquals(settings, opticalSettings[i])) settled = false;
                opticalSettings[i] = settings;
            }
            var scale = sfp.TryGetValue(port, out var light)
                ? Math.Clamp(draft.Num($"sfp.{port}.brightness") ?? light.Num("brightness") ?? 100, 0, 100) / 100 : 1;
            var brightness = master / 100 * scale;
            if (brightnessScale[i] != brightness) settled = false;
            brightnessScale[i] = brightness;
        }
    }

    internal void Paint(double time, double elapsed, bool still,
        double? hour, IReadOnlyList<JsonNode?>? traffic, IReadOnlyList<JsonObject?> livePorts)
    {
        if (settled && !parametersChangeWithTime && id is "static" or "steady") return;
        var phase = optical ? 0 : EffectRenderer.PreviewPhase(motionClock, config, time);
        var colorPhase = optical ? 0 : EffectRenderer.PreviewColorPhase(colorClock, config, time);
        var parameters = parametersChangeWithTime ? EffectRenderer.ResolvedColorParams(config, time, colorPhase) : cachedParameters;
        double peak = traffic is null ? 0 : EffectRenderer.ActivityPeak(traffic, config.Bool("params.include_uplinks") == true);
        double blend = still || first ? 1 : 1 - Math.Exp(-Math.Clamp(elapsed, 0, 80) / 42);
        bool ready = true;
        double[]? shared = null;
        bool changed = false;
        for (var i = 0; i < brushes.Length; i++)
        {
            double[] target;
            double scale = 1;
            if (optical) target = EffectGallery.SfpPreviewColor(opticalSettings[i], id, time, master, gamma, native);
            else if (id == "daylight" && hour is null)
                target = PortDisplay.PortDisplayRgb(i < livePorts.Count ? livePorts[i] : null, gamma);
            else
            {
                scale = brightnessScale[i];
                if (shared is not null) target = shared;
                else
                {
                    var facts = new EffectFacts(traffic is not null && i < traffic.Count ? traffic[i] : null)
                    {
                        ResolvedParams = parameters,
                        Phase = phase,
                        ColorPhase = colorPhase,
                        Hour = hour,
                        PeakTraffic = peak,
                        AnimationState = animation[i],
                    };
                    var result = EffectRenderer.EffectColor(config, i, brushes.Length, time, facts);
                    target = result as double[] ?? EffectRenderer.Rgb(result);
                    if (uniform) shared = target;
                }
            }
            for (int channel = 0; channel < 3; channel++)
            {
                var desired = Math.Clamp(target[channel], 0, 255) * scale;
                colors[i, channel] += (desired - colors[i, channel]) * blend;
                if (Math.Abs(colors[i, channel] - desired) > 0.001) ready = false;
                else colors[i, channel] = desired;
            }
            var color = Color.FromRgb(Channel(colors[i, 0]), Channel(colors[i, 1]), Channel(colors[i, 2]));
            if (brushes[i].Color == color) continue;
            brushes[i] = i > 0 && brushes[i - 1].Color == color ? brushes[i - 1] : new ImmutableSolidColorBrush(color);
            changed = true;
        }
        first = false;
        settled = ready;
        if (changed) InvalidateVisual();
    }

    static byte Channel(double value) => (byte)Math.Clamp(Math.Floor(value + 0.5), 0, 255);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var count = brushes.Length;
        double width = Math.Clamp((Bounds.Width - gap * (columns - 1)) / columns, 1, lampWidth);
        double x = (Bounds.Width - columns * width - (columns - 1) * gap) / 2;
        for (var i = 0; i < count; i++)
        {
            var rect = new Rect(x + i % columns * (width + gap), i / columns * (lampHeight + gap), width, lampHeight);
            context.DrawRectangle(brushes[i], null, rect, radius, radius);
        }
    }
}
