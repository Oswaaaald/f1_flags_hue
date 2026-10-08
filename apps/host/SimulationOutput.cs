using F1Hue.Core;
using F1Hue.Infrastructure;

namespace F1Hue.Host;

public sealed class SimulationOutput : IEffectOutput
{
    public static readonly HueInventory Inventory = new([new("11111111-1111-1111-1111-111111111111", "Lampe salon", true, "/lights/1"), new("22222222-2222-2222-2222-222222222222", "Ruban TV", true, "/lights/2")], [new("33333333-3333-3333-3333-333333333333", "Salon", "room", ["11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"], "/groups/1")], []);
    public Task CaptureAsync(AppSettings settings, CancellationToken ct) => Task.CompletedTask;
    public Task ApplyAsync(RaceFlag flag, EffectSpec effect, AppSettings settings, CancellationToken ct) => Task.CompletedTask;
    public Task EndAnimationAsync(CancellationToken ct) => Task.CompletedTask;
    public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
}
