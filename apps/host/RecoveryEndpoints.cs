using F1Hue.Core;
using F1Hue.Infrastructure;

namespace F1Hue.Host;

public static class RecoveryEndpoints
{
    public static void Map(RouteGroupBuilder api, IEffectOutput output, Runner runner, HueClient hue, Store store,
        IBridgeVault vault, AutomaticStartup startup, Action changed)
    {
        HueOutput RequireRecovery()
        {
            if (runner.State.Running || runner.State.Stopping)
                throw new InvalidOperationException("Attends la fin du mode actif.");
            if (output is not HueOutput real || !real.RecoveryPending)
                throw new InvalidOperationException("Aucune récupération en attente.");
            startup.Cancel();
            return real;
        }
        api.MapPost("/recovery/pair", async (PairRequest body, CancellationToken ct) =>
        {
            RequireRecovery();
            var expected = store.Get<string>("recovery_bridge") ?? vault.Read()?.BridgeId
                ?? throw new InvalidOperationException("L’identité de l’ancien pont n’est pas disponible. Restaure la liaison sauvegardée, ou abandonne explicitement la récupération.");
            await hue.PairAsync(body.Ip, ct, expected);
            changed();
            return Results.Ok(new
            {
                ok = true
            });
        });
        api.MapPost("/recovery/available", async (CancellationToken ct) =>
        {
            var real = RequireRecovery();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            await real.RestoreAvailableAsync(deadline.Token);
            if (!real.RecoveryPending)
                runner.RecoveryResolved();
            changed();
            return Results.Ok(real.RecoveryStatus());
        });
        api.MapPost("/recovery/abandon", async (RecoveryAbandonRequest body, CancellationToken ct) =>
        {
            var real = RequireRecovery();
            if (body.Confirmation != "ABANDONNER")
                throw new ArgumentException("Confirme avec ABANDONNER. Les lampes ne seront plus restaurées automatiquement et les ressources temporaires pourront rester sur le pont.");
            var archive = await real.AbandonRecoveryAsync(ct);
            runner.RecoveryResolved();
            changed();
            return Results.Ok(new
            {
                archive = Path.GetFileName(archive)
            });
        });
    }
}
