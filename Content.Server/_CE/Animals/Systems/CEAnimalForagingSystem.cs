using Content.Server._CE.Animals.Components;
using Content.Server._CE.GOAP;
using Content.Server._CE.GOAP.Actions;
using Content.Server._CE.GOAP.Perceptors;
using Content.Server._CE.GOAP.Steering;
using Content.Server.Animals.Components;
using Content.Server.Animals.Systems;
using Content.Shared._CE.Cooking.Components;
using Content.Shared._CE.DoAfter;
using Content.Shared._CE.EntityEffect;
using Content.Shared._CE.EntityEffect.Conditions;
using Content.Shared._CE.GOAP;
using Content.Shared._CE.GOAP.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Nutrition;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Standing;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Server._CE.Animals.Systems;

/// <summary>One ordinary ingestion attempt after the GOAP orchestrator approaches its target.</summary>
public sealed partial class CEGOAPEatAction : CEGOAPActionBase<CEGOAPEatAction>;

public sealed partial class CEAnimalForagingSystem : CEGOAPActionSystem<CEGOAPEatAction>
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SatiationSystem _satiation = default!;
    [Dependency] private IngestionSystem _ingestion = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private StandingStateSystem _standing = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private MovementSpeedModifierSystem _speed = default!;
    [Dependency] private CEGOAPSteeringSystem _steering = default!;
    [Dependency] private SatiationProductionSystem _production = default!;
    [Dependency] private TurfSystem _turf = default!;
    [Dependency] private NpcFactionSystem _factions = default!;
    [Dependency] private EntityQuery<CEAnimalForagingComponent> _foragingQuery = default!;
    [Dependency] private EntityQuery<SatiationComponent> _satiationQuery = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesBefore.Add(typeof(CEGOAPSystem));
    }

    [SubscribeLocalEvent]
    private void OnMapInit(Entity<CEAnimalForagingComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.NextGraze = _timing.CurTime + ent.Comp.GrazeInterval;
        ent.Comp.NextEggAttempt = _timing.CurTime + ent.Comp.EggAttemptInterval;
    }

    [SubscribeLocalEvent]
    private void OnSatiationChanged(Entity<CEAnimalForagingComponent> ent, ref SatiationUpdateEvent args)
    {
        if (args.Type == SatiationSystem.Hunger)
            RefreshState(ent);
    }

    private float? Hunger(EntityUid uid)
    {
        return _satiationQuery.TryComp(uid, out var satiation)
            ? _satiation.GetValueOrNull((uid, satiation), SatiationSystem.Hunger)
            : null;
    }

    private void RefreshState(Entity<CEAnimalForagingComponent> ent)
    {
        if (Hunger(ent) is not { } hunger)
            return;

        var comp = ent.Comp;
        var hungry = hunger < comp.HungryThreshold;
        var exhausted = comp.Exhausted ? hunger < comp.RecoveryThreshold : hunger <= comp.ExhaustionThreshold;
        var changed = comp.Hungry != hungry || comp.Exhausted != exhausted
            || HasComp<CEAnimalHungryComponent>(ent) != hungry;
        var wasDown = HasComp<CEAnimalExhaustedComponent>(ent);
        comp.Hungry = hungry;
        comp.Exhausted = exhausted;

        if (hungry)
            EnsureComp<CEAnimalHungryComponent>(ent);
        else
            RemComp<CEAnimalHungryComponent>(ent);

        if (exhausted)
        {
            EnsureComp<CEAnimalExhaustedComponent>(ent);
            _standing.Down(ent, playSound: false, force: true);
            if (!wasDown)
                _steering.Stop(ent.Owner);
        }
        else
        {
            RemComp<CEAnimalExhaustedComponent>(ent);
            if (wasDown && _mobState.IsAlive(ent))
                _standing.Stand(ent);
        }

        if (changed || wasDown != exhausted)
        {
            _blocker.UpdateCanMove(ent);
            _speed.RefreshMovementSpeedModifiers(ent.Owner);
        }
    }

    [SubscribeLocalEvent]
    private void OnCanMove(Entity<CEAnimalForagingComponent> ent, ref UpdateCanMoveEvent args)
    {
        if (ent.Comp.Exhausted)
            args.Cancel();
    }

    [SubscribeLocalEvent]
    private void OnStandAttempt(Entity<CEAnimalForagingComponent> ent, ref StandAttemptEvent args)
    {
        if (ent.Comp.Exhausted)
            args.Cancel();
    }

    [SubscribeLocalEvent]
    private void OnSpeed(Entity<CEAnimalForagingComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (ent.Comp.Exhausted)
            args.ModifySpeed(0f);
        else if (ent.Comp.Hungry)
            args.ModifySpeed(ent.Comp.SearchSpeedModifier);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<CEAnimalForagingComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (now < comp.NextUpdate)
                continue;

            comp.NextUpdate = now + comp.UpdateInterval;
            var ent = new Entity<CEAnimalForagingComponent>(uid, comp);
            RefreshState(ent);
            if (!_mobState.IsAlive(uid) || _containers.IsEntityInContainer(uid) || Hunger(uid) == null)
            {
                StopEating(ent);
                comp.SelectedFood = null;
                continue;
            }

            if (now >= comp.NextEggAttempt)
            {
                comp.NextEggAttempt = now + comp.EggAttemptInterval;
                if (!comp.Hungry && !comp.Exhausted && TryComp<SatiationProductionComponent>(uid, out var production))
                    _production.TryProduce((uid, production), out _);
            }

            if (!HasComp<CEActiveGOAPComponent>(uid) || !comp.Hungry || CurrentAction(uid) is CEGOAPFleeAction)
            {
                StopEating(ent);
                comp.SelectedFood = null;
                continue;
            }

            if (comp.Eating)
            {
                if (!comp.BiteCompleted && (comp.SelectedFood is not { } food || !IsFood(ent, food, comp.EatRange)))
                    StopEating(ent);
                continue;
            }

            if (comp.SelectedFood is { } selected && !IsFood(ent, selected, SearchRange(comp)))
                comp.SelectedFood = null;

            // An approach can fail before Startup/Shutdown are called. Bound that attempt here.
            if (comp.SelectedFood is { } stalled && now >= comp.ApproachUntil)
            {
                comp.FailedFood = stalled;
                comp.RetryAt = now + comp.RetryDelay;
                comp.SelectedFood = null;
            }

            if (comp.SelectedFood == null)
            {
                comp.SelectedFood = Goap.ResolveTarget(uid, comp.FoodTarget).Entity;
                comp.ApproachUntil = now + comp.ApproachTimeout;
            }
        }
    }

    private CEGOAPAction? CurrentAction(EntityUid uid)
    {
        return TryComp<CEGOAPComponent>(uid, out var goap)
            && goap.CurrentActionIndex < goap.CurrentPlan.Count
            ? goap.CurrentPlan[goap.CurrentActionIndex] : null;
    }

    private float SearchRange(CEAnimalForagingComponent comp) => comp.Exhausted ? comp.EatRange : comp.SearchRadius;

    private bool IsFood(Entity<CEAnimalForagingComponent> ent, EntityUid food, float range)
    {
        if (food == ent.Owner || TerminatingOrDeleted(food) || _containers.IsEntityInContainer(food)
            || !TryComp<EdibleComponent>(food, out var edible) || !TryComp<CEFoodTagComponent>(food, out var tags)
            || !Transform(ent).Coordinates.TryDistance(EntityManager, Transform(food).Coordinates, out var distance)
            || distance > range || !_interaction.InRangeAndAccessible(ent.Owner, food, range)
            || _ingestion.IsEmpty((food, edible)))
            return false;

        foreach (var tag in tags.Tags)
        {
            if (ent.Comp.FoodTags.Contains(tag))
                return _ingestion.CanIngest(ent, food);
        }

        return false;
    }

    [SubscribeLocalEvent]
    private void OnFoodCondition(ref CEEntityConditionEvent<CECanEatNearby> args)
    {
        if (args.Args.Resolve(CEEffectTarget.User) is not { } user || !_foragingQuery.TryComp(user, out var comp)
            || (comp.SelectedFood is { } selected && selected != args.Entity)
            || (comp.FailedFood == args.Entity && _timing.CurTime < comp.RetryAt))
            return;

        args.Result = IsFood((user, comp), args.Entity, SearchRange(comp));
    }

    protected override void OnCanExecute(Entity<CEGOAPComponent> ent, ref CEGOAPActionCanExecuteEvent<CEGOAPEatAction> args)
    {
        args.CanExecute = _foragingQuery.TryComp(ent, out var comp) && comp.Hungry && _mobState.IsAlive(ent)
            && !_containers.IsEntityInContainer(ent) && comp.SelectedFood is { } food
            && IsFood((ent.Owner, comp), food, SearchRange(comp));
    }

    protected override void OnActionStartup(Entity<CEGOAPComponent> ent, ref CEGOAPActionStartupEvent<CEGOAPEatAction> args)
    {
        if (!_foragingQuery.TryComp(ent, out var comp))
            return;

        comp.Eating = true;
        comp.EatingDoAfter = null;
        comp.BiteSucceeded = false;
        comp.BiteCompleted = false;
        comp.StartingBite = true;
        if (comp.SelectedFood is not { } food || !IsFood((ent.Owner, comp), food, comp.EatRange)
            || !_ingestion.TryIngest(ent.Owner, ent.Owner, food))
            comp.BiteCompleted = true;
        comp.StartingBite = false;
    }

    [SubscribeLocalEvent]
    private void OnDoAfterStarted(Entity<CEAnimalForagingComponent> ent, ref CEDoAfterStartedEvent args)
    {
        if (ent.Comp.StartingBite && args.DoAfter.Args.Event is EatingDoAfterEvent
            && args.DoAfter.Args.User == ent.Owner && args.DoAfter.Args.EventTarget == ent.Owner
            && args.DoAfter.Args.Target == ent.Comp.SelectedFood)
            ent.Comp.EatingDoAfter = args.DoAfter.Id;
    }

    [SubscribeLocalEvent]
    private void OnIngesting(Entity<CEAnimalForagingComponent> ent, ref IngestingEvent args)
    {
        if (ent.Comp.Eating && !args.ForceFed && args.Food == ent.Comp.SelectedFood && args.Split.Volume > FixedPoint2.Zero)
            ent.Comp.BiteSucceeded = true;
    }

    [SubscribeLocalEvent]
    private void OnBeforeIngested(Entity<CEAnimalForagingComponent> ent, ref BeforeIngestedEvent args)
    {
        // Ingestion's usual DoAfter reach is 1 tile; enforce our tighter range at the actual bite too.
        if (ent.Comp.Eating && ent.Comp.SelectedFood is { } food
            && TryComp<EdibleComponent>(food, out var edible)
            && _solutions.TryGetSolution(food, edible.Solution, out _, out var solution)
            && ReferenceEquals(args.Solution, solution) && !IsFood(ent, food, ent.Comp.EatRange))
            args.Cancelled = true;
    }

    [SubscribeLocalEvent(after: [typeof(IngestionSystem)])]
    private void OnEatingCompleted(Entity<CEAnimalForagingComponent> ent, ref EatingDoAfterEvent args)
    {
        if (!ent.Comp.Eating || args.User != ent.Owner || args.Target != ent.Comp.SelectedFood
            || (ent.Comp.EatingDoAfter is { } id && args.DoAfter.Id != id))
            return;

        // A single bite, including the zero-delay path which never raises CEDoAfterStartedEvent.
        args.Repeat = false;
        ent.Comp.BiteCompleted = true;
    }

    protected override void OnActionUpdate(Entity<CEGOAPComponent> ent, ref CEGOAPActionUpdateEvent<CEGOAPEatAction> args)
    {
        if (!_foragingQuery.TryComp(ent, out var comp) || !comp.Eating)
            args.Status = CEGOAPActionStatus.Failed;
        else if (comp.BiteCompleted)
            args.Status = comp.BiteSucceeded ? CEGOAPActionStatus.Finished : CEGOAPActionStatus.Failed;
        else if (!_doAfter.IsRunning(comp.EatingDoAfter))
            args.Status = CEGOAPActionStatus.Failed;
    }

    protected override void OnActionShutdown(Entity<CEGOAPComponent> ent, ref CEGOAPActionShutdownEvent<CEGOAPEatAction> args)
    {
        if (!_foragingQuery.TryComp(ent, out var comp))
            return;

        if (comp.BiteCompleted && !comp.BiteSucceeded)
        {
            comp.FailedFood = comp.SelectedFood;
            comp.RetryAt = _timing.CurTime + comp.RetryDelay;
        }

        StopEating((ent.Owner, comp));
        comp.SelectedFood = null;
    }

    private void StopEating(Entity<CEAnimalForagingComponent> ent)
    {
        if (_doAfter.IsRunning(ent.Comp.EatingDoAfter))
            _doAfter.Cancel(ent.Comp.EatingDoAfter);
        ent.Comp.Eating = false;
        ent.Comp.StartingBite = false;
        ent.Comp.EatingDoAfter = null;
        ent.Comp.BiteCompleted = false;
        ent.Comp.BiteSucceeded = false;
    }

    [SubscribeLocalEvent]
    private void OnFoodTerminating(Entity<EdibleComponent> food, ref EntityTerminatingEvent args)
    {
        // Cancel before native ingestion can try to access the other bird's deleted target.
        var query = AllEntityQuery<CEAnimalForagingComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.SelectedFood == food.Owner && _doAfter.IsRunning(comp.EatingDoAfter))
                StopEating((uid, comp));
        }
    }

    [SubscribeLocalEvent(after: [typeof(CEGOAPExploreActionSystem)])]
    private void OnExploreUpdate(Entity<CEAnimalForagingComponent> ent, ref CEGOAPActionUpdateEvent<CEGOAPExploreAction> args)
    {
        if (args.Status == CEGOAPActionStatus.Running)
            TryGraze(ent);
    }

    private void TryGraze(Entity<CEAnimalForagingComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.Exhausted || comp.Eating || comp.SelectedFood != null || _timing.CurTime < comp.NextGraze
            || !_mobState.IsAlive(ent) || _containers.IsEntityInContainer(ent)
            || CurrentAction(ent) is not CEGOAPExploreAction || Goap.ResolveTarget(ent, comp.ThreatTarget).Entity != null
            || !TryComp<CEGOAPSteeringComponent>(ent, out var steering) || steering.Target != null
            || Hunger(ent) is not { } hunger || hunger >= comp.GrazeLimit
            || !TryComp<PhysicsComponent>(ent, out var body) || body.LinearVelocity.LengthSquared() > 0.01f
            || _turf.GetTileRef(Transform(ent).Coordinates) is not { } tile
            || !comp.GrazingTiles.Contains(_turf.GetContentTileDefinition(tile).ID)
            || !_satiationQuery.TryComp(ent, out var satiation))
            return;

        comp.NextGraze = _timing.CurTime + comp.GrazeInterval;
        _satiation.ModifyValue((ent.Owner, satiation), SatiationSystem.Hunger, MathF.Min(comp.GrazeNutrition, comp.GrazeLimit - hunger));
    }

    [SubscribeLocalEvent(after: [typeof(CEGOAPPainPerceptorSystem)])]
    private void OnDamaged(Entity<CEAnimalForagingComponent> ent, ref DamageDealtEvent args)
    {
        if (args.Origin is not { } source || source == ent.Owner || TerminatingOrDeleted(source)
            || !_mobState.IsAlive(source) || !TryComp<CEGOAPComponent>(ent, out var goap))
            return;

        foreach (var amount in args.Damage.DamageDict.Values)
        {
            if (amount <= FixedPoint2.Zero)
                continue;
            ent.Comp.LastThreat = source;
            ent.Comp.ThreatUntil = _timing.CurTime + ent.Comp.ThreatMemory;
            Goap.Remember((ent.Owner, goap), source, Transform(source).Coordinates, ent.Comp.ThreatMemory);
            break;
        }
    }

    [SubscribeLocalEvent]
    private void OnThreatCondition(ref CEEntityConditionEvent<CEAnimalThreat> args)
    {
        if (args.Args.Resolve(CEEffectTarget.User) is not { } user || user == args.Entity
            || !_foragingQuery.TryComp(user, out var comp) || TerminatingOrDeleted(args.Entity)
            || !_mobState.IsAlive(args.Entity) || Transform(user).MapID != Transform(args.Entity).MapID)
            return;

        if (comp.LastThreat == args.Entity && _timing.CurTime < comp.ThreatUntil)
        {
            args.Result = true;
            return;
        }

        args.Result = TryComp<NpcFactionMemberComponent>(user, out var ownFaction)
            && TryComp<NpcFactionMemberComponent>(args.Entity, out var otherFaction)
            && !_factions.IsEntityFriendly((user, ownFaction), (args.Entity, otherFaction))
            && _factions.IsMemberOfAny((args.Entity, otherFaction), ownFaction.HostileFactions);
    }

    [SubscribeLocalEvent]
    private void OnShutdown(Entity<CEAnimalForagingComponent> ent, ref ComponentShutdown args)
    {
        StopEating(ent);
        if (TerminatingOrDeleted(ent))
            return;

        var wasExhausted = ent.Comp.Exhausted;
        ent.Comp.Hungry = false;
        ent.Comp.Exhausted = false;
        RemComp<CEAnimalHungryComponent>(ent);
        RemComp<CEAnimalExhaustedComponent>(ent);
        _blocker.UpdateCanMove(ent);
        _speed.RefreshMovementSpeedModifiers(ent.Owner);
        if (wasExhausted && _mobState.IsAlive(ent))
            _standing.Stand(ent);
    }
}
