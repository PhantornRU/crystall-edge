using Robust.Shared.Prototypes;

namespace Content.Server._CE.Animals.Components;

/// <summary>Time spent alive and fed before this animal permanently grows up.</summary>
[RegisterComponent]
public sealed partial class CEAnimalGrowthComponent : Component
{
    // A remaining duration needs no pause offset: paused entities are not updated.
    [DataField]
    public TimeSpan Remaining = TimeSpan.FromMinutes(15);

    [DataField(required: true)]
    public EntProtoId GrowInto;

    [DataField]
    public float MinimumSatiation = 25f;
}
