using Content.Server._CE.Animals.Components;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Mind;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Robust.Shared.Containers;

namespace Content.Server._CE.Animals.Systems;

public sealed partial class CEAnimalGrowthSystem : EntitySystem
{
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private MobThresholdSystem _thresholds = default!;
    [Dependency] private SatiationSystem _satiation = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Update(float frameTime)
    {
        if (frameTime <= 0 || !float.IsFinite(frameTime))
            return;

        List<Entity<CEAnimalGrowthComponent>>? ready = null;
        var query = EntityQueryEnumerator<CEAnimalGrowthComponent, SatiationComponent>();
        while (query.MoveNext(out var uid, out var growth, out var satiation))
        {
            if (!CanGrow((uid, growth), satiation))
                continue;

            growth.Remaining -= TimeSpan.FromSeconds(frameTime);
            if (growth.Remaining <= TimeSpan.Zero)
                (ready ??= new()).Add((uid, growth));
        }

        if (ready == null)
            return;

        // Spawning/deleting outside the query avoids invalidating its enumerator.
        foreach (var animal in ready)
        {
            if (TerminatingOrDeleted(animal))
                continue;

            // Failed replacements retry at most once per second of eligible time.
            animal.Comp.Remaining = TimeSpan.FromSeconds(1);
            TryGrow(animal);
        }
    }

    private bool CanGrow(Entity<CEAnimalGrowthComponent> animal, SatiationComponent satiation)
    {
        if (TerminatingOrDeleted(animal) || !_mobState.IsAlive(animal) ||
            _containers.TryGetContainingContainer(animal.Owner, out _) ||
            _satiation.GetValueOrNull((animal.Owner, satiation), SatiationSystem.Hunger) is not { } hunger ||
            !float.IsFinite(hunger) || hunger < animal.Comp.MinimumSatiation)
            return false;

        return !TryComp<CEAnimalForagingComponent>(animal, out var foraging) ||
               !foraging.Exhausted && !foraging.Eating && !_doAfter.IsRunning(foraging.EatingDoAfter);
    }

    private void TryGrow(Entity<CEAnimalGrowthComponent> animal)
    {
        if (!TryComp<SatiationComponent>(animal, out var satiation) || !CanGrow(animal, satiation) ||
            !TryComp<TransformComponent>(animal, out var transform) || transform.MapUid == null)
            return;

        EntityUid adult;
        try
        {
            adult = Spawn(animal.Comp.GrowInto, _transform.GetMapCoordinates(animal, transform),
                rotation: _transform.GetWorldRotation(transform));
        }
        catch (Exception exception)
        {
            Log.Warning($"Could not grow {ToPrettyString(animal)}: {exception.Message}");
            return;
        }

        if (!TryTransferState(animal, adult, satiation))
        {
            QueueDel(adult);
            return;
        }

        var metadata = MetaData(animal);
        if (metadata.EntityPrototype != null && metadata.EntityName != metadata.EntityPrototype.Name)
            _metadata.SetEntityName(adult, metadata.EntityName);

        if (_mind.TryGetMind(animal, out var mindId, out var mind))
        {
            _mind.TransferTo(mindId, adult, mind: mind);
            if (mind.OwnedEntity != adult || TerminatingOrDeleted(adult))
            {
                _mind.TransferTo(mindId, animal.Owner, mind: mind);
                QueueDel(adult);
                return;
            }
        }

        QueueDel(animal);
    }

    private bool TryTransferState(EntityUid source, EntityUid adult, SatiationComponent sourceSatiation)
    {
        if (!TryComp<DamageableComponent>(adult, out var damageable) ||
            !_thresholds.GetScaledDamage(source, adult, out var damage) || damage == null ||
            !TryComp<SatiationComponent>(adult, out var adultSatiation))
            return false;

        _damageable.SetDamage((adult, damageable), damage);
        foreach (var type in sourceSatiation.Satiations.Keys)
        {
            if (!adultSatiation.Has(type) ||
                _satiation.GetValueOrNull((source, sourceSatiation), type) is not { } value || !float.IsFinite(value))
                return false;

            _satiation.SetValue((adult, adultSatiation), type, value);
        }

        return !TerminatingOrDeleted(source) && !TerminatingOrDeleted(adult);
    }
}
