using Content.Server._CE.Animals.Components;
using Content.Shared.Body;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Mind;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._CE.Animals.Systems;

public sealed partial class CEAnimalGrowthSystem : EntitySystem
{
    private static readonly ProtoId<ReagentPrototype> Nutriment = "CENutriment";

    [Dependency] private BodySystem _body = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private MobThresholdSystem _thresholds = default!;
    [Dependency] private SatiationSystem _satiation = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Update(float frameTime)
    {
        var elapsed = TimeSpan.FromSeconds(frameTime);
        List<(Entity<CEAnimalGrowthComponent> Animal, float Hunger)>? ready = null;
        var query = EntityQueryEnumerator<CEAnimalGrowthComponent, SatiationComponent, CEAnimalForagingComponent>();
        while (query.MoveNext(out var uid, out var growth, out var satiation, out var foraging))
        {
            if (!_mobState.IsAlive(uid) || _containers.IsEntityInContainer(uid)
                || HasComp<CEAnimalExhaustedComponent>(uid) || foraging.BiteStatus != null
                || _satiation.GetValueOrNull((uid, satiation), SatiationSystem.Hunger) is not { } hunger
                || hunger < growth.MinimumSatiation)
                continue;

            growth.Remaining -= elapsed;
            if (growth.Remaining <= TimeSpan.Zero)
                (ready ??= new()).Add(((uid, growth), hunger));
        }

        if (ready == null)
            return;

        // Spawning/deleting outside the query avoids invalidating its enumerator.
        foreach (var (animal, hunger) in ready)
        {
            if (TerminatingOrDeleted(animal))
                continue;

            // Waiting for digestion retries at most once per second of eligible time.
            animal.Comp.Remaining = TimeSpan.FromSeconds(1);
            TryGrow(animal, hunger);
        }
    }

    private void TryGrow(Entity<CEAnimalGrowthComponent> animal, float hunger)
    {
        var transform = Transform(animal);
        if (transform.MapUid == null || HasPendingNutrition(animal))
            return;

        EntityUid adult;
        try
        {
            adult = Spawn(animal.Comp.GrowInto, _transform.GetMapCoordinates(animal, transform),
                rotation: _transform.GetWorldRotation(transform));
        }
        catch (EntityCreationException exception)
        {
            AbortGrowth(animal, exception.ToString());
            return;
        }

        if (!TryTransferState(animal, adult, hunger))
        {
            AbortGrowth(animal, "The adult cannot receive the chick's state.", adult);
            return;
        }

        var metadata = MetaData(animal);
        if (metadata.EntityPrototype != null && metadata.EntityName != metadata.EntityPrototype.Name)
            _metadata.SetEntityName(adult, metadata.EntityName);

        if (_mind.TryGetMind(animal, out var mindId, out var mind))
        {
            try
            {
                _mind.TransferTo(mindId, adult, mind: mind);
            }
            catch (ArgumentException exception)
            {
                if (mind.OwnedEntity != animal.Owner)
                    _mind.TransferTo(mindId, animal.Owner, mind: mind);
                AbortGrowth(animal, exception.Message, adult);
                return;
            }
        }

        QueueDel(animal);
    }

    private void AbortGrowth(Entity<CEAnimalGrowthComponent> animal, string reason, EntityUid? adult = null)
    {
        QueueDel(adult);
        Log.Error($"Could not grow {ToPrettyString(animal)} into {animal.Comp.GrowInto}: {reason}");
        RemComp<CEAnimalGrowthComponent>(animal);
    }

    private bool HasPendingNutrition(EntityUid animal)
    {
        // A completed bite can still be in any stage of digestion. Let native metabolism finish
        // before deleting the old body and its organs; blood and unrelated reagents do not delay growth.
        foreach (var stomach in _body.EnumerateOrgans<StomachComponent>(animal))
        {
            if (HasNutrition(stomach.Owner, StomachSystem.DefaultSolutionName))
                return true;
        }

        return TryComp<BloodstreamComponent>(animal, out var blood) &&
               (HasNutrition(animal, blood.BloodSolutionName) || HasNutrition(animal, blood.MetabolitesSolutionName));
    }

    private bool HasNutrition(EntityUid uid, string solutionName) =>
        _solutions.TryGetSolution(uid, solutionName, out _, out var solution) &&
        solution.GetTotalPrototypeQuantity(Nutriment) > FixedPoint2.Zero;

    private bool TryTransferState(EntityUid source, EntityUid adult, float hunger)
    {
        if (!TryComp<DamageableComponent>(adult, out var damageable) ||
            !_thresholds.GetScaledDamage(source, adult, out var damage) || damage == null ||
            !TryComp<SatiationComponent>(adult, out var adultSatiation) || !adultSatiation.Has(SatiationSystem.Hunger))
            return false;

        _damageable.SetDamage((adult, damageable), damage);
        _satiation.SetValue((adult, adultSatiation), SatiationSystem.Hunger, hunger);
        return true;
    }
}
