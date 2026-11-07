using Content.Server._CE.Animals.Components;
using Content.Shared._CE.Animals;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._CE.Animals.Systems;

/// <summary>Adulthood changes appearance and adds production; the body and all its state stay intact.</summary>
public sealed partial class CEAnimalGrowthSystem : EntitySystem
{
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SatiationSystem _satiation = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Update(float frameTime)
    {
        var elapsed = TimeSpan.FromSeconds(frameTime);
        var query = EntityQueryEnumerator<CEAnimalGrowthComponent, SatiationComponent>();
        while (query.MoveNext(out var uid, out var growth, out var satiation))
        {
            if (!_mobState.IsAlive(uid) || _containers.IsEntityInContainer(uid)
                || HasComp<CEAnimalExhaustedComponent>(uid)
                || _satiation.GetValueOrNull((uid, satiation), SatiationSystem.Hunger) is not { } hunger
                || hunger < growth.MinimumSatiation)
                continue;

            growth.Remaining -= elapsed;
            if (growth.Remaining > TimeSpan.Zero)
                continue;

            EntityManager.AddComponents(uid, growth.AdultComponents, removeExisting: false);
            _appearance.SetData(uid, CEAnimalVisuals.Grown, true);

            var metadata = MetaData(uid);
            var adult = _prototypes.Index(growth.AdultPrototype);
            if (metadata.EntityName == metadata.EntityPrototype?.Name)
                _metadata.SetEntityName(uid, adult.Name);
            if (metadata.EntityDescription == metadata.EntityPrototype?.Description)
                _metadata.SetEntityDescription(uid, adult.Description);

            RemCompDeferred<CEAnimalGrowthComponent>(uid);
        }
    }
}
