namespace Content.Shared._CE.EntityEffect.Conditions;

/// <summary>
/// Filters known food using the user's animal-foraging policy and selected target.
/// The server foraging system handles the condition; the shared definition holds no runtime state.
/// </summary>
public sealed partial class CECanEatNearby : CEEntityConditionBase<CECanEatNearby>;
