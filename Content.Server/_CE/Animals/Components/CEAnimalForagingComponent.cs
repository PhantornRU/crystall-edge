using Content.Shared._CE.Cooking.Prototypes;
using Content.Shared._CE.GOAP.Prototypes;
using Content.Shared.DoAfter;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._CE.Animals.Components;

/// <summary>Local feeding policy and per-animal state; no second hunger or fatigue meter.</summary>
[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class CEAnimalForagingComponent : Component
{
    [DataField]
    public ProtoId<CEGOAPTargetPrototype> ThreatTarget = "Threat";

    [DataField(required: true)]
    public HashSet<ProtoId<CEFoodTagPrototype>> FoodTags = new();

    [DataField] public float HungryThreshold = 60f;
    [DataField] public float ExhaustionThreshold = 10f;
    [DataField] public float RecoveryThreshold = 60f;
    [DataField] public TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);
    [DataField] public TimeSpan EatAttemptInterval = TimeSpan.FromSeconds(1);
    [DataField] public TimeSpan ThreatMemory = TimeSpan.FromSeconds(6);
    [DataField] public TimeSpan GrazeInterval = TimeSpan.FromSeconds(12);
    [DataField] public float GrazeNutrition = 2f;
    [DataField] public float GrazeLimit = 50f;
    [DataField] public HashSet<string> GrazingTiles = new();

    // Only the animal's current ingestion attempt; GOAP never pursues food.
    [DataField] public EntityUid? TrackedFood;
    [DataField] public EntityUid? LastThreat;
    [DataField] public DoAfterId? EatingDoAfter;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextUpdate;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextEatAttempt;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan ThreatUntil;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextGraze;
}

/// <summary>Observed by the existing GOAP has-component sensor.</summary>
[RegisterComponent]
public sealed partial class CEAnimalHungryComponent : Component;

/// <summary>Latched until the existing Hunger satiation reaches RecoveryThreshold.</summary>
[RegisterComponent]
public sealed partial class CEAnimalExhaustedComponent : Component;
