using GameInterface.Configuration;

namespace E2E.Tests.Environment.Mock;

/// <summary>
/// Fixed mod-config for every E2E instance. The real <see cref="ModConfig"/> discovers the HOSTING
/// MACHINE's CoopData/mod-config.json, so tests would follow whatever file the developer's live
/// server happens to contain — and on CI, where no file or template exists, they would load an
/// empty <see cref="ModConfigData"/> and fail <c>ModConfigAuthority.InitializeHost</c>'s
/// birthAndDeath preflight. Everything else deliberately stays at documented defaults.
/// </summary>
internal sealed class DeterministicModConfig : IModConfig
{
    public ModConfigData Data { get; } = new ModConfigData
    {
        Difficulty = new DifficultyConfigData
        {
            BirthAndDeath = true,
        },
    };
}
